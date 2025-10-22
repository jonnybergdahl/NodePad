using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Linq;
using Bergdahl.NodePad.WebApp.Models;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Bergdahl.NodePad.WebApp;

[ApiController]
[Route("api/pages")]
public class PagesController: ControllerBase
{
    private readonly string rootPath;
    private readonly string backupRootPath;
    private readonly ILogger<PagesController> _logger;

    public PagesController(IConfiguration configuration, ILogger<PagesController> logger)
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
    
    [HttpGet("structure")]
    public IActionResult GetFolderStructure([FromQuery] bool sorted = false, [FromQuery] bool dirsFirst = true, [FromQuery] string? tags = null, [FromQuery] bool includeCounts = false, [FromQuery] bool includeTitles = false)
    {
        try
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

            return Ok(structure);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when reading folder structure");
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading folder structure");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("content")]
    public IActionResult GetPageContent([FromQuery] string path, [FromQuery] bool includeMeta = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Path cannot be empty");
    
        var filePath = GetSafePath(path);
        if (filePath == null)
            return BadRequest("Invalid path");
    
        try
        {
            if (!System.IO.File.Exists(filePath)) 
                return NotFound();
        
            var content = System.IO.File.ReadAllText(filePath);
            if (!includeMeta)
            {
                // Backward compatibility: plain text as before
                return Ok(content);
            }

            var breadcrumbs = BuildBreadcrumbs(RelPath(filePath).Replace('\\', '/'));
            return Ok(new { content, breadcrumbs });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when reading file: {Path}", path);
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading file: {Path}", path);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("meta")]
    public IActionResult GetPageMeta([FromQuery] string path, [FromQuery] bool includeNormalized = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Path cannot be empty");

        var filePath = GetSafePath(path);
        if (filePath == null)
            return BadRequest("Invalid path");

        try
        {
            var display = ReadTagsFromMeta(filePath);
            if (!includeNormalized)
            {
                return Ok(display);
            }
            var normalized = display.Select(NormalizeTag)
                                    .Where(s => !string.IsNullOrEmpty(s))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToArray();
            return Ok(new { display, normalized });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when reading meta: {Path}", path);
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading meta: {Path}", path);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost("save")]
    [Consumes("text/plain")]
    public IActionResult SavePageContent([FromQuery] string path, [FromBody] string content, [FromQuery] string? tags)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Path cannot be empty");
        
        if (content == null)
            return BadRequest("Content cannot be null");
        
        var filePath = GetSafePath(path);
        if (filePath == null)
            return BadRequest("Invalid path");
        
        try
        {
            // Write tags to separate .meta file when provided (normalize and de-duplicate)
            if (tags != null)
            {
                var tagList = tags
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
            
            System.IO.File.WriteAllText(filePath, content);
            _logger.LogInformation("File saved: {Path}", path);
            // Start background backup without blocking the response
            StartBackgroundBackup();
            return Content(content, "text/plain");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when saving file: {Path}", path);
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving file: {Path}", path);
            return StatusCode(500, "Internal server error");
        }
    }

    // JSON variant for structured tags and content at the same route
    [HttpPost("save")]
    [Consumes("application/json")]
    public IActionResult SavePageContentJson([FromQuery] string path, [FromBody] Models.SavePageRequest request)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Path cannot be empty");
        if (request == null)
            return BadRequest("Request body cannot be null");
        if (request.Content == null)
            return BadRequest("Content cannot be null");

        var filePath = GetSafePath(path);
        if (filePath == null)
            return BadRequest("Invalid path");

        try
        {
            // Normalize and write tags if provided
            if (request.Tags != null)
            {
                var tagList = request.Tags
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(NormalizeTag)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                WriteTagsToMeta(filePath, tagList);
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            System.IO.File.WriteAllText(filePath, request.Content);
            _logger.LogInformation("File saved (JSON): {Path}", path);
            StartBackgroundBackup();
            return Content(request.Content, "text/plain");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when saving file (JSON): {Path}", path);
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving file (JSON): {Path}", path);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost("create")]
    public IActionResult CreateEntity([FromQuery] string path, [FromQuery] string type)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Path cannot be empty");
        
        if (string.IsNullOrWhiteSpace(type) || (type != "file" && type != "directory"))
            return BadRequest("Type must be 'file' or 'directory'");

        try
        {
            if (type == "file")
            {
                var filePath = GetSafePath(path);
                if (filePath == null)
                    return BadRequest("Invalid path");

                // Säkerställ att katalogen finns
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Skapa fil med titel som första raden om den inte finns
                if (!System.IO.File.Exists(filePath))
                {
                    var title = Path.GetFileNameWithoutExtension(filePath);
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = "New Note";
                    }
                    // Use H1 heading with the title derived from filename
                    var initialContent = $"# {title}\n\nStart writing here...";
                    System.IO.File.WriteAllText(filePath, initialContent);
                    // Create empty meta file for tags
                    WriteTagsToMeta(filePath, Array.Empty<string>());
                    _logger.LogInformation("File created: {Path}", path);
                }
                else
                {
                    return BadRequest("File already exists");
                }
            }
            else // directory
            {
                // Validera directory path (utan .md extension krav)
                var sanitizedPath = path.Replace("..", "").Replace("\\", "/");
                var fullPath = Path.GetFullPath(Path.Combine(rootPath, sanitizedPath));
                
                if (!fullPath.StartsWith(Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Invalid path");
                }

                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                    _logger.LogInformation("Directory created: {Path}", path);
                }
                else
                {
                    return BadRequest("Directory already exists");
                }
            }

            return Ok();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when creating {Type}: {Path}", type, path);
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating {Type}: {Path}", type, path);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpDelete("delete")]
    public IActionResult DeleteEntity([FromQuery] string path, [FromQuery] bool recursive = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Path cannot be empty");

        try
        {
            // Försök först som fil
            var filePath = GetSafePath(path);
            if (filePath != null && System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
                // Try delete corresponding meta file
                try
                {
                    var metaPath = GetMetaPath(filePath);
                    if (System.IO.File.Exists(metaPath)) System.IO.File.Delete(metaPath);
                }
                catch {}
                _logger.LogInformation("File deleted: {Path}", path);
                return Ok();
            }

            // Annars som directory
            var sanitizedPath = path.Replace("..", "").Replace("\\", "/");
            var fullPath = Path.GetFullPath(Path.Combine(rootPath, sanitizedPath));
            
            if (!fullPath.StartsWith(Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Invalid path");
            }

            if (Directory.Exists(fullPath))
            {
                if (recursive)
                {
                    Directory.Delete(fullPath, true);
                    _logger.LogInformation("Directory deleted recursively: {Path}", path);
                    return Ok();
                }
                else
                {
                    // Check if directory is empty
                    bool isEmpty = !Directory.EnumerateFileSystemEntries(fullPath).Any();
                    if (!isEmpty)
                    {
                        // Directory not empty, require confirmation from client
                        return StatusCode(409, "Directory not empty");
                    }
                    Directory.Delete(fullPath, false);
                    _logger.LogInformation("Empty directory deleted: {Path}", path);
                    return Ok();
                }
            }

            return NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when deleting: {Path}", path);
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting: {Path}", path);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("search")]
    public IActionResult Search([FromQuery] string query, [FromQuery] string? tags = null, [FromQuery] int limit = 100)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            {
                return BadRequest("Query must be at least 2 characters long");
            }

            // Normalize inputs
            var q = query.Trim();
            var qLower = q.ToLowerInvariant();
            if (limit < 1) limit = 1;
            if (limit > 200) limit = 200;

            var requiredTags = ParseTags(tags);

            var results = new List<object>();
            var files = Directory.EnumerateFiles(rootPath, "*.md", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                string rel = Path.GetRelativePath(rootPath, file).Replace("\\", "/");
                string name = Path.GetFileNameWithoutExtension(file);

                string content = string.Empty;
                try { content = System.IO.File.ReadAllText(file); } catch { /* ignore unreadable files */ }

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
                var highlights = new List<object>();

                if (contentFirstIdx >= 0 && !string.IsNullOrEmpty(content))
                {
                    int start = Math.Max(0, contentFirstIdx - 40);
                    int len = Math.Min(content.Length - start, 200);
                    var raw = content.Substring(start, len);
                    // Replace newlines with spaces but keep indices consistent (1 char -> 1 char)
                    raw = raw.Replace('\n', ' ').Replace('\r', ' ');

                    // Find highlight ranges within the raw snippet
                    var rawLower = raw.ToLowerInvariant();
                    int s = 0;
                    while (true)
                    {
                        s = rawLower.IndexOf(qLower, s, StringComparison.Ordinal);
                        if (s < 0) break;
                        highlights.Add(new { start = s, length = q.Length });
                        s += qLower.Length;
                    }

                    // Add ellipsis if trimmed and adjust highlight positions accordingly
                    bool prefixed = start > 0;
                    bool suffixed = (start + len) < content.Length;
                    if (prefixed)
                    {
                        // shift all starts by 1 for leading ellipsis
                        highlights = highlights.Select(h =>
                        {
                            var st = (int)h.GetType().GetProperty("start")!.GetValue(h)!;
                            var ln = (int)h.GetType().GetProperty("length")!.GetValue(h)!;
                            return new { start = st + 1, length = ln };
                        }).ToList<object>();
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
                        highlights.Add(new { start = prefix.Length + idx, length = q.Length });
                    }
                }
                else if (pathMatch)
                {
                    var prefix = "Match in path: ";
                    snippet = $"{prefix}{rel}";
                    int idx = rel.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        highlights.Add(new { start = prefix.Length + idx, length = q.Length });
                    }
                }
                else if (titleMatch)
                {
                    var prefix = "Match in title: ";
                    snippet = $"{prefix}{title}";
                    int idx = title.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        highlights.Add(new { start = prefix.Length + idx, length = q.Length });
                    }
                }

                results.Add(new { path = rel, title, snippet, score, highlights });
            }

            // Order by score desc, then title asc; take limit
            var ordered = results
                .OrderByDescending(r => (int)r.GetType().GetProperty("score")!.GetValue(r)!)
                .ThenBy(r => (string)r.GetType().GetProperty("title")!.GetValue(r)!)
                .Take(limit)
                .ToList();

            return Ok(ordered);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied during search");
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during search");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("recent")]
    public IActionResult GetRecent([FromQuery] int limit = 20, [FromQuery] string? tags = null)
    {
        try
        {
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            var required = ParseTags(tags);
            var files = Directory.EnumerateFiles(rootPath, "*.md", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToList();

            var results = new List<object>(limit);
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

                // Build response item
                string rel = Path.GetRelativePath(rootPath, full).Replace("\\", "/");
                string content = string.Empty;
                try { content = System.IO.File.ReadAllText(full); } catch { /* ignore */ }
                string title = ExtractTitle(content, Path.GetFileNameWithoutExtension(full));

                results.Add(new
                {
                    path = rel,
                    title,
                    lastModifiedUtc = fi.LastWriteTimeUtc
                });

                if (results.Count >= limit) break;
            }

            return Ok(results);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when listing recent notes");
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing recent notes");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("untagged")]
    public IActionResult GetUntagged()
    {
        try
        {
            var items = new List<object>();
            foreach (var file in Directory.EnumerateFiles(rootPath, "*.md", SearchOption.AllDirectories))
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
                    try { content = System.IO.File.ReadAllText(file); } catch { /* ignore */ }
                    string title = ExtractTitle(content, Path.GetFileNameWithoutExtension(file));
                    items.Add(new { path = rel, title });
                }
            }
            // Sort untagged by title for stable output
            var ordered = items
                .OrderBy(o => (string)o.GetType().GetProperty("title")!.GetValue(o)!, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Ok(ordered);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when listing untagged notes");
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing untagged notes");
            return StatusCode(500, "Internal server error");
        }
    }
    
    private string? GetSafePath(string userPath)
    {
        // Ta bort farliga tecken
        userPath = userPath.Replace("..", "").Replace("\\", "/");
        
        // Kombinera med root path
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, userPath));
        
        // Verifiera att sökvägen är inom rootPath
        if (!fullPath.StartsWith(Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase))
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

    // New meta helpers
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
            if (!System.IO.File.Exists(metaPath))
            {
                return Array.Empty<string>();
            }
            var raw = System.IO.File.ReadAllText(metaPath);
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
        System.IO.File.WriteAllText(metaPath, string.Join(", ", tags));
    }

    // Tag utilities
    private static string NormalizeTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
        var trimmed = tag.Trim();
        // Collapse whitespaces to single space
        trimmed = Regex.Replace(trimmed, "\u0020+", " ");
        return trimmed.ToLowerInvariant();
    }

    private HashSet<string> ParseTags(string? csv)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv)) return set;
        foreach (var t in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = NormalizeTag(t);
            if (!string.IsNullOrEmpty(n)) set.Add(n);
        }
        return set;
    }

    private string RelPath(string fullPath)
    {
        var rel = Path.GetRelativePath(rootPath, fullPath)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return rel;
    }

    private sealed class BreadcrumbItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    private List<BreadcrumbItem> BuildBreadcrumbs(string relativePath)
    {
        // Expect a relative path using forward slashes and ending with .md
        var rel = (relativePath ?? string.Empty).Replace("\\", "/");
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<BreadcrumbItem>();
        if (parts.Length == 0) return list;

        var acc = string.Empty;
        for (int i = 0; i < parts.Length; i++)
        {
            var isLast = i == parts.Length - 1;
            var part = parts[i];
            if (!isLast)
            {
                acc = string.IsNullOrEmpty(acc) ? part : acc + "/" + part;
                list.Add(new BreadcrumbItem { Name = part, Path = acc });
            }
            else
            {
                // Final crumb is the file itself
                list.Add(new BreadcrumbItem { Name = part, Path = rel });
            }
        }
        return list;
    }

    private Dictionary<string, List<string>> BuildNormalizedTagsIndex()
    {
        var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(rootPath, "*.md", SearchOption.AllDirectories))
        {
            var tags = ReadTagsFromMeta(file).Select(NormalizeTag).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            dict[RelPath(file)] = tags;
        }
        return dict;
    }

    private IEnumerable<FileSystemEntity> FilterTreeByTags(IEnumerable<FileSystemEntity> nodes, HashSet<string> required, Dictionary<string, List<string>> index)
    {
        foreach (var n in nodes)
        {
            if (string.Equals(n.Type, "file", StringComparison.OrdinalIgnoreCase))
            {
                var tags = index.TryGetValue(n.Path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), out var t)
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

    private IEnumerable<FileSystemEntity> SortTree(IEnumerable<FileSystemEntity> nodes, bool dirsFirst)
    {
        var list = nodes.Select(n => new FileSystemEntity
        {
            Name = n.Name,
            Path = n.Path,
            Type = n.Type,
            Children = n.Children != null ? SortTree(n.Children, dirsFirst).ToList() : null
        }).ToList();

        list.Sort((a, b) =>
        {
            int dirRankA = string.Equals(a.Type, "file", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            int dirRankB = string.Equals(b.Type, "file", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (dirsFirst && dirRankA != dirRankB) return dirRankA - dirRankB;
            return string.Compare(a.Name ?? string.Empty, b.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    // Tags endpoints
    [HttpGet("tags-index")]
    public IActionResult GetTagsIndex()
    {
        try
        {
            var index = BuildNormalizedTagsIndex();
            // Return with forward slashes for client friendliness
            var obj = index.ToDictionary(kv => kv.Key.Replace('\\', '/'), kv => (IEnumerable<string>)kv.Value);
            return Ok(obj);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building tags index");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("tags")]
    public IActionResult GetTags()
    {
        try
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
            var arr = counts.Select(kv => new { tag = kv.Key, count = kv.Value })
                .OrderByDescending(x => x.count)
                .ThenBy(x => x.tag, StringComparer.OrdinalIgnoreCase);
            return Ok(arr);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error aggregating tags");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("tags/suggest")]
    public IActionResult SuggestTags([FromQuery] string? prefix)
    {
        try
        {
            var p = NormalizeTag(prefix ?? string.Empty);
            var index = BuildNormalizedTagsIndex();
            var all = new HashSet<string>(index.Values.SelectMany(v => v), StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> result = all;
            if (!string.IsNullOrEmpty(p))
            {
                result = all.Where(t => t.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            }
            return Ok(result.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Take(50));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error suggesting tags");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("breadcrumbs")]
    public IActionResult GetBreadcrumbsOnly([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Path cannot be empty");

        var filePath = GetSafePath(path);
        if (filePath == null)
            return BadRequest("Invalid path");

        try
        {
            if (!System.IO.File.Exists(filePath))
                return NotFound();

            var rel = RelPath(filePath).Replace('\\', '/');
            var items = BuildBreadcrumbs(rel);
            return Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when computing breadcrumbs for: {Path}", path);
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing breadcrumbs for: {Path}", path);
            return StatusCode(500, "Internal server error");
        }
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

    private void StartBackgroundBackup()
    {
        try
        {
            // Fire-and-forget background backup to avoid blocking the HTTP request
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
                    if (System.IO.File.Exists(zipPath))
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
                                    System.IO.File.Delete(old.FullName);
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

    private IEnumerable<FileSystemEntity> GetDirectoryStructure(string path)
    {
        var files = Directory.EnumerateFiles(path, "*.md")
            .Select(entry => new FileSystemEntity
            {
                Name = Path.GetFileName(entry),
                Path = entry.Replace(rootPath, "").TrimStart(Path.DirectorySeparatorChar),
                Type = "file",
                Children = null
            });

        var directories = Directory.EnumerateDirectories(path)
            .Where(entry => {
                var name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name)) return false;
                if (name.StartsWith(".")) return false; // ignore dot-prefixed hidden folders
                try
                {
                    var attrs = System.IO.File.GetAttributes(entry);
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
                Path = entry.Replace(rootPath, "").TrimStart(Path.DirectorySeparatorChar),
                Type = "directory",
                Children = GetDirectoryStructure(entry)
            });

        return files.Concat(directories);
    }

    [HttpPost("move")]
    public IActionResult Move([FromQuery] string source, [FromQuery] string? destination)
    {
        if (string.IsNullOrWhiteSpace(source))
            return BadRequest("Source is required");
        destination = (destination ?? string.Empty).Trim();
        try
        {
            var rootFull = Path.GetFullPath(rootPath);

            // First, try moving a file
            var sourceFileFull = GetSafePath(source);
            if (sourceFileFull != null && System.IO.File.Exists(sourceFileFull))
            {
                // Destination is interpreted as a directory under root
                var destSanitized = destination.Replace("..", string.Empty).Replace("\\", "/");
                var destDirFull = Path.GetFullPath(Path.Combine(rootPath, destSanitized));
                if (!destDirFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return BadRequest("Invalid destination");
                if (!Directory.Exists(destDirFull)) return NotFound("Destination folder not found");

                var fileName = Path.GetFileName(sourceFileFull);
                if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    // Enforce .md extension
                    fileName += ".md";
                }
                var destFileFull = Path.GetFullPath(Path.Combine(destDirFull, fileName));
                if (!destFileFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return BadRequest("Invalid destination");
                if (System.IO.File.Exists(destFileFull)) return Conflict("A file with the same name already exists in the destination");

                System.IO.File.Move(sourceFileFull, destFileFull);
                // Move meta if present
                try
                {
                    var oldMeta = GetMetaPath(sourceFileFull);
                    var newMeta = GetMetaPath(destFileFull);
                    if (System.IO.File.Exists(oldMeta)) System.IO.File.Move(oldMeta, newMeta);
                }
                catch { }

                var oldRel = Path.GetRelativePath(rootPath, sourceFileFull).Replace("\\", "/");
                var newRel = Path.GetRelativePath(rootPath, destFileFull).Replace("\\", "/");
                _logger.LogInformation("File moved: {Old} -> {New}", oldRel, newRel);
                return Ok(new { oldPath = oldRel, path = newRel, type = "file" });
            }

            // Otherwise, treat as directory move
            var srcSan = source.Replace("..", string.Empty).Replace("\\", "/");
            var srcDirFull = Path.GetFullPath(Path.Combine(rootPath, srcSan));
            if (!srcDirFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return BadRequest("Invalid source");
            if (!Directory.Exists(srcDirFull)) return NotFound("Source folder not found");

            var dstSan = destination.Replace("..", string.Empty).Replace("\\", "/");
            var dstDirFull2 = Path.GetFullPath(Path.Combine(rootPath, dstSan));
            if (!dstDirFull2.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return BadRequest("Invalid destination");
            if (!Directory.Exists(dstDirFull2)) return NotFound("Destination folder not found");

            // Prevent moving a directory into itself or its descendant
            var normSrc = srcDirFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normDst = dstDirFull2.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (normDst.StartsWith(normSrc, StringComparison.OrdinalIgnoreCase)) return BadRequest("Cannot move a folder into itself or its subfolder");

            var folderName = Path.GetFileName(srcDirFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(folderName)) return BadRequest("Invalid source folder");
            var newDirFull = Path.GetFullPath(Path.Combine(dstDirFull2, folderName));
            if (!newDirFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return BadRequest("Invalid destination");
            if (Directory.Exists(newDirFull)) return Conflict("A folder with the same name already exists in the destination");

            Directory.Move(srcDirFull, newDirFull);

            var oldDirRel = Path.GetRelativePath(rootPath, srcDirFull).Replace("\\", "/");
            var newDirRel = Path.GetRelativePath(rootPath, newDirFull).Replace("\\", "/");
            _logger.LogInformation("Directory moved: {Old} -> {New}", oldDirRel, newDirRel);
            return Ok(new { oldPath = oldDirRel, path = newDirRel, type = "directory" });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when moving: {Source} -> {Destination}", source, destination);
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error moving: {Source} -> {Destination}", source, destination);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost("rename")]
    public IActionResult Rename([FromQuery] string path, [FromQuery] string newName)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(newName))
            return BadRequest("Path and newName are required");
        try
        {
            // Try as file first
            var oldFileFull = GetSafePath(path);
            var rootFull = Path.GetFullPath(rootPath);
            if (oldFileFull != null && System.IO.File.Exists(oldFileFull))
            {
                // Ensure new name is a simple filename
                newName = newName.Replace("..", string.Empty).Replace("\\", "/");
                if (newName.Contains('/')) newName = newName.Split('/').Last();
                if (!newName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) newName += ".md";
                var dir = Path.GetDirectoryName(oldFileFull) ?? rootFull;
                var newFull = Path.GetFullPath(Path.Combine(dir, newName));
                if (!newFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return BadRequest("Invalid new name");
                if (System.IO.File.Exists(newFull)) return BadRequest("A file with the new name already exists");
                // Move markdown
                System.IO.File.Move(oldFileFull, newFull);
                // Move meta if present
                try
                {
                    var oldMeta = GetMetaPath(oldFileFull);
                    var newMeta = GetMetaPath(newFull);
                    if (System.IO.File.Exists(oldMeta)) System.IO.File.Move(oldMeta, newMeta);
                }
                catch { }
                var relNew = Path.GetRelativePath(rootPath, newFull).Replace("\\", "/");
                _logger.LogInformation("File renamed: {Old} -> {New}", path, relNew);
                return Ok(new { oldPath = path.Replace("\\", "/"), path = relNew, type = "file" });
            }

            // Treat as directory
            var sanitized = path.Replace("..", string.Empty).Replace("\\", "/");
            var oldDirFull = Path.GetFullPath(Path.Combine(rootPath, sanitized));
            if (!oldDirFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return BadRequest("Invalid path");
            if (!Directory.Exists(oldDirFull)) return NotFound();
            // New name sanitize - collapse to last segment; reject empty
            newName = newName.Replace("..", string.Empty).Replace("\\", "/");
            if (newName.Contains('/')) newName = newName.Split('/').Last();
            if (string.IsNullOrWhiteSpace(newName)) return BadRequest("Invalid new name");
            var parent = Path.GetDirectoryName(oldDirFull) ?? rootFull;
            var newDirFull = Path.GetFullPath(Path.Combine(parent, newName));
            if (!newDirFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return BadRequest("Invalid new name");
            if (Directory.Exists(newDirFull)) return BadRequest("A folder with the new name already exists");
            Directory.Move(oldDirFull, newDirFull);
            var relNewDir = Path.GetRelativePath(rootPath, newDirFull).Replace("\\", "/");
            var relOldDir = Path.GetRelativePath(rootPath, oldDirFull).Replace("\\", "/");
            _logger.LogInformation("Directory renamed: {Old} -> {New}", relOldDir, relNewDir);
            return Ok(new { oldPath = relOldDir, path = relNewDir, type = "directory" });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when renaming: {Path}", path);
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming: {Path}", path);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("validate-name")]
    public IActionResult ValidateName([FromQuery] string? path, [FromQuery] string type, [FromQuery] string name)
    {
        try
        {
            // Validate basic inputs
            if (string.IsNullOrWhiteSpace(type))
            {
                return Ok(new { valid = false, message = "Type is required (file|directory)" });
            }
            type = type.Trim().ToLowerInvariant();
            if (type != "file" && type != "directory")
            {
                return Ok(new { valid = false, message = "Type must be 'file' or 'directory'" });
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                return Ok(new { valid = false, message = "Name cannot be empty" });
            }
            if (name.Contains('/') || name.Contains('\\'))
            {
                return Ok(new { valid = false, message = "Name must not contain path separators" });
            }

            // Resolve and validate parent directory
            var sanitizedRelParent = (path ?? string.Empty).Replace("..", string.Empty).Replace("\\", "/");
            var parentFull = Path.GetFullPath(Path.Combine(rootPath, sanitizedRelParent));
            var rootFull = Path.GetFullPath(rootPath);
            if (!parentFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return Ok(new { valid = false, message = "Invalid parent path" });
            }
            if (!Directory.Exists(parentFull))
            {
                return Ok(new { valid = false, message = "Parent directory does not exist" });
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
                return Ok(new { valid = false, message = "Name becomes empty after sanitization" });
            }

            // Determine target path and check duplicates
            string targetFull;
            if (type == "file")
            {
                targetFull = Path.GetFullPath(Path.Combine(parentFull, sanitized));
                if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    return Ok(new { valid = false, message = "Invalid target path" });
                }
                if (System.IO.File.Exists(targetFull))
                {
                    var (baseName, ext) = (Path.GetFileNameWithoutExtension(sanitized), Path.GetExtension(sanitized));
                    var unique = SuggestUniqueName(parentFull, baseName, string.IsNullOrEmpty(ext) ? ".md" : ext);
                    return Ok(new { valid = false, message = "A file with this name already exists", suggestedName = unique });
                }
            }
            else
            {
                targetFull = Path.GetFullPath(Path.Combine(parentFull, sanitized));
                if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    return Ok(new { valid = false, message = "Invalid target path" });
                }
                if (Directory.Exists(targetFull))
                {
                    var unique = SuggestUniqueName(parentFull, sanitized, null);
                    return Ok(new { valid = false, message = "A folder with this name already exists", suggestedName = unique });
                }
            }

            // Valid. If sanitation changed, propose it as suggestedName so the client can show it.
            if (!string.Equals(name, sanitized, StringComparison.Ordinal))
            {
                return Ok(new { valid = true, message = $"Name sanitized to '{sanitized}'", suggestedName = sanitized });
            }
            return Ok(new { valid = true });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied during name validation");
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during name validation");
            return StatusCode(500, "Internal server error");
        }
    }

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
            bool exists = ext == null ? Directory.Exists(full) : System.IO.File.Exists(full);
            if (!exists) return candidate;
            candidate = ext == null ? $"{baseName}-{i}" : $"{baseName}-{i}{ext}";
            i++;
            if (i > 1000) return candidate; // safety cap
        }
    }

    [HttpGet("info")]
    public IActionResult GetPageInfo([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Path cannot be empty");

        var filePath = GetSafePath(path);
        if (filePath == null)
            return BadRequest("Invalid path");

        try
        {
            if (!System.IO.File.Exists(filePath))
                return NotFound();

            var content = string.Empty;
            try { content = System.IO.File.ReadAllText(filePath); } catch { /* ignore */ }
            var fallbackName = Path.GetFileNameWithoutExtension(filePath) ?? "";
            var title = ExtractTitle(content, fallbackName);

            var tagsDisplay = ReadTagsFromMeta(filePath);
            var fi = new FileInfo(filePath);
            var lastModifiedUtc = fi.LastWriteTimeUtc;
            var sizeBytes = fi.Exists ? fi.Length : 0L;

            return Ok(new { title, tags = tagsDisplay, lastModifiedUtc, sizeBytes });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when reading info: {Path}", path);
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading info: {Path}", path);
            return StatusCode(500, "Internal server error");
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
                try { if (System.IO.File.Exists(full)) content = System.IO.File.ReadAllText(full); } catch { }
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
}