using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Bergdahl.NodePad.WebApp.Services;

namespace Bergdahl.NodePad.WebApp;

/// <summary>
/// ToDo lists. Changes return the updated list; includeOldDone=true also returns done items older than two weeks.
/// </summary>
[ApiController]
[Route("api/todos")]
public class TodosController : NotesApiController
{
    private readonly TodoService _todos;

    public record ListRequest(string? Name);

    public record ItemRequest(string? Text, bool? Done);

    public TodosController(TodoService todos, ILogger<TodosController> logger) : base(logger)
    {
        _todos = todos;
    }

    [HttpGet]
    public IActionResult GetLists([FromQuery] bool includeOldDone = false)
    {
        return Execute("reading ToDo lists", null, () => Ok(_todos.GetLists(includeOldDone)));
    }

    /// <summary>
    /// Server-sent events: a "data: &lt;version&gt;" message whenever a list changes, from any source
    /// (this API, the MCP tools, Home Assistant). The page reloads the lists when it gets one.
    /// </summary>
    [HttpGet("events")]
    public async Task Events([FromServices] TodoChangeNotifier changes, [FromServices] IHostApplicationLifetime lifetime)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        // Holds only the latest version, so changes made while a message is being sent become one message
        var pending = Channel.CreateBounded<long>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnChanged(long version) => pending.Writer.TryWrite(version);
        changes.Changed += OnChanged;

        // End the stream when the browser goes away or the app shuts down
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, lifetime.ApplicationStopping);
        var ct = stop.Token;
        try
        {
            await WriteEvent($"retry: {RetryMilliseconds}\n: connected\n\n", ct);
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(PingInterval);
                try
                {
                    await pending.Reader.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Keeps proxies from closing an idle connection
                    await WriteEvent(": ping\n\n", ct);
                    continue;
                }
                // Let a burst of changes (a Home Assistant sync of several items) settle into one message
                await Task.Delay(CoalesceDelay, ct);
                if (pending.Reader.TryRead(out var version)) await WriteEvent($"data: {version}\n\n", ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The browser closed the page or the app is stopping
        }
        finally
        {
            changes.Changed -= OnChanged;
        }
    }

    private const int RetryMilliseconds = 5000;
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(250);

    private async Task WriteEvent(string text, CancellationToken ct)
    {
        await Response.WriteAsync(text, ct);
        await Response.Body.FlushAsync(ct);
    }

    [HttpPost("lists")]
    public IActionResult CreateList([FromBody] ListRequest request)
    {
        return Execute("creating ToDo list", request.Name, () => Ok(_todos.CreateList(request.Name ?? string.Empty)));
    }

    [HttpPost("lists/rename")]
    public IActionResult RenameList([FromQuery] string name, [FromQuery] string newName, [FromQuery] bool includeOldDone = false)
    {
        return Execute("renaming ToDo list", name, () => Ok(_todos.RenameList(name, newName, includeOldDone)));
    }

    [HttpDelete("lists")]
    public IActionResult DeleteList([FromQuery] string name)
    {
        return Execute("deleting ToDo list", name, () =>
        {
            _todos.DeleteList(name);
            return Ok();
        });
    }

    [HttpPost("items")]
    public IActionResult AddItem([FromQuery] string list, [FromBody] ItemRequest request, [FromQuery] bool includeOldDone = false)
    {
        return Execute("adding ToDo item", list, () => Ok(_todos.AddItem(list, request.Text ?? string.Empty, includeOldDone)));
    }

    [HttpPut("items")]
    public IActionResult UpdateItem([FromQuery] string list, [FromQuery] int index, [FromBody] ItemRequest request, [FromQuery] bool includeOldDone = false)
    {
        return Execute("updating ToDo item", list, () => Ok(_todos.UpdateItem(list, index, request.Text, request.Done, includeOldDone)));
    }

    [HttpDelete("items")]
    public IActionResult DeleteItem([FromQuery] string list, [FromQuery] int index, [FromQuery] bool includeOldDone = false)
    {
        return Execute("deleting ToDo item", list, () => Ok(_todos.DeleteItem(list, index, includeOldDone)));
    }
}
