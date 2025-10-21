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
    public IActionResult GetFolderStructure()
    {
        try
        {
            var structure = GetDirectoryStructure(rootPath);
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
    public IActionResult GetPageContent([FromQuery] string path)
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
            return Ok(content);
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
    public IActionResult GetPageMeta([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Path cannot be empty");

        var filePath = GetSafePath(path);
        if (filePath == null)
            return BadRequest("Invalid path");

        try
        {
            var tags = ReadTagsFromMeta(filePath);
            return Ok(tags);
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
            // Write tags to separate .meta file when provided
            if (tags != null)
            {
                var tagList = tags
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => t.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
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
    public IActionResult Search([FromQuery] string query)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            {
                return BadRequest("Query must be at least 2 characters long");
            }

            var q = query.Trim();
            var results = new List<object>();
            var files = Directory.EnumerateFiles(rootPath, "*.md", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                string rel = Path.GetRelativePath(rootPath, file).Replace("\\", "/");
                string name = Path.GetFileNameWithoutExtension(file);
                string content = string.Empty;
                try { content = System.IO.File.ReadAllText(file); } catch { /* ignore unreadable files */ }

                bool match = false;
                int idx = -1;
                // Match on file name or relative path
                if (name.Contains(q, StringComparison.OrdinalIgnoreCase) || rel.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    match = true;
                }
                // Match on content
                if (!match && !string.IsNullOrEmpty(content))
                {
                    idx = content.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                    match = idx >= 0;
                }

                if (!match) continue;

                // Title: first H1 if present, otherwise filename
                string title = name;
                try
                {
                    var lines = content?.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                    var h1 = lines.FirstOrDefault(l => l.TrimStart().StartsWith("# "));
                    if (!string.IsNullOrWhiteSpace(h1))
                    {
                        title = h1.TrimStart().TrimStart('#').Trim();
                    }
                }
                catch { /* ignore */ }

                // Snippet around first match in content (or filename)
                string snippet = "";
                if (idx >= 0 && !string.IsNullOrEmpty(content))
                {
                    int start = Math.Max(0, idx - 40);
                    int len = Math.Min(content.Length - start, 160);
                    snippet = content.Substring(start, len).Replace('\n', ' ').Replace('\r', ' ');
                    if (start > 0) snippet = "…" + snippet;
                    if (start + len < content.Length) snippet += "…";
                }
                else if (name.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    snippet = $"Match in file name: {name}.md";
                }
                else if (rel.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    snippet = $"Match in path: {rel}";
                }

                results.Add(new { path = rel, title, snippet });
            }

            // Limit to first 100 results to keep payload small
            return Ok(results.Take(100));
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
    public IActionResult Move([FromQuery] string source, [FromQuery] string destination)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
            return BadRequest("Source and destination are required");
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
}