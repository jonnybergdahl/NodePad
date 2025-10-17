using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Linq;
using Bergdahl.NodePad.WebApp.Models;

namespace Bergdahl.NodePad.WebApp;

[ApiController]
[Route("api/pages")]
public class PagesController: ControllerBase
{
    private readonly string rootPath;
    private readonly ILogger<PagesController> _logger;

    public PagesController(IConfiguration configuration, ILogger<PagesController> logger)
    {
        _logger = logger;
        rootPath = configuration.GetValue<string>("PagesDirectory") 
                   ?? Path.Combine(Directory.GetCurrentDirectory(), "Pages");
        
        // Säkerställ att katalogen finns
        if (!Directory.Exists(rootPath))
        {
            Directory.CreateDirectory(rootPath);
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

    [HttpPost("save")]
    public IActionResult SavePageContent([FromQuery] string path, [FromBody] string content)
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
            // Säkerställ att katalogen finns
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            System.IO.File.WriteAllText(filePath, content);
            _logger.LogInformation("File saved: {Path}", path);
            return Ok();
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
            .Select(entry => new FileSystemEntity()
            {
                Name = Path.GetFileName(entry),
                Path = entry.Replace(rootPath, "").TrimStart(Path.DirectorySeparatorChar),
                Type = "directory",
                Children = GetDirectoryStructure(entry)
            });

        return files.Concat(directories);
    }
}