using System.Text.Json.Serialization;

namespace Bergdahl.NodePad.WebApp.Models;

public record Breadcrumb(string Name, string Path);

public record SearchHighlight(int Start, int Length);

public record SearchHit(string Path, string Title, string Snippet, int Score, IReadOnlyList<SearchHighlight> Highlights);

public record RecentNote(string Path, string Title, DateTime LastModifiedUtc);

public record NoteSummary(string Path, string Title);

public record PageInfo(string Title, string[] Tags, DateTime LastModifiedUtc, long SizeBytes);

public record MoveResult(string OldPath, string Path, string Type);

public record TagCount(string Tag, int Count);

public record NameValidation(
    bool Valid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SuggestedName = null);
