using System.ComponentModel;
using Bergdahl.NodePad.WebApp.Models;
using Bergdahl.NodePad.WebApp.Services;
using ModelContextProtocol.Server;

namespace Bergdahl.NodePad.WebApp.Mcp;

/// <summary>
/// MCP tools for NodePad's ToDo lists. Single items can be removed, but there is deliberately no tool
/// that deletes a whole list (or a note).
/// </summary>
[McpServerToolType]
public class TodoTools
{
    [McpServerTool(Name = "list_todos", Title = "List ToDo lists", ReadOnly = true, OpenWorld = false)]
    [Description("Returns all ToDo lists with their items. Each item has an index (used by set_todo_done, edit_todo and remove_todo), its text, " +
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

    [McpServerTool(Name = "edit_todo", Title = "Edit ToDo item", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Changes an item's text and returns the updated list. The done state and done date are kept. " +
                 "Get the index from list_todos first, since indexes change when items are added or removed.")]
    public static TodoList EditTodo(
        TodoService todos,
        [Description("Name of the list.")] string list,
        [Description("Index of the item, as returned by list_todos.")] int index,
        [Description("New text of the item, a single line.")] string text)
    {
        return NotesTools.Run(() => todos.UpdateItem(list, index, text: text));
    }

    [McpServerTool(Name = "remove_todo", Title = "Remove ToDo item", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Deletes one item from a list and returns the updated list. The indexes of later items move down by one. " +
                 "Get the index from list_todos first, and prefer set_todo_done for finished items, since done items keep their date.")]
    public static TodoList RemoveTodo(
        TodoService todos,
        [Description("Name of the list.")] string list,
        [Description("Index of the item, as returned by list_todos.")] int index)
    {
        return NotesTools.Run(() => todos.DeleteItem(list, index));
    }
}
