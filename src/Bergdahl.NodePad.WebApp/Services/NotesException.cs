namespace Bergdahl.NodePad.WebApp.Services;

public enum NotesErrorKind
{
    BadRequest,
    NotFound,
    Conflict,
}

/// <summary>
/// An expected failure in a notes operation (invalid input, missing entry, name clash).
/// The message is safe to show to the caller; it is null when there is nothing to add.
/// </summary>
public class NotesException : Exception
{
    public NotesErrorKind Kind { get; }
    public string? UserMessage { get; }

    public NotesException(NotesErrorKind kind, string? message = null)
        : base(message ?? kind.ToString())
    {
        Kind = kind;
        UserMessage = message;
    }

    public static NotesException BadRequest(string message) => new(NotesErrorKind.BadRequest, message);
    public static NotesException NotFound(string? message = null) => new(NotesErrorKind.NotFound, message);
    public static NotesException Conflict(string message) => new(NotesErrorKind.Conflict, message);
}
