using System.ComponentModel;
using Bergdahl.NodePad.WebApp.Models;
using Bergdahl.NodePad.WebApp.Services;
using ModelContextProtocol.Server;

namespace Bergdahl.NodePad.WebApp.Mcp;

/// <summary>
/// MCP tools for NodePad's ToDo lists. Like the note tools, there is deliberately no delete tool.
/// </summary>
[McpServerToolType]
public class TodoTools
{
    [McpServerTool(Name = "list_todos", Title = "List ToDo lists", ReadOnly = true, OpenWorld = false)]
    [Description("Returns all ToDo lists with their items. Each item has an index (used by set_todo_done), its text, " +
                 "whether it is done and the date it was done. Open items come first. Done items older than two weeks " +
                 "are left out (hiddenDoneCount says how many) unless includeOldDone is true.")]
    public static List<TodoList> ListTodos(
        TodoService todos,
        [Description("Also return done items older than two weeks.")] bool includeOldDone = false)
    {
        return NotesTools.Run(() => todos.GetLists(includeOldDone));
    }

    [McpServerTool(Name = "create_todo_list", Title = "Create ToDo list", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates a new, empty ToDo list. Fails if a list with that name already exists.")]
    public static TodoList CreateTodoList(
        TodoService todos,
        [Description("Name of the new list, e.g. 'Groceries'.")] string name)
    {
        return NotesTools.Run(() => todos.CreateList(name));
    }

    [McpServerTool(Name = "add_todo", Title = "Add ToDo item", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds an open item to the end of a ToDo list and returns the updated list.")]
    public static TodoList AddTodo(
        TodoService todos,
        [Description("Name of the list.")] string list,
        [Description("Text of the item, a single line.")] string text)
    {
        return NotesTools.Run(() => todos.AddItem(list, text));
    }

    [McpServerTool(Name = "set_todo_done", Title = "Check or uncheck ToDo item", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Checks (done=true, records today's date) or unchecks an item and returns the updated list. " +
                 "Get the index from list_todos first, since indexes change when items are added or removed.")]
    public static TodoList SetTodoDone(
        TodoService todos,
        [Description("Name of the list.")] string list,
        [Description("Index of the item, as returned by list_todos.")] int index,
        [Description("True to check the item, false to uncheck it.")] bool done = true)
    {
        return NotesTools.Run(() => todos.UpdateItem(list, index, done: done));
    }
}
