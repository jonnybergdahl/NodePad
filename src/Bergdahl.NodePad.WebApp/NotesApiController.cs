using Microsoft.AspNetCore.Mvc;
using Bergdahl.NodePad.WebApp.Services;

namespace Bergdahl.NodePad.WebApp;

/// <summary>
/// Base for the notes and ToDo API controllers: maps <see cref="NotesException"/> and file system
/// failures to HTTP responses.
/// </summary>
public abstract class NotesApiController : ControllerBase
{
    private readonly ILogger _logger;

    protected NotesApiController(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Runs a notes operation and maps failures to HTTP responses.
    /// </summary>
    protected IActionResult Execute(string activity, string? target, Func<IActionResult> action)
    {
        try
        {
            return action();
        }
        catch (NotesException ex)
        {
            return ex.Kind switch
            {
                NotesErrorKind.NotFound => ex.UserMessage == null ? NotFound() : NotFound(ex.UserMessage),
                NotesErrorKind.Conflict => Conflict(ex.UserMessage),
                _ => BadRequest(ex.UserMessage),
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when {Activity}: {Target}", activity, target);
            return StatusCode(403, "Access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error {Activity}: {Target}", activity, target);
            return StatusCode(500, "Internal server error");
        }
    }
}
