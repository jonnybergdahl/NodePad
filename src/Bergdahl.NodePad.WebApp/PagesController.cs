using Microsoft.AspNetCore.Mvc;
using Bergdahl.NodePad.WebApp.Services;

namespace Bergdahl.NodePad.WebApp;

[ApiController]
[Route("api/pages")]
public class PagesController: NotesApiController
{
    private readonly NotesService _notes;

    public PagesController(NotesService notes, ILogger<PagesController> logger) : base(logger)
    {
        _notes = notes;
    }

    [HttpGet("structure")]
    public IActionResult GetFolderStructure([FromQuery] bool sorted = false, [FromQuery] bool dirsFirst = true, [FromQuery] string? tags = null, [FromQuery] bool includeCounts = false, [FromQuery] bool includeTitles = false)
    {
        return Execute("reading folder structure", null,
            () => Ok(_notes.GetStructure(sorted, dirsFirst, tags, includeCounts, includeTitles)));
    }

    [HttpGet("content")]
    public IActionResult GetPageContent([FromQuery] string path, [FromQuery] bool includeMeta = false)
    {
        return Execute("reading file", path, () =>
        {
            var content = _notes.ReadContent(path);
            if (!includeMeta)
            {
                // Backward compatibility: plain text as before
                return Ok(content);
            }
            var breadcrumbs = _notes.GetBreadcrumbs(path);
            return Ok(new { content, breadcrumbs });
        });
    }

    [HttpGet("meta")]
    public IActionResult GetPageMeta([FromQuery] string path, [FromQuery] bool includeNormalized = false)
    {
        return Execute("reading meta", path, () =>
        {
            var display = _notes.ReadTags(path);
            if (!includeNormalized)
            {
                return Ok(display);
            }
            var normalized = NotesService.NormalizeTags(display);
            return Ok(new { display, normalized });
        });
    }

    [HttpPost("save")]
    [Consumes("text/plain")]
    public IActionResult SavePageContent([FromQuery] string path, [FromBody] string content, [FromQuery] string? tags)
    {
        return Execute("saving file", path, () =>
        {
            _notes.Save(path, content, tags != null ? NotesService.SplitTags(tags) : null);
            return Content(content, "text/plain");
        });
    }

    // JSON variant for structured tags and content at the same route
    [HttpPost("save")]
    [Consumes("application/json")]
    public IActionResult SavePageContentJson([FromQuery] string path, [FromBody] Models.SavePageRequest request)
    {
        if (request == null)
            return BadRequest("Request body cannot be null");

        return Execute("saving file (JSON)", path, () =>
        {
            _notes.Save(path, request.Content!, request.Tags);
            return Content(request.Content!, "text/plain");
        });
    }

    [HttpPost("create")]
    public IActionResult CreateEntity([FromQuery] string path, [FromQuery] string type)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Path cannot be empty");

        if (string.IsNullOrWhiteSpace(type) || (type != "file" && type != "directory"))
            return BadRequest("Type must be 'file' or 'directory'");

        return Execute($"creating {type}", path, () =>
        {
            if (type == "file")
                _notes.CreateFile(path);
            else
                _notes.CreateDirectory(path);
            return Ok();
        });
    }

    [HttpDelete("delete")]
    public IActionResult DeleteEntity([FromQuery] string path, [FromQuery] bool recursive = false)
    {
        return Execute("deleting", path, () =>
        {
            _notes.Delete(path, recursive);
            return Ok();
        });
    }

    [HttpGet("search")]
    public IActionResult Search([FromQuery] string query, [FromQuery] string? tags = null, [FromQuery] int limit = 100)
    {
        return Execute("searching", query, () => Ok(_notes.Search(query, tags, limit)));
    }

    [HttpGet("recent")]
    public IActionResult GetRecent([FromQuery] int limit = 20, [FromQuery] string? tags = null)
    {
        return Execute("listing recent notes", null, () => Ok(_notes.GetRecent(limit, tags)));
    }

    [HttpGet("untagged")]
    public IActionResult GetUntagged()
    {
        return Execute("listing untagged notes", null, () => Ok(_notes.GetUntagged()));
    }

    // Tags endpoints
    [HttpGet("tags-index")]
    public IActionResult GetTagsIndex()
    {
        return Execute("building tags index", null, () => Ok(_notes.GetTagsIndex()));
    }

    [HttpGet("tags")]
    public IActionResult GetTags()
    {
        return Execute("aggregating tags", null, () => Ok(_notes.GetTagCounts()));
    }

    [HttpGet("tags/suggest")]
    public IActionResult SuggestTags([FromQuery] string? prefix)
    {
        return Execute("suggesting tags", prefix, () => Ok(_notes.SuggestTags(prefix)));
    }

    [HttpGet("breadcrumbs")]
    public IActionResult GetBreadcrumbsOnly([FromQuery] string path)
    {
        return Execute("computing breadcrumbs", path, () => Ok(_notes.GetBreadcrumbs(path)));
    }

    [HttpPost("move")]
    public IActionResult Move([FromQuery] string source, [FromQuery] string? destination)
    {
        return Execute("moving", $"{source} -> {destination}", () => Ok(_notes.Move(source, destination)));
    }

    [HttpPost("rename")]
    public IActionResult Rename([FromQuery] string path, [FromQuery] string newName)
    {
        return Execute("renaming", path, () => Ok(_notes.Rename(path, newName)));
    }

    [HttpGet("validate-name")]
    public IActionResult ValidateName([FromQuery] string? path, [FromQuery] string type, [FromQuery] string name)
    {
        return Execute("validating name", name, () => Ok(_notes.ValidateName(path, type, name)));
    }

    [HttpGet("info")]
    public IActionResult GetPageInfo([FromQuery] string path)
    {
        return Execute("reading info", path, () => Ok(_notes.GetInfo(path)));
    }
}
