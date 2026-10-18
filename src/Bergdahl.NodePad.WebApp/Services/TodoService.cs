using System.Text.RegularExpressions;
using Bergdahl.NodePad.WebApp.Models;

namespace Bergdahl.NodePad.WebApp.Services;

/// <summary>
/// Named ToDo lists, stored as Markdown task lists in the _ToDo folder of the pages root
/// (one file per list). Checking an item appends its done date (" ✅ 2026-10-18");
/// done items older than <see cref="HideDoneAfterDays"/> days are hidden but stay in the file.
/// Items are addressed by their 0-based position among the task lines of the file.
/// </summary>
public class TodoService
{
    public const string FolderName = "_ToDo";
    public const int HideDoneAfterDays = 14;

    private static readonly Regex ItemPattern = new(@"^(?<indent>\s*)(?<bullet>[-*+])\s+\[(?<check>[ xX])\]\s?(?<text>.*)$");
    private static readonly Regex DoneDatePattern = new(@"\s*✅\s*(?<date>\d{4}-\d{2}-\d{2})\s*$");
    // Serializes read-modify-write of list files within this process
    private static readonly object WriteLock = new();

    private readonly NotesService _notes;
    private readonly TimeProvider _time;
    private readonly ILogger<TodoService> _logger;

    public TodoService(NotesService notes, TimeProvider time, ILogger<TodoService> logger)
    {
        _notes = notes;
        _time = time;
        _logger = logger;
    }

    private string FolderPath => Path.Combine(_notes.RootPath, FolderName);

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    // ---------------------------------------------------------------------
    // Lists
    // ---------------------------------------------------------------------

    /// <summary>
    /// All lists, sorted by name. Open items come first in file order, then the visible done items, newest first.
    /// </summary>
    public List<TodoList> GetLists(bool includeOldDone = false)
    {
        if (!Directory.Exists(FolderPath)) return new List<TodoList>();

        return Directory.EnumerateFiles(FolderPath, "*.md")
            .Select(file => BuildList(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file), includeOldDone))
            .OrderBy(list => list.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public TodoList GetList(string name, bool includeOldDone = false)
    {
        var path = RequireListPath(name);
        return BuildList(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path), includeOldDone);
    }

    public TodoList CreateList(string name)
    {
        var cleanName = CleanListName(name);
        lock (WriteLock)
        {
            Directory.CreateDirectory(FolderPath);
            var path = ListPath(cleanName);
            if (File.Exists(path)) throw NotesException.Conflict("A to do list with that name already exists");
            File.WriteAllText(path, $"# {cleanName}\n\n");
        }
        _logger.LogInformation("ToDo list created: {Name}", cleanName);
        _notes.RequestBackup();
        return GetList(cleanName);
    }

    /// <summary>
    /// Renames the list file and updates its "# " heading.
    /// </summary>
    public TodoList RenameList(string name, string newName, bool includeOldDone = false)
    {
        var cleanNewName = CleanListName(newName);
        lock (WriteLock)
        {
            var oldPath = RequireListPath(name);
            var newPath = ListPath(cleanNewName);
            var sameFile = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
            if (!sameFile && File.Exists(newPath)) throw NotesException.Conflict("A to do list with that name already exists");

            if (sameFile)
            {
                // Only the case changes; move through a temporary name for case-insensitive file systems
                var tempPath = newPath + ".renaming";
                File.Move(oldPath, tempPath);
                File.Move(tempPath, newPath);
            }
            else
            {
                File.Move(oldPath, newPath);
            }

            var lines = ReadLines(newPath, out var newline);
            var heading = lines.FindIndex(line => line.TrimStart().StartsWith("# "));
            if (heading >= 0)
            {
                lines[heading] = $"# {cleanNewName}";
                WriteLines(newPath, lines, newline);
            }
        }
        _logger.LogInformation("ToDo list renamed: {Old} -> {New}", name, cleanNewName);
        _notes.RequestBackup();
        return GetList(cleanNewName, includeOldDone);
    }

    public void DeleteList(string name)
    {
        lock (WriteLock)
        {
            File.Delete(RequireListPath(name));
        }
        _logger.LogInformation("ToDo list deleted: {Name}", name);
        _notes.RequestBackup();
    }

    // ---------------------------------------------------------------------
    // Items
    // ---------------------------------------------------------------------

