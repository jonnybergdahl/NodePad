using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Linq;
using Bergdahl.NodePad.WebApp.Models;

namespace Bergdahl.NodePad.WebApp;

[ApiController]
[Route("api/pages")]
public class PagesController: ControllerBase
{
    private readonly string rootPath = Path.Combine(Directory.GetCurrentDirectory(), "Pages");

    [HttpGet("structure")]
    public IActionResult GetFolderStructure()
    {
        var structure = GetDirectoryStructure(rootPath);
        return Ok(structure);
    }

    [HttpGet("content")]
    public IActionResult GetPageContent([FromQuery] string path)
    {
        var filePath = Path.Combine(rootPath, path);
        if (!System.IO.File.Exists(filePath)) return NotFound();
        var content = System.IO.File.ReadAllText(filePath);
        return Ok(content);
    }

    [HttpPost("save")]
    public IActionResult SavePageContent([FromQuery] string path, [FromBody] string content)
    {
        var filePath = Path.Combine(rootPath, path);
        System.IO.File.WriteAllText(filePath, content);
        return Ok();
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
