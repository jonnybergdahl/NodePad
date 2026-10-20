namespace Bergdahl.NodePad.WebApp.Services;

/// <summary>
/// Tells open browser pages that the ToDo lists changed, whoever changed them: the REST API,
/// the MCP tools or the Home Assistant sync. <see cref="TodoService"/> calls <see cref="Notify"/>
/// after every write; the /api/todos/events stream passes it on.
/// </summary>
public class TodoChangeNotifier
{
    private long _version;

    /// <summary>Counts the changes since the app started.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Raised after a change, with the new <see cref="Version"/>.</summary>
    public event Action<long>? Changed;

    public void Notify()
    {
        var version = Interlocked.Increment(ref _version);
        Changed?.Invoke(version);
    }
}
