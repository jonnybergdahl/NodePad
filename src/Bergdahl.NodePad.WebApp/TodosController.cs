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
