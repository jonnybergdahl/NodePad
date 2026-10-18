using System.ComponentModel;
using Bergdahl.NodePad.WebApp.Models;
using Bergdahl.NodePad.WebApp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Bergdahl.NodePad.WebApp.Mcp;

/// <summary>
/// MCP tools for reading and editing NodePad notes. There is deliberately no delete tool.
/// </summary>
[McpServerToolType]
public class NotesTools
{
    public const string Instructions =
        "NodePad is a Markdown notes app. Notes are .md files in a folder tree. " +
        "Paths are relative to the notes root, use forward slashes and must end in .md for notes (e.g. 'Projects/NodePad/ideas.md'); " +
        "folder paths have no extension and an empty string means the root folder. " +
        "A note's title is its first '# ' heading. Tags are stored separately from the content, normalized to lowercase. " +
        "Use search_notes or list_notes to find notes before reading or editing, and read a note before updating it, " +
        "since update_note replaces the whole content. Notes cannot be deleted through this server. " +
        "There are also named ToDo lists (list_todos, create_todo_list, add_todo, set_todo_done); items are addressed by " +
        "the list name and the item's index as returned by list_todos, and done items older than two weeks are hidden unless asked for.";

    public record NoteContent(string Path, string Title, string[] Tags, DateTime LastModifiedUtc, string Content);

    [McpServerTool(Name = "search_notes", Title = "Search notes", ReadOnly = true, OpenWorld = false)]
    [Description("Searches note names, paths, titles and content. Returns matching notes with a snippet, best matches first.")]
    public static List<SearchHit> SearchNotes(
        NotesService notes,
        [Description("Text to search for, at least 2 characters.")] string query,
        [Description("Optional comma separated tags; only notes with all of them are returned.")] string? tags = null,
        [Description("Maximum number of results (1-200).")] int limit = 20)
    {
        return Run(() => notes.Search(query, tags, limit));
    }

    [McpServerTool(Name = "list_notes", Title = "List notes", ReadOnly = true, OpenWorld = false)]
    [Description("Returns the folder tree with all notes and their titles, sorted alphabetically.")]
    public static List<FileSystemEntity> ListNotes(
        NotesService notes,
        [Description("Optional comma separated tags; only notes with all of them (and their folders) are returned.")] string? tags = null)
    {
        return Run(() => notes.GetStructure(sorted: true, dirsFirst: true, tags: tags, includeTitles: true));
    }

    [McpServerTool(Name = "read_note", Title = "Read note", ReadOnly = true, OpenWorld = false)]
    [Description("Returns a note's Markdown content together with its title, tags and last modified time.")]
    public static NoteContent ReadNote(
        NotesService notes,
        [Description("Path of the note, e.g. 'Folder/note.md'.")] string path)
    {
        return Run(() =>
        {
            var content = notes.ReadContent(path);
            var info = notes.GetInfo(path);
            return new NoteContent(Normalize(path), info.Title, info.Tags, info.LastModifiedUtc, content);
        });
    }

    [McpServerTool(Name = "recent_notes", Title = "Recent notes", ReadOnly = true, OpenWorld = false)]
    [Description("Lists the most recently modified notes, newest first.")]
    public static List<RecentNote> RecentNotes(
        NotesService notes,
        [Description("Maximum number of notes (1-100).")] int limit = 10,
        [Description("Optional comma separated tags; only notes with all of them are returned.")] string? tags = null)
    {
        return Run(() => notes.GetRecent(limit, tags));
    }

    [McpServerTool(Name = "list_tags", Title = "List tags", ReadOnly = true, OpenWorld = false)]
    [Description("Lists all tags in use with the number of notes that have each tag, most used first.")]
    public static List<TagCount> ListTags(NotesService notes)
    {
        return Run(notes.GetTagCounts);
    }

    [McpServerTool(Name = "create_note", Title = "Create note", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates a new note. Missing folders are created. Fails if a note already exists at the path; use update_note to change an existing note.")]
    public static NoteSummary CreateNote(
        NotesService notes,
        [Description("Path of the new note, ending in .md, e.g. 'Folder/new-note.md'.")] string path,
        [Description("Markdown content. Start with a '# Title' heading.")] string content,
        [Description("Optional tags for the note.")] string[]? tags = null)
    {
        return Run(() =>
        {
            if (notes.NoteExists(path))
                throw NotesException.Conflict($"A note already exists at '{Normalize(path)}'. Use update_note to change it.");
            notes.Save(path, content, tags ?? Array.Empty<string>());
            return new NoteSummary(Normalize(path), notes.GetInfo(path).Title);
        });
    }

    [McpServerTool(Name = "update_note", Title = "Update note", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Replaces the full content of an existing note, and optionally its tags. Read the note first so nothing is lost.")]
    public static NoteSummary UpdateNote(
        NotesService notes,
        [Description("Path of the existing note, e.g. 'Folder/note.md'.")] string path,
        [Description("The complete new Markdown content of the note.")] string content,
        [Description("New tags, replacing the current ones. Omit to keep the current tags.")] string[]? tags = null)
    {
        return Run(() =>
        {
            if (!notes.NoteExists(path))
                throw NotesException.NotFound($"No note exists at '{Normalize(path)}'. Use create_note to create it.");
            notes.Save(path, content, tags);
            return new NoteSummary(Normalize(path), notes.GetInfo(path).Title);
        });
    }

    [McpServerTool(Name = "set_note_tags", Title = "Set note tags", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Replaces the tags of an existing note without changing its content.")]
    public static PageInfo SetNoteTags(
        NotesService notes,
        [Description("Path of the existing note, e.g. 'Folder/note.md'.")] string path,
        [Description("The new tags; an empty list removes all tags.")] string[] tags)
    {
        return Run(() =>
        {
            notes.Save(path, notes.ReadContent(path), tags);
            return notes.GetInfo(path);
        });
    }

    [McpServerTool(Name = "create_folder", Title = "Create folder", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates a folder (and any missing parent folders).")]
    public static string CreateFolder(
        NotesService notes,
        [Description("Path of the new folder, e.g. 'Projects/NodePad'.")] string path)
    {
        return Run(() =>
        {
            notes.CreateDirectory(path);
            return $"Folder '{Normalize(path)}' created.";
        });
    }

    [McpServerTool(Name = "move", Title = "Move note or folder", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Moves a note (with its tags) or a folder into another folder. Fails if the destination already has an entry with the same name.")]
    public static MoveResult Move(
        NotesService notes,
        [Description("Path of the note or folder to move.")] string source,
        [Description("Destination folder; an empty string means the root folder.")] string destination)
    {
        return Run(() => notes.Move(source, destination));
    }

    [McpServerTool(Name = "rename", Title = "Rename note or folder", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Renames a note (with its tags) or a folder in place. For notes, .md is added if missing.")]
    public static MoveResult Rename(
        NotesService notes,
        [Description("Path of the note or folder to rename.")] string path,
        [Description("New name only, without folders.")] string newName)
    {
        return Run(() => notes.Rename(path, newName));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Turns expected failures into tool errors the model can read and act on.
    /// </summary>
    internal static T Run<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (NotesException ex)
        {
            throw new McpException(ex.UserMessage ?? ex.Kind switch
            {
                NotesErrorKind.NotFound => "Not found",
                NotesErrorKind.Conflict => "Conflict",
                _ => "Invalid request",
            });
        }
        catch (UnauthorizedAccessException)
        {
            throw new McpException("Access denied");
        }
    }
}