    /// <summary>
    /// Adds an open item after the last item of the list.
    /// </summary>
    public TodoList AddItem(string list, string text, bool includeOldDone = false)
    {
        var cleanText = CleanItemText(text);
        return Modify(list, includeOldDone, (lines, items) =>
        {
            var line = $"- [ ] {cleanText}";
            if (items.Count > 0)
            {
                lines.Insert(items[^1] + 1, line);
            }
            else
            {
                // Keep a blank line between the heading and the first item
                while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0) lines.Add(string.Empty);
                lines.Add(line);
            }
        });
    }

    /// <summary>
    /// Changes an item's text and/or done state. Checking an item records today's date;
    /// unchecking removes the date. An item that is already done keeps its date.
    /// </summary>
    public TodoList UpdateItem(string list, int index, string? text = null, bool? done = null, bool includeOldDone = false)
    {
        var cleanText = text == null ? null : CleanItemText(text);
        return Modify(list, includeOldDone, (lines, items) =>
        {
            var lineIndex = RequireItemLine(items, index);
            var item = ParseItem(lines[lineIndex])!;
            var newDone = done ?? item.Done;
            DateOnly? date = newDone ? (item.Done ? item.DoneDate : Today) : null;
            lines[lineIndex] = FormatItem(item.Indent, item.Bullet, newDone, cleanText ?? item.Text, date);
        });
    }

    public TodoList DeleteItem(string list, int index, bool includeOldDone = false)
    {
        return Modify(list, includeOldDone, (lines, items) => lines.RemoveAt(RequireItemLine(items, index)));
    }

    // ---------------------------------------------------------------------
    // Parsing and formatting
    // ---------------------------------------------------------------------

    private sealed record ParsedItem(string Indent, string Bullet, bool Done, string Text, DateOnly? DoneDate);

    private static ParsedItem? ParseItem(string line)
    {
        var match = ItemPattern.Match(line);
        if (!match.Success) return null;

        var done = match.Groups["check"].Value != " ";
        var text = match.Groups["text"].Value.TrimEnd();
        DateOnly? date = null;
        var dateMatch = DoneDatePattern.Match(text);
        if (dateMatch.Success && DateOnly.TryParseExact(dateMatch.Groups["date"].Value, "yyyy-MM-dd", out var parsed))
        {
            date = parsed;
            text = text[..dateMatch.Index].TrimEnd();
        }
        return new ParsedItem(match.Groups["indent"].Value, match.Groups["bullet"].Value, done, text, done ? date : null);
    }

    private static string FormatItem(string indent, string bullet, bool done, string text, DateOnly? doneDate)
    {
        var line = $"{indent}{bullet} [{(done ? "x" : " ")}] {text}";
        return done && doneDate != null ? $"{line} ✅ {doneDate:yyyy-MM-dd}" : line;
    }

    private TodoList BuildList(string name, string content, bool includeOldDone)
    {
        var parsed = SplitLines(content, out _)
            .Select(ParseItem)
            .Where(item => item != null)
            .Select((item, index) => new TodoItem(index, item!.Text, item.Done, item.DoneDate))
            .ToList();

        var cutoff = Today.AddDays(-HideDoneAfterDays);
        bool IsOld(TodoItem item) => item.Done && item.DoneDate != null && item.DoneDate < cutoff;

        var visible = parsed.Where(item => !item.Done)
            .Concat(parsed.Where(item => item.Done && (includeOldDone || !IsOld(item)))
                // Newest first; done items without a date (written by hand) last
                .OrderByDescending(item => item.DoneDate ?? DateOnly.MinValue))
            .ToList();

        return new TodoList(name, visible, includeOldDone ? 0 : parsed.Count(IsOld));
    }

    /// <summary>
    /// Read-modify-write of one list file. The action gets the file lines and the line numbers of the items.
    /// </summary>
    private TodoList Modify(string list, bool includeOldDone, Action<List<string>, List<int>> change)
    {
        string path;
        lock (WriteLock)
        {
            path = RequireListPath(list);
            var lines = ReadLines(path, out var newline);
            var items = lines.Select((line, i) => (line, i))
                .Where(x => ParseItem(x.line) != null)
                .Select(x => x.i)
                .ToList();
            change(lines, items);
            WriteLines(path, lines, newline);
        }
        _notes.RequestBackup();
        return GetList(Path.GetFileNameWithoutExtension(path), includeOldDone);
    }

    private static int RequireItemLine(List<int> items, int index)
    {
        if (index < 0 || index >= items.Count) throw NotesException.NotFound("To do item not found");
        return items[index];
    }

    private static List<string> SplitLines(string content, out string newline)
    {
        newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        // A trailing newline gives an empty last entry; WriteLines adds it back
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static List<string> ReadLines(string path, out string newline) => SplitLines(File.ReadAllText(path), out newline);

    private static void WriteLines(string path, List<string> lines, string newline) =>
        File.WriteAllText(path, string.Join(newline, lines) + newline);

    // ---------------------------------------------------------------------
    // Names and paths
    // ---------------------------------------------------------------------

    private string ListPath(string cleanName) => Path.Combine(FolderPath, cleanName + ".md");

    private string RequireListPath(string name)
    {
        var path = ListPath(CleanListName(name));
        if (!File.Exists(path)) throw NotesException.NotFound("To do list not found");
        return path;
    }

    /// <summary>
    /// A list name is its file name without .md: invalid file name characters become "-",
    /// whitespace is collapsed and a leading "." is not allowed.
    /// </summary>
    private static string CleanListName(string? name)
    {
        var cleaned = name ?? string.Empty;
        if (cleaned.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[..^3];
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToHashSet();
        cleaned = new string(cleaned.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim().TrimStart('.').Trim();
        if (cleaned.Length == 0) throw NotesException.BadRequest("List name cannot be empty");
        if (cleaned.Length > 100) throw NotesException.BadRequest("List name is too long");
        return cleaned;
    }

    private static string CleanItemText(string? text)
    {
        var cleaned = (text ?? string.Empty).Trim();
        if (cleaned.Length == 0) throw NotesException.BadRequest("Item text cannot be empty");
        if (cleaned.Contains('\n') || cleaned.Contains('\r')) throw NotesException.BadRequest("Item text must be a single line");
        return cleaned;
    }
}
