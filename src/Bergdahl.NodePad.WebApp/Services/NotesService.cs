using System.IO.Compression;
using System.Text.RegularExpressions;
using Bergdahl.NodePad.WebApp.Models;

namespace Bergdahl.NodePad.WebApp.Services;

/// <summary>
/// All file system work on the notes tree: Markdown files (.md) with tags in a sibling .meta file.
/// Used by both the REST API and the MCP tools. Paths are relative to PagesDirectory.
/// Expected failures are reported as <see cref="NotesException"/>.
/// </summary>
public class NotesService
{
    private readonly string rootPath;
    private readonly string backupRootPath;
    private readonly ILogger<NotesService> _logger;

    /// <summary>Full path of the pages folder.</summary>
    public string RootPath => rootPath;

    public NotesService(IConfiguration configuration, ILogger<NotesService> logger)
    {
        _logger = logger;
        rootPath = configuration.GetValue<string>("PagesDirectory")
                   ?? Path.Combine(Directory.GetCurrentDirectory(), "Pages");

        var configuredBackup = configuration.GetValue<string>("BackupDirectory");
        backupRootPath = string.IsNullOrWhiteSpace(configuredBackup)
            ? Path.Combine(Directory.GetCurrentDirectory(), "Backups")
            : (Path.IsPathRooted(configuredBackup)
                ? configuredBackup
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuredBackup)));

        // Säkerställ att katalogerna finns
        if (!Directory.Exists(rootPath))
        {
            Directory.CreateDirectory(rootPath);
        }
        if (!Directory.Exists(backupRootPath))
        {
            Directory.CreateDirectory(backupRootPath);
        }
    }

    // ---------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------

    public List<FileSystemEntity> GetStructure(bool sorted = false, bool dirsFirst = true, string? tags = null, bool includeCounts = false, bool includeTitles = false)
    {
        var structure = GetDirectoryStructure(rootPath).ToList();

        // Optional tag filtering
        var required = ParseTags(tags);
        if (required.Count > 0)
        {
            var index = BuildNormalizedTagsIndex();
            structure = FilterTreeByTags(structure, required, index).ToList();
        }

        // Optional sorting
        if (sorted)
        {
            structure = SortTree(structure, dirsFirst).ToList();
        }

        // Optional enrichment counts for directories
        if (includeCounts)
        {
            structure = AttachCounts(structure).nodes;
        }

        // Optional title enrichment for files
        if (includeTitles)
        {
            structure = AttachTitles(structure).ToList();
        }

        return structure;
    }

    public bool NoteExists(string path)
    {
        return File.Exists(RequireFilePath(path));
    }

    public string ReadContent(string path)
    {
        var filePath = RequireFilePath(path);
        if (!File.Exists(filePath))
            throw NotesException.NotFound();
        return File.ReadAllText(filePath);
    }

    /// <summary>
    /// Tags as stored in the .meta file (display form, not normalized).
    /// </summary>
    public string[] ReadTags(string path)
    {
        var filePath = RequireFilePath(path);
        return ReadTagsFromMeta(filePath);
    }

    public List<Breadcrumb> GetBreadcrumbs(string path)
    {
        var filePath = RequireFilePath(path);
        if (!File.Exists(filePath))
            throw NotesException.NotFound();
        return BuildBreadcrumbs(RelPath(filePath).Replace('\\', '/'));
    }

    public PageInfo GetInfo(string path)
    {
        var filePath = RequireFilePath(path);
        if (!File.Exists(filePath))
            throw NotesException.NotFound();

        var content = string.Empty;
        try { content = File.ReadAllText(filePath); } catch { /* ignore */ }
        var fallbackName = Path.GetFileNameWithoutExtension(filePath) ?? "";
        var title = ExtractTitle(content, fallbackName);

        var tagsDisplay = ReadTagsFromMeta(filePath);
        var fi = new FileInfo(filePath);
        var sizeBytes = fi.Exists ? fi.Length : 0L;

        return new PageInfo(title, tagsDisplay, fi.LastWriteTimeUtc, sizeBytes);
    }

    public List<SearchHit> Search(string query, string? tags = null, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            throw NotesException.BadRequest("Query must be at least 2 characters long");
        }

        // Normalize inputs
        var q = query.Trim();
        var qLower = q.ToLowerInvariant();
        if (limit < 1) limit = 1;
        if (limit > 200) limit = 200;

        var requiredTags = ParseTags(tags);

        var results = new List<SearchHit>();
        var files = EnumerateNoteFiles();
        foreach (var file in files)
        {
            string rel = Path.GetRelativePath(rootPath, file).Replace("\\", "/");
            string name = Path.GetFileNameWithoutExtension(file);

            string content = string.Empty;
            try { content = File.ReadAllText(file); } catch { /* ignore unreadable files */ }

            // Read tags for optional filtering
            if (requiredTags.Count > 0)
            {
                try
                {
                    var fileTags = ReadTagsFromMeta(file)
                        .Select(NormalizeTag)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    bool hasAll = requiredTags.All(t => fileTags.Contains(t));
                    if (!hasAll) continue;
                }
                catch { continue; }
            }

            // Compute matches
            bool nameMatch = name.Contains(q, StringComparison.OrdinalIgnoreCase);
            bool pathMatch = rel.Contains(q, StringComparison.OrdinalIgnoreCase);

            // Title: first H1 if present, otherwise filename
            string title = name;
            try
            {
                title = ExtractTitle(content, name);
            }
            catch { /* ignore */ }
            bool titleMatch = !string.IsNullOrEmpty(title) && title.Contains(q, StringComparison.OrdinalIgnoreCase);

            // Content matches: count occurrences and first index
            int contentFirstIdx = -1;
            int contentCount = 0;
            if (!string.IsNullOrEmpty(content))
            {
                try
                {
                    var lower = content.ToLowerInvariant();
                    int pos = 0;
                    while (true)
                    {
                        pos = lower.IndexOf(qLower, pos, StringComparison.Ordinal);
                        if (pos < 0) break;
                        contentCount++;
                        if (contentFirstIdx < 0) contentFirstIdx = pos;
                        pos += qLower.Length;
                    }
                }
                catch { /* ignore */ }
            }

            bool anyMatch = nameMatch || pathMatch || titleMatch || contentCount > 0;
            if (!anyMatch) continue;

            // Scoring
            int score = 0;
            if (nameMatch) score += 50;
            if (pathMatch) score += 20;
            if (titleMatch) score += 40;
            if (contentCount > 0) score += Math.Min(10, contentCount) * 5; // cap contribution
            if (contentFirstIdx >= 0)
            {
                int bonus = 30 - Math.Min(30, contentFirstIdx / 50); // earlier match -> higher score
                if (bonus > 0) score += bonus;
            }

            // Build snippet and highlight ranges
            string snippet = string.Empty;
            var highlights = new List<SearchHighlight>();

            if (contentFirstIdx >= 0 && !string.IsNullOrEmpty(content))
            {
                int start = Math.Max(0, contentFirstIdx - 40);
                int len = Math.Min(content.Length - start, 200);
                var raw = content.Substring(start, len);
                // Replace newlines with spaces but keep indices consistent (1 char -> 1 char)
                raw = raw.Replace('\n', ' ').Replace('\r', ' ');

                // Add ellipsis if trimmed; a leading ellipsis shifts highlight positions by 1
                bool prefixed = start > 0;
                bool suffixed = (start + len) < content.Length;
                int shift = prefixed ? 1 : 0;

                // Find highlight ranges within the raw snippet
                var rawLower = raw.ToLowerInvariant();
                int s = 0;
                while (true)
                {
                    s = rawLower.IndexOf(qLower, s, StringComparison.Ordinal);
                    if (s < 0) break;
                    highlights.Add(new SearchHighlight(s + shift, q.Length));
                    s += qLower.Length;
                }

                snippet = (prefixed ? "…" : string.Empty) + raw + (suffixed ? "…" : string.Empty);
            }
            else if (nameMatch)
            {
                var prefix = "Match in file name: ";
                snippet = $"{prefix}{name}.md";
                int idx = name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    highlights.Add(new SearchHighlight(prefix.Length + idx, q.Length));
                }
            }
            else if (pathMatch)
            {
                var prefix = "Match in path: ";
                snippet = $"{prefix}{rel}";
                int idx = rel.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    highlights.Add(new SearchHighlight(prefix.Length + idx, q.Length));
                }
            }
            else if (titleMatch)
            {
                var prefix = "Match in title: ";
                snippet = $"{prefix}{title}";
                int idx = title.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    highlights.Add(new SearchHighlight(prefix.Length + idx, q.Length));
                }
            }

            results.Add(new SearchHit(rel, title, snippet, score, highlights));
        }

        // Order by score desc, then title asc; take limit
        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title)
            .Take(limit)
            .ToList();
    }

    public List<RecentNote> GetRecent(int limit = 20, string? tags = null)
    {
        if (limit < 1) limit = 1;
        if (limit > 100) limit = 100;

        var required = ParseTags(tags);
        var files = EnumerateNoteFiles()
            .Select(path => new FileInfo(path))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .ToList();

        var results = new List<RecentNote>(limit);
        foreach (var fi in files)
        {
            var full = fi.FullName;
            // Tag filtering if required
            if (required.Count > 0)
            {
                try
                {
                    var tagSet = ReadTagsFromMeta(full)
                        .Select(NormalizeTag)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!required.All(t => tagSet.Contains(t)))
                    {
                        continue;
                    }
                }
                catch { continue; }
            }

            string rel = Path.GetRelativePath(rootPath, full).Replace("\\", "/");
            string content = string.Empty;
            try { content = File.ReadAllText(full); } catch { /* ignore */ }
            string title = ExtractTitle(content, Path.GetFileNameWithoutExtension(full));

            results.Add(new RecentNote(rel, title, fi.LastWriteTimeUtc));

            if (results.Count >= limit) break;
        }

        return results;
    }

    public List<NoteSummary> GetUntagged()
    {
        var items = new List<NoteSummary>();
        foreach (var file in EnumerateNoteFiles())
        {
            string[] displayTags;
            try { displayTags = ReadTagsFromMeta(file); } catch { continue; }
            var normalized = displayTags.Select(NormalizeTag)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (normalized.Length == 0)
            {
                string rel = Path.GetRelativePath(rootPath, file).Replace("\\", "/");
                string content = string.Empty;
                try { content = File.ReadAllText(file); } catch { /* ignore */ }
                string title = ExtractTitle(content, Path.GetFileNameWithoutExtension(file));
                items.Add(new NoteSummary(rel, title));
            }
        }
        // Sort untagged by title for stable output
        return items.OrderBy(o => o.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Map of relative markdown path (forward slashes) to normalized tags.
    /// </summary>
    public Dictionary<string, List<string>> GetTagsIndex()
    {
        return BuildNormalizedTagsIndex();
    }

    public List<TagCount> GetTagCounts()
    {
        var index = BuildNormalizedTagsIndex();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in index.Values)
        {
            foreach (var t in list)
            {
                counts[t] = counts.TryGetValue(t, out var c) ? c + 1 : 1;
            }
        }
        return counts.Select(kv => new TagCount(kv.Key, kv.Value))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> SuggestTags(string? prefix)
    {
        var p = NormalizeTag(prefix ?? string.Empty);
        var index = BuildNormalizedTagsIndex();
        var all = new HashSet<string>(index.Values.SelectMany(v => v), StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> result = all;
        if (!string.IsNullOrEmpty(p))
        {
            result = all.Where(t => t.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }
        return result.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Take(50).ToList();
    }

    public NameValidation ValidateName(string? path, string type, string name)
    {
        // Validate basic inputs
        if (string.IsNullOrWhiteSpace(type))
        {
            return new NameValidation(false, "Type is required (file|directory)");
        }
        type = type.Trim().ToLowerInvariant();
        if (type != "file" && type != "directory")
        {
            return new NameValidation(false, "Type must be 'file' or 'directory'");
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            return new NameValidation(false, "Name cannot be empty");
        }
        if (name.Contains('/') || name.Contains('\\'))
        {
            return new NameValidation(false, "Name must not contain path separators");
        }

        // Resolve and validate parent directory
        var sanitizedRelParent = (path ?? string.Empty).Replace("..", string.Empty).Replace("\\", "/");
        var parentFull = Path.GetFullPath(Path.Combine(rootPath, sanitizedRelParent));
        var rootFull = Path.GetFullPath(rootPath);
        if (!parentFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            return new NameValidation(false, "Invalid parent path");
        }
        if (!Directory.Exists(parentFull))
        {
            return new NameValidation(false, "Parent directory does not exist");
        }

        // Sanitize name for filesystem
        var sanitized = SanitizeNameForFs(name);
        if (type == "file")
        {
            sanitized = EnsureMdExtension(sanitized);
        }
        else
        {
            // avoid ".md" directory names by stripping trailing .md if user added it
            if (sanitized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                sanitized = sanitized[..^3];
                if (sanitized.EndsWith(".", StringComparison.Ordinal))
                {
                    sanitized = sanitized.TrimEnd('.');
                }
            }
        }
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return new NameValidation(false, "Name becomes empty after sanitization");
        }

        // Determine target path and check duplicates
        var targetFull = Path.GetFullPath(Path.Combine(parentFull, sanitized));
        if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            return new NameValidation(false, "Invalid target path");
        }
        if (IsInTodoFolder(targetFull))
        {
            return new NameValidation(false, $"The {TodoService.FolderName} folder is reserved for to do lists");
        }
        if (type == "file")
        {
            if (File.Exists(targetFull))
            {
                var (baseName, ext) = (Path.GetFileNameWithoutExtension(sanitized), Path.GetExtension(sanitized));
                var unique = SuggestUniqueName(parentFull, baseName, string.IsNullOrEmpty(ext) ? ".md" : ext);
                return new NameValidation(false, "A file with this name already exists", unique);
            }
        }
        else if (Directory.Exists(targetFull))
        {
            var unique = SuggestUniqueName(parentFull, sanitized, null);
            return new NameValidation(false, "A folder with this name already exists", unique);
        }

        // Valid. If sanitation changed, propose it as suggestedName so the client can show it.
        if (!string.Equals(name, sanitized, StringComparison.Ordinal))
        {
            return new NameValidation(true, $"Name sanitized to '{sanitized}'", sanitized);
        }
        return new NameValidation(true);
    }

    // ---------------------------------------------------------------------
    // Writing
    // ---------------------------------------------------------------------

    /// <summary>
    /// Writes a note, creating it and its folders if needed. Tags replace the existing tags when
    /// given (normalized and de-duplicated); null leaves the tags untouched.
    /// </summary>
    public void Save(string path, string content, IEnumerable<string>? tags)
    {
        var filePath = RequireFilePath(path);
        if (content == null)
            throw NotesException.BadRequest("Content cannot be null");

        // Write tags to separate .meta file when provided (normalize and de-duplicate)
        if (tags != null)
        {
            var tagList = tags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(NormalizeTag)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            WriteTagsToMeta(filePath, tagList);
        }

        // Säkerställ att katalogen finns
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, content);
        _logger.LogInformation("File saved: {Path}", path);
        // Start background backup without blocking the caller
        StartBackgroundBackup();
    }

    /// <summary>
    /// Creates a new note with a title heading derived from the file name and no tags.
    /// </summary>
    public void CreateFile(string path)
    {
        var filePath = RequireFilePath(path);

        // Säkerställ att katalogen finns
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(filePath))
            throw NotesException.BadRequest("File already exists");

        var title = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "New Note";
        }
        // Use H1 heading with the title derived from filename
        var initialContent = $"# {title}\n\nStart writing here...";
        File.WriteAllText(filePath, initialContent);
        // Create empty meta file for tags
        WriteTagsToMeta(filePath, Array.Empty<string>());
        _logger.LogInformation("File created: {Path}", path);
    }

    public void CreateDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw NotesException.BadRequest("Path cannot be empty");

        var fullPath = GetSafeDirectoryPath(path) ?? throw NotesException.BadRequest("Invalid path");
        if (Directory.Exists(fullPath))
            throw NotesException.BadRequest("Directory already exists");

        Directory.CreateDirectory(fullPath);
        _logger.LogInformation("Directory created: {Path}", path);
    }

    /// <summary>
    /// Deletes a note (with its .meta file) or a folder. A non-empty folder is only deleted when
    /// <paramref name="recursive"/> is true; otherwise a Conflict is raised.
    /// </summary>
    public void Delete(string path, bool recursive = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw NotesException.BadRequest("Path cannot be empty");

        // Försök först som fil
        var filePath = GetSafePath(path);
        if (filePath != null && File.Exists(filePath))
        {
            File.Delete(filePath);
            // Try delete corresponding meta file
            try
            {
                var metaPath = GetMetaPath(filePath);
                if (File.Exists(metaPath)) File.Delete(metaPath);
            }
            catch {}
            _logger.LogInformation("File deleted: {Path}", path);
            return;
        }

        // Annars som directory
        var fullPath = GetSafeDirectoryPath(path) ?? throw NotesException.BadRequest("Invalid path");
        if (!Directory.Exists(fullPath))
            throw NotesException.NotFound();

        if (recursive)
        {
            Directory.Delete(fullPath, true);
            _logger.LogInformation("Directory deleted recursively: {Path}", path);
            return;
        }

        // Directory not empty, require confirmation from client
        if (Directory.EnumerateFileSystemEntries(fullPath).Any())
            throw NotesException.Conflict("Directory not empty");

        Directory.Delete(fullPath, false);
        _logger.LogInformation("Empty directory deleted: {Path}", path);
    }

    /// <summary>
    /// Moves a note (with its .meta file) or a folder into the destination folder.
    /// An empty destination means the root folder.
    /// </summary>
    public MoveResult Move(string source, string? destination)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw NotesException.BadRequest("Source is required");
        destination = (destination ?? string.Empty).Trim();

        var rootFull = Path.GetFullPath(rootPath);

        // First, try moving a file
        var sourceFileFull = GetSafePath(source);
        if (sourceFileFull != null && File.Exists(sourceFileFull))
        {
            // Destination is interpreted as a directory under root
            var destDirFull = GetSafeDirectoryPath(destination) ?? throw NotesException.BadRequest("Invalid destination");
            if (!Directory.Exists(destDirFull)) throw NotesException.NotFound("Destination folder not found");

            var fileName = Path.GetFileName(sourceFileFull);
            var destFileFull = Path.GetFullPath(Path.Combine(destDirFull, fileName));
            if (!destFileFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw NotesException.BadRequest("Invalid destination");
            if (File.Exists(destFileFull)) throw NotesException.Conflict("A file with the same name already exists in the destination");

            File.Move(sourceFileFull, destFileFull);
            MoveMeta(sourceFileFull, destFileFull);

            var oldRel = Path.GetRelativePath(rootPath, sourceFileFull).Replace("\\", "/");
            var newRel = Path.GetRelativePath(rootPath, destFileFull).Replace("\\", "/");
            _logger.LogInformation("File moved: {Old} -> {New}", oldRel, newRel);
            return new MoveResult(oldRel, newRel, "file");
        }

        // Otherwise, treat as directory move
        var srcDirFull = GetSafeDirectoryPath(source) ?? throw NotesException.BadRequest("Invalid source");
        if (!Directory.Exists(srcDirFull)) throw NotesException.NotFound("Source folder not found");

        var dstDirFull = GetSafeDirectoryPath(destination) ?? throw NotesException.BadRequest("Invalid destination");
        if (!Directory.Exists(dstDirFull)) throw NotesException.NotFound("Destination folder not found");

        // Prevent moving a directory into itself or its descendant
        var normSrc = srcDirFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normDst = dstDirFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (normDst.StartsWith(normSrc, StringComparison.OrdinalIgnoreCase)) throw NotesException.BadRequest("Cannot move a folder into itself or its subfolder");

        var folderName = Path.GetFileName(srcDirFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderName)) throw NotesException.BadRequest("Invalid source folder");
        var newDirFull = Path.GetFullPath(Path.Combine(dstDirFull, folderName));
        if (!newDirFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw NotesException.BadRequest("Invalid destination");
        if (Directory.Exists(newDirFull)) throw NotesException.Conflict("A folder with the same name already exists in the destination");

        Directory.Move(srcDirFull, newDirFull);

        var oldDirRel = Path.GetRelativePath(rootPath, srcDirFull).Replace("\\", "/");
        var newDirRel = Path.GetRelativePath(rootPath, newDirFull).Replace("\\", "/");
        _logger.LogInformation("Directory moved: {Old} -> {New}", oldDirRel, newDirRel);
        return new MoveResult(oldDirRel, newDirRel, "directory");
    }

    /// <summary>
    /// Renames a note (keeping it in its folder, .md is added if missing) or a folder.
    /// </summary>
    public MoveResult Rename(string path, string newName)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(newName))
            throw NotesException.BadRequest("Path and newName are required");

        var rootFull = Path.GetFullPath(rootPath);

        // Try as file first
        var oldFileFull = GetSafePath(path);
        if (oldFileFull != null && File.Exists(oldFileFull))
        {
            // Ensure new name is a simple filename
            newName = LastSegment(newName);
            if (!newName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) newName += ".md";
            var dir = Path.GetDirectoryName(oldFileFull) ?? rootFull;
            var newFull = Path.GetFullPath(Path.Combine(dir, newName));
            if (!newFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw NotesException.BadRequest("Invalid new name");
            if (File.Exists(newFull)) throw NotesException.BadRequest("A file with the new name already exists");

            File.Move(oldFileFull, newFull);
            MoveMeta(oldFileFull, newFull);

            var relNew = Path.GetRelativePath(rootPath, newFull).Replace("\\", "/");
            _logger.LogInformation("File renamed: {Old} -> {New}", path, relNew);
            return new MoveResult(path.Replace("\\", "/"), relNew, "file");
        }

        // Treat as directory
        var oldDirFull = GetSafeDirectoryPath(path) ?? throw NotesException.BadRequest("Invalid path");
        if (!Directory.Exists(oldDirFull)) throw NotesException.NotFound();
        // New name sanitize - collapse to last segment; reject empty
        newName = LastSegment(newName);
        if (string.IsNullOrWhiteSpace(newName)) throw NotesException.BadRequest("Invalid new name");
        var parent = Path.GetDirectoryName(oldDirFull) ?? rootFull;
        var newDirFull = Path.GetFullPath(Path.Combine(parent, newName));
        if (!newDirFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw NotesException.BadRequest("Invalid new name");
        RejectTodoFolder(newDirFull);
        if (Directory.Exists(newDirFull)) throw NotesException.BadRequest("A folder with the new name already exists");

        Directory.Move(oldDirFull, newDirFull);
        var relNewDir = Path.GetRelativePath(rootPath, newDirFull).Replace("\\", "/");
        var relOldDir = Path.GetRelativePath(rootPath, oldDirFull).Replace("\\", "/");
        _logger.LogInformation("Directory renamed: {Old} -> {New}", relOldDir, relNewDir);
        return new MoveResult(relOldDir, relNewDir, "directory");
    }

    // ---------------------------------------------------------------------
    // Tag utilities
    // ---------------------------------------------------------------------

    public static string NormalizeTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
        var trimmed = tag.Trim();
        // Collapse whitespaces to single space
        trimmed = Regex.Replace(trimmed, " +", " ");
        return trimmed.ToLowerInvariant();
    }

    public static string[] NormalizeTags(IEnumerable<string> tags)
    {
        return tags.Select(NormalizeTag)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Splits a comma separated tag list.
    /// </summary>
    public static string[] SplitTags(string csv)
    {
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static HashSet<string> ParseTags(string? csv)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv)) return set;
        foreach (var t in SplitTags(csv))
        {
            var n = NormalizeTag(t);
            if (!string.IsNullOrEmpty(n)) set.Add(n);
        }
        return set;
    }

    // ---------------------------------------------------------------------
    // Path helpers
    // ---------------------------------------------------------------------

    private string RequireFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw NotesException.BadRequest("Path cannot be empty");
        return GetSafePath(path) ?? throw NotesException.BadRequest("Invalid path");
    }

    /// <summary>
    /// Full path of a markdown file inside the pages root, or null if the path escapes the root
    /// or does not end in .md.
    /// </summary>
    private string? GetSafePath(string userPath)
    {
        var fullPath = GetSafeDirectoryPath(userPath);
        if (fullPath == null)
        {
            return null;
        }

        // Verifiera att filen har .md extension
        if (!fullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fullPath;
    }

    /// <summary>
    /// Full path of any entry inside the pages root (no extension requirement), or null if the
    /// path escapes the root. An empty path is the root itself.
    /// </summary>
    private string? GetSafeDirectoryPath(string userPath)
    {
        // Ta bort farliga tecken
        var sanitized = userPath.Replace("..", string.Empty).Replace("\\", "/");

        // Kombinera med root path
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, sanitized));

        // Verifiera att sökvägen är inom rootPath
        if (!fullPath.StartsWith(Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        RejectTodoFolder(fullPath);
        return fullPath;
    }

    /// <summary>
    /// True for the _ToDo folder at the pages root and anything inside it.
    /// </summary>
    private bool IsInTodoFolder(string fullPath)
    {
        var todoFull = Path.Combine(Path.GetFullPath(rootPath), TodoService.FolderName);
        var full = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(todoFull, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(todoFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Note operations must not create, change or remove anything in the _ToDo folder.
    /// </summary>
    private void RejectTodoFolder(string fullPath)
    {
        if (IsInTodoFolder(fullPath))
            throw NotesException.BadRequest($"The {TodoService.FolderName} folder is reserved for to do lists");
    }

    /// <summary>
    /// All notes in the tree, leaving out ToDo lists and dot-prefixed hidden folders (like the tree does).
    /// </summary>
    private IEnumerable<string> EnumerateNoteFiles()
    {
        return Directory.EnumerateFiles(rootPath, "*.md", SearchOption.AllDirectories)
            .Where(file => !IsInTodoFolder(file)
                           && !RelPath(file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               .SkipLast(1).Any(segment => segment.StartsWith(".")));
    }

    private static string LastSegment(string name)
    {
        name = name.Replace("..", string.Empty).Replace("\\", "/");
        return name.Contains('/') ? name.Split('/').Last() : name;
    }

    private string RelPath(string fullPath)
    {
        var rel = Path.GetRelativePath(rootPath, fullPath)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return rel;
    }

    // ---------------------------------------------------------------------
    // Meta (.meta tag files)
    // ---------------------------------------------------------------------

    private static string GetMetaPath(string markdownPath)
    {
        var dir = Path.GetDirectoryName(markdownPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(markdownPath);
        return Path.Combine(dir, name + ".meta");
    }

    private static string[] ReadTagsFromMeta(string markdownPath)
    {
        try
        {
            var metaPath = GetMetaPath(markdownPath);
            if (!File.Exists(metaPath))
            {
                return Array.Empty<string>();
            }
            var raw = File.ReadAllText(metaPath);
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return SplitTags(raw)
                      .Select(s => s.Trim())
                      .Where(s => s.Length > 0)
                      .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static void WriteTagsToMeta(string markdownPath, IEnumerable<string>? tagsEnumerable)
    {
        var tags = (tagsEnumerable ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToArray();
        var metaPath = GetMetaPath(markdownPath);
        var directory = Path.GetDirectoryName(metaPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(metaPath, string.Join(", ", tags));
    }

    private static void MoveMeta(string oldMarkdownPath, string newMarkdownPath)
    {
        try
        {
            var oldMeta = GetMetaPath(oldMarkdownPath);
            var newMeta = GetMetaPath(newMarkdownPath);
            if (File.Exists(oldMeta)) File.Move(oldMeta, newMeta);
        }
        catch { }
    }

    private Dictionary<string, List<string>> BuildNormalizedTagsIndex()
    {
        var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateNoteFiles())
        {
            var tags = ReadTagsFromMeta(file).Select(NormalizeTag).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            dict[RelPath(file).Replace('\\', '/')] = tags;
        }
        return dict;
    }

    // ---------------------------------------------------------------------
    // Tree helpers
    // ---------------------------------------------------------------------

    private static List<Breadcrumb> BuildBreadcrumbs(string relativePath)
    {
        // Expect a relative path using forward slashes and ending with .md
        var rel = (relativePath ?? string.Empty).Replace("\\", "/");
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<Breadcrumb>();
        if (parts.Length == 0) return list;

        var acc = string.Empty;
        for (int i = 0; i < parts.Length; i++)
        {
            var isLast = i == parts.Length - 1;
            var part = parts[i];
            if (!isLast)
            {
                acc = string.IsNullOrEmpty(acc) ? part : acc + "/" + part;
                list.Add(new Breadcrumb(part, acc));
            }
            else
            {
                // Final crumb is the file itself
                list.Add(new Breadcrumb(part, rel));
            }
        }
        return list;
    }

    private IEnumerable<FileSystemEntity> GetDirectoryStructure(string path)
    {
        var files = Directory.EnumerateFiles(path, "*.md")
            .Select(entry => new FileSystemEntity
            {
                Name = Path.GetFileName(entry),
                Path = RelPath(entry).Replace('\\', '/'),
                Type = "file",
                Children = null
            });

        var directories = Directory.EnumerateDirectories(path)
            .Where(entry => {
                var name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name)) return false;
                if (name.StartsWith(".")) return false; // ignore dot-prefixed hidden folders
                if (IsInTodoFolder(entry)) return false; // ToDo lists have their own page
                try
                {
                    var attrs = File.GetAttributes(entry);
                    if ((attrs & FileAttributes.Hidden) == FileAttributes.Hidden) return false;
                    if ((attrs & FileAttributes.System) == FileAttributes.System) return false;
                }
                catch
                {
                    // If attributes cannot be read, fall back to including the directory
                }
                return true;
            })
            .Select(entry => new FileSystemEntity()
            {
                Name = Path.GetFileName(entry),
                Path = RelPath(entry).Replace('\\', '/'),
                Type = "directory",
                Children = GetDirectoryStructure(entry)
            });

        return files.Concat(directories);
    }

    private IEnumerable<FileSystemEntity> FilterTreeByTags(IEnumerable<FileSystemEntity> nodes, HashSet<string> required, Dictionary<string, List<string>> index)
    {
        foreach (var n in nodes)
        {
            if (string.Equals(n.Type, "file", StringComparison.OrdinalIgnoreCase))
            {
                var tags = index.TryGetValue(n.Path, out var t)
                    ? t
                    : new List<string>();
                if (required.All(r => tags.Contains(r)))
                {
                    yield return n;
                }
            }
            else
            {
                var children = n.Children != null ? FilterTreeByTags(n.Children, required, index).ToList() : new List<FileSystemEntity>();
                if (children.Count > 0)
                {
                    yield return new FileSystemEntity { Name = n.Name, Path = n.Path, Type = n.Type, Children = children };
                }
            }
        }
    }

    private IEnumerable<FileSystemEntity> SortTree(IEnumerable<FileSystemEntity> nodes, bool dirsFirst, int depth = 0)
    {
        var list = nodes.Select(n => new FileSystemEntity
        {
            Name = n.Name,
            Path = n.Path,
            Type = n.Type,
            Children = n.Children != null ? SortTree(n.Children, dirsFirst, depth + 1).ToList() : null
        }).ToList();

        list.Sort((a, b) =>
        {
            // At the root level (depth == 0), put files before directories.
            // For deeper levels, keep existing behavior controlled by dirsFirst.
            bool aIsFile = string.Equals(a.Type, "file", StringComparison.OrdinalIgnoreCase);
            bool bIsFile = string.Equals(b.Type, "file", StringComparison.OrdinalIgnoreCase);

            if (depth == 0)
            {
                int rankA = aIsFile ? 0 : 1; // files first at root
                int rankB = bIsFile ? 0 : 1;
                if (rankA != rankB) return rankA - rankB;
            }
            else if (dirsFirst)
            {
                int rankA = aIsFile ? 1 : 0; // directories first below root
                int rankB = bIsFile ? 1 : 0;
                if (rankA != rankB) return rankA - rankB;
            }
            // Finally, name sort (case-insensitive)
            return string.Compare(a.Name ?? string.Empty, b.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    // Attach counts to directory nodes: FileCount (direct .md files), TotalFileCount (recursive)
    private (List<FileSystemEntity> nodes, int totalFiles) AttachCounts(IEnumerable<FileSystemEntity> nodes)
    {
        var result = new List<FileSystemEntity>();
        int total = 0;
        foreach (var n in nodes)
        {
            if (string.Equals(n.Type, "file", StringComparison.OrdinalIgnoreCase))
            {
                // Files are passed through unchanged; contribute 1 to totals
                result.Add(new FileSystemEntity
                {
                    Name = n.Name,
                    Path = n.Path,
                    Type = n.Type,
                    Children = null,
                    FileCount = null,
                    TotalFileCount = null
                });
                total += 1;
            }
            else
            {
                var children = n.Children ?? Enumerable.Empty<FileSystemEntity>();
                var attached = AttachCounts(children);
                var dirNode = new FileSystemEntity
                {
                    Name = n.Name,
                    Path = n.Path,
                    Type = n.Type,
                    Children = attached.nodes,
                    FileCount = attached.nodes.Count(c => string.Equals(c.Type, "file", StringComparison.OrdinalIgnoreCase)),
                    TotalFileCount = attached.totalFiles
                };
                result.Add(dirNode);
                total += attached.totalFiles;
            }
        }
        return (result, total);
    }

    private IEnumerable<FileSystemEntity> AttachTitles(IEnumerable<FileSystemEntity> nodes)
    {
        var list = new List<FileSystemEntity>();
        foreach (var n in nodes)
        {
            if (string.Equals(n.Type, "file", StringComparison.OrdinalIgnoreCase))
            {
                var rel = n.Path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var full = Path.GetFullPath(Path.Combine(rootPath, rel));
                string content = string.Empty;
                try { if (File.Exists(full)) content = File.ReadAllText(full); } catch { }
                var title = ExtractTitle(content, Path.GetFileNameWithoutExtension(full));
                list.Add(new FileSystemEntity
                {
                    Name = n.Name,
                    Path = n.Path,
                    Type = n.Type,
                    Children = null,
                    FileCount = n.FileCount,
                    TotalFileCount = n.TotalFileCount,
                    Title = title
                });
            }
            else
            {
                var children = n.Children != null ? AttachTitles(n.Children).ToList() : null;
                list.Add(new FileSystemEntity
                {
                    Name = n.Name,
                    Path = n.Path,
                    Type = n.Type,
                    Children = children,
                    FileCount = n.FileCount,
                    TotalFileCount = n.TotalFileCount,
                    Title = null
                });
            }
        }
        return list;
    }

    // ---------------------------------------------------------------------
    // Names and titles
    // ---------------------------------------------------------------------

    private static string SanitizeNameForFs(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        var cleaned = new string(chars);
        // Collapse whitespace to single hyphen
        cleaned = Regex.Replace(cleaned, "\\s+", "-");
        // Collapse multiple hyphens
        cleaned = Regex.Replace(cleaned, "-+", "-");
        cleaned = cleaned.Trim('-').Trim();
        return cleaned;
    }

    private static string EnsureMdExtension(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name : name + ".md";
    }

    private static string SuggestUniqueName(string parentDirFull, string baseName, string? ext)
    {
        // ext includes the dot (e.g., ".md") or null for directories
        string candidate = ext == null ? baseName : baseName + ext;
        int i = 2;
        while (true)
        {
            var full = Path.Combine(parentDirFull, candidate);
            bool exists = ext == null ? Directory.Exists(full) : File.Exists(full);
            if (!exists) return candidate;
            candidate = ext == null ? $"{baseName}-{i}" : $"{baseName}-{i}{ext}";
            i++;
            if (i > 1000) return candidate; // safety cap
        }
    }

    private static string ExtractTitle(string? content, string? fallbackName)
    {
        try
        {
            if (!string.IsNullOrEmpty(content))
            {
                var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var raw in lines)
                {
                    var line = raw.TrimStart();
                    if (line.StartsWith("# "))
                    {
                        var t = line.TrimStart('#').Trim();
                        if (!string.IsNullOrWhiteSpace(t)) return t;
                    }
                }
            }
        }
        catch { /* ignore */ }
        var fb = fallbackName ?? string.Empty;
        return string.IsNullOrWhiteSpace(fb) ? "Untitled" : fb;
    }

    // ---------------------------------------------------------------------
    // Backup
    // ---------------------------------------------------------------------

    /// <summary>
    /// Starts a background zip backup of the pages folder, as after saving a note.
    /// </summary>
    internal void RequestBackup() => StartBackgroundBackup();

    private void StartBackgroundBackup()
    {
        try
        {
            // Fire-and-forget background backup to avoid blocking the caller
            _ = Task.Run(() =>
            {
                try
                {
                    // Prevent placing backups inside the Pages directory to avoid self-inclusion
                    var pagesFull = Path.GetFullPath(rootPath);
                    var backupFull = Path.GetFullPath(backupRootPath);
                    if (backupFull.StartsWith(pagesFull, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Backup directory is inside Pages directory. Skipping backup to avoid recursive zipping. Path: {BackupDir}", backupRootPath);
                        return;
                    }

                    // Ensure backup root exists
                    if (!Directory.Exists(backupRootPath))
                    {
                        Directory.CreateDirectory(backupRootPath);
                    }

                    // Build a timestamped file name
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var zipFileName = $"NodePadBackup_{timestamp}.zip";
                    var zipPath = Path.Combine(backupRootPath, zipFileName);

                    // In the rare case of a collision, append a short guid
                    if (File.Exists(zipPath))
                    {
                        zipFileName = $"NodePadBackup_{timestamp}_{Guid.NewGuid().ToString()[..8]}.zip";
                        zipPath = Path.Combine(backupRootPath, zipFileName);
                    }

                    // Create zip of the Pages root folder
                    // includeBaseDirectory=false => only contents of rootPath at top of zip
                    ZipFile.CreateFromDirectory(rootPath, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                    _logger.LogInformation("Backup created: {ZipPath}", zipPath);

                    // Retention policy: keep only the 10 most recent backup zip files
                    try
                    {
                        var backupFiles = Directory.EnumerateFiles(backupRootPath, "NodePadBackup_*.zip")
                            .Select(f => new FileInfo(f))
                            .OrderByDescending(f => f.CreationTimeUtc)
                            .ToList();

                        const int maxBackups = 10;
                        if (backupFiles.Count > maxBackups)
                        {
                            foreach (var old in backupFiles.Skip(maxBackups))
                            {
                                try
                                {
                                    File.Delete(old.FullName);
                                    _logger.LogInformation("Old backup deleted: {ZipPath}", old.FullName);
                                }
                                catch (Exception delEx)
                                {
                                    _logger.LogWarning(delEx, "Failed to delete old backup: {ZipPath}", old.FullName);
                                }
                            }
                        }
                    }
                    catch (Exception retentionEx)
                    {
                        _logger.LogWarning(retentionEx, "Backup retention cleanup failed");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background backup failed");
                }
            });
        }
        catch (Exception ex)
        {
            // As a last resort, log and continue; never fail the save due to backup
            _logger.LogError(ex, "Failed to queue background backup");
        }
    }
}
