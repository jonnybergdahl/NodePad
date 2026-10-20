using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Bergdahl.NodePad.WebApp.Models;
using static Bergdahl.NodePad.WebApp.Services.HomeAssistantMerge;

namespace Bergdahl.NodePad.WebApp.Services;

/// <summary>
/// Keeps the ToDo lists in step with Home Assistant, both ways. Each list has a Local To-do list with the
/// same name in Home Assistant holding the items NodePad shows (open items, and done items from the last
/// two weeks). Item changes on either side are merged (see <see cref="HomeAssistantMerge"/>); lists are
/// created, renamed and deleted from NodePad only.
/// <para>
/// A WebSocket connection reports changes made in Home Assistant as they happen. Every time it connects,
/// at startup and after a lost connection, all lists are synced, which also catches up on changes made
/// while not connected. The items as they were after the last sync are kept in _ToDo/.homeassistant.json.
/// Sync jobs run one at a time in the background, so a slow or unreachable Home Assistant never holds up NodePad.
/// </para>
/// </summary>
public class HomeAssistantSync : BackgroundService
{
    public const string StateFileName = ".homeassistant.json";

    private abstract record Job;
    private sealed record SyncListJob(string Name) : Job;
    private sealed record DeleteListJob(string Name) : Job;
    private sealed record SyncAllJob : Job;

    public sealed record SyncStatus(bool Connected, DateTimeOffset? LastSync, string? LastError);

    private static readonly TimeSpan EntityWait = TimeSpan.FromMilliseconds(500);
    private const int EntityWaitAttempts = 10;
    private static readonly TimeSpan MinRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HomeAssistantSettingsStore _settings;
    private readonly IHttpClientFactory _httpClients;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<HomeAssistantSync> _logger;
    private int _pending;

    // Lists with a queued sync job; true when NodePad changed the list, false when only Home Assistant reported a change
    private readonly Dictionary<string, bool> _queuedLists = new(StringComparer.OrdinalIgnoreCase);
    // The items Home Assistant last reported for each list
    private readonly Dictionary<string, List<HomeAssistantClient.Item>> _reported = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sessionLock = new();
    private CancellationTokenSource? _session;
    private HomeAssistantSettingsStore.Settings? _sessionSettings;
    private volatile HomeAssistantSocket? _socket;

    private readonly object _statusLock = new();
    private SyncStatus _status = new(false, null, null);

    public HomeAssistantSync(HomeAssistantSettingsStore settings, IHttpClientFactory httpClients, IServiceScopeFactory scopes,
        TimeProvider time, ILogger<HomeAssistantSync> logger)
    {
        _settings = settings;
        _httpClients = httpClients;
        _scopes = scopes;
        _time = time;
        _logger = logger;
        _settings.Changed += OnSettingsChanged;
    }

    public SyncStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    // ---------------------------------------------------------------------
    // Requests; ignored while syncing is off
    // ---------------------------------------------------------------------

    public void ListChanged(string name) => QueueListSync(name, changedInNodePad: true);

    public void ListDeleted(string name) => Enqueue(new DeleteListJob(name));

    /// <summary>
    /// Syncs every list and deletes the Home Assistant lists of deleted NodePad lists.
    /// </summary>
    public void SyncAll() => Enqueue(new SyncAllJob());

    /// <summary>
    /// Waits until all queued jobs have run. Used by tests.
    /// </summary>
    public async Task WaitForIdleAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pending) > 0)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Home Assistant sync did not finish");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Returns null when Home Assistant answers with the current settings, otherwise what went wrong.
    /// </summary>
    public async Task<string?> CheckConnectionAsync(CancellationToken ct)
    {
        var settings = _settings.Current;
        if (string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.Token)) return "Address and access token are required";
        try
        {
            await CreateClient(settings).CheckAsync(ct);
            return null;
        }
        catch (HomeAssistantException ex)
        {
            return ex.Message;
        }
    }

    private void QueueListSync(string name, bool changedInNodePad)
    {
        if (!_settings.Current.IsUsable) return;
        lock (_queuedLists)
        {
            // One queued job per list is enough, it reads both sides when it runs
            if (_queuedLists.TryGetValue(name, out var queued))
            {
                _queuedLists[name] = queued || changedInNodePad;
                return;
            }
            _queuedLists[name] = changedInNodePad;
        }
        Enqueue(new SyncListJob(name), checkSettings: false);
    }

    private void Enqueue(Job job, bool checkSettings = true)
    {
        if (checkSettings && !_settings.Current.IsUsable) return;
        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(job)) Interlocked.Decrement(ref _pending);
    }

    /// <summary>
    /// Takes a list off the queued set; true if NodePad changed it since it was queued.
    /// </summary>
    private bool TakeQueuedList(string name)
    {
        lock (_queuedLists)
        {
            return !_queuedLists.Remove(name, out var changedInNodePad) || changedInNodePad;
        }
    }

    private void UpdateStatus(Func<SyncStatus, SyncStatus> change)
    {
        lock (_statusLock) _status = change(_status);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(WorkAsync(stoppingToken), ConnectAsync(stoppingToken));

    // ---------------------------------------------------------------------
    // Connection
    // ---------------------------------------------------------------------

    private void OnSettingsChanged()
    {
        var current = _settings.Current;
        lock (_sessionLock)
        {
            // Reconnect with the new settings, or disconnect if syncing was turned off
            if (_session != null && current != _sessionSettings) _session.Cancel();
        }
    }

    /// <summary>
    /// Keeps a connection open while syncing is on, reconnecting after a growing delay when it fails.
    /// </summary>
    private async Task ConnectAsync(CancellationToken stoppingToken)
    {
        var retry = MinRetry;
        TimeSpan? wait = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            HomeAssistantSettingsStore.Settings settings;
            lock (_sessionLock)
            {
                settings = _settings.Current;
                _sessionSettings = settings;
                _session = session;
            }

            try
            {
                if (!settings.IsUsable)
                {
                    // Until the settings change
                    await Task.Delay(Timeout.Infinite, session.Token);
                }
                if (wait != null) await Task.Delay(wait.Value, session.Token);

                await using var socket = await HomeAssistantSocket.ConnectAsync(settings.Url!, settings.Token!, _logger, session.Token);
                _socket = socket;
                UpdateStatus(s => s with { Connected = true });
                _logger.LogInformation("Connected to Home Assistant at {Url}", settings.Url);
                retry = MinRetry;

                // Catch up on changes made on either side while not connected
                SyncAll();
                await socket.RunAsync(OnItemsReported, session.Token);
                throw new HomeAssistantException("Home Assistant closed the connection");
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                // The settings changed, or shutting down
                wait = null;
                retry = MinRetry;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Home Assistant connection failed, retrying in {Delay}: {Error}", retry, ex.Message);
                UpdateStatus(s => s with { LastError = ex.Message });
                wait = retry;
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
            }
            finally
            {
                _socket = null;
                UpdateStatus(s => s with { Connected = false });
                lock (_sessionLock) _session = null;
                session.Dispose();
            }
        }
    }

    private void OnItemsReported(string listName, List<HomeAssistantClient.Item> items)
    {
        lock (_reported) _reported[listName] = items;
        QueueListSync(listName, changedInNodePad: false);
    }

    // ---------------------------------------------------------------------
    // Worker
    // ---------------------------------------------------------------------

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // Settings may have changed since the job was queued
                    var settings = _settings.Current;
                    if (job is SyncListJob queued && !TakeQueuedList(queued.Name) && !ReportedChange(queued.Name))
                    {
                        // Only Home Assistant's own echo of the last sync
                    }
                    else if (settings.IsUsable)
                    {
                        await RunAsync(CreateClient(settings), job, stoppingToken);
                        UpdateStatus(s => s with { LastSync = _time.GetUtcNow(), LastError = null });
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Home Assistant sync failed for {Job}", job);
                    UpdateStatus(s => s with { LastError = ex.Message });
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down
        }
    }

    /// <summary>
    /// True unless Home Assistant last reported exactly the items of the last sync, which is what
    /// it does right after subscribing and after the changes a sync makes there.
    /// </summary>
    private bool ReportedChange(string name)
    {
        List<HomeAssistantClient.Item>? reported;
        lock (_reported) _reported.TryGetValue(name, out reported);
        return reported == null || !SameAsBase(LoadState().Get(name), reported);
    }

    private HomeAssistantClient CreateClient(HomeAssistantSettingsStore.Settings settings) =>
        new(_httpClients.CreateClient(HomeAssistantClient.HttpClientName), settings.Url!, settings.Token!);

    private async Task RunAsync(HomeAssistantClient ha, Job job, CancellationToken ct)
    {
        var state = LoadState();
        switch (job)
        {
            case SyncListJob sync:
                await SyncListAsync(ha, sync.Name, state, ct);
                break;

            case DeleteListJob delete:
                await DeleteRemoteListAsync(ha, delete.Name, ct);
                state.Remove(delete.Name);
                break;

            case SyncAllJob:
                string? firstError = null;
                var lists = WithTodos(todos => todos.GetLists());
                foreach (var each in lists ?? new List<TodoList>())
                {
                    try
                    {
                        await SyncListAsync(ha, each.Name, state, ct);
                    }
                    catch (HomeAssistantException ex)
                    {
                        // Keep going with the other lists
                        _logger.LogWarning("Home Assistant sync failed for {Name}: {Error}", each.Name, ex.Message);
                        firstError ??= ex.Message;
                    }
                }
                if (lists != null)
                {
                    // Lists synced before but deleted in NodePad since, for example while not connected
                    var names = lists.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var gone in state.Names.Where(n => !names.Contains(n)).ToList())
                    {
                        await DeleteRemoteListAsync(ha, gone, ct);
                        state.Remove(gone);
                    }
                }
                state.Save();
                if (firstError != null) throw new HomeAssistantException(firstError);
                break;
        }
        state.Save();
    }

    /// <summary>
    /// Runs an action with a scoped <see cref="TodoService"/>; null if the list is gone.
    /// </summary>
    private T? WithTodos<T>(Func<TodoService, T> action) where T : class
    {
        using var scope = _scopes.CreateScope();
        try
        {
            return action(scope.ServiceProvider.GetRequiredService<TodoService>());
        }
        catch (NotesException)
        {
            return null;
        }
    }

    private SyncState LoadState()
    {
        using var scope = _scopes.CreateScope();
        var root = scope.ServiceProvider.GetRequiredService<NotesService>().RootPath;
        return SyncState.Load(Path.Combine(root, TodoService.FolderName), _logger);
    }

    private async Task SyncListAsync(HomeAssistantClient ha, string listName, SyncState state, CancellationToken ct)
    {
        var list = WithTodos(todos => todos.GetList(listName));
        // Gone again by the time the job runs; its delete job follows
        if (list == null) return;
        var name = list.Name;

        var (entityId, created) = await EnsureEntityAsync(ha, name, ct);
        // A list that is new in Home Assistant (or was deleted there) gets NodePad's items, it does not empty NodePad's list
        var baseItems = created ? new List<SyncedItem>() : state.Get(name);
        var remote = await ha.GetItemsAsync(entityId, ct);

        Plan? plan = null;
        var updated = WithTodos(todos => todos.ApplyEdits(name, current =>
        {
            plan = Merge(baseItems, current.Items, remote);
            return plan.Local;
        }));
        if (updated == null || plan == null) return;

        if (plan.RemoteRemove.Count > 0) await ha.RemoveItemsAsync(entityId, plan.RemoteRemove, ct);
        foreach (var (uid, done) in plan.RemoteSetDone) await ha.SetItemDoneAsync(entityId, uid, done, ct);
        foreach (var (text, _) in plan.RemoteAdd) await ha.AddItemAsync(entityId, text, ct);
        if (plan.ChangesRemote)
        {
            // Added items start out open; check off the ones that are done in NodePad
            var known = remote.Select(r => r.Uid).ToHashSet(StringComparer.Ordinal);
            remote = await ha.GetItemsAsync(entityId, ct);
            var added = DoneMismatches(updated.Items, remote).Where(m => !known.Contains(m.Uid)).ToList();
            foreach (var (uid, done) in added) await ha.SetItemDoneAsync(entityId, uid, done, ct);
            if (added.Count > 0) remote = await ha.GetItemsAsync(entityId, ct);
        }
        state.Set(name, Pair(updated.Items, remote));

        if (plan.Local.Count > 0 || plan.ChangesRemote)
        {
            _logger.LogInformation("Home Assistant sync of {Name}: {Local} change(s) here, {Remote} there",
                name, plan.Local.Count, plan.RemoteRemove.Count + plan.RemoteSetDone.Count + plan.RemoteAdd.Count);
        }
        await SubscribeAsync(entityId, name, created, ct);
    }

    /// <summary>
    /// The list's todo entity, creating the Local To-do list if needed; created is true if there was no entity.
    /// </summary>
    private async Task<(string EntityId, bool Created)> EnsureEntityAsync(HomeAssistantClient ha, string name, CancellationToken ct)
    {
        var entityId = await ha.FindTodoEntityAsync(name, ct);
        if (entityId != null) return (entityId, false);

        var exists = (await ha.GetLocalTodoEntriesAsync(ct))
            .Any(e => string.Equals(e.Title, name, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            await ha.CreateLocalTodoAsync(name, ct);
            _logger.LogInformation("Home Assistant to do list created: {Name}", name);
        }
        // The entity shows up once Home Assistant has set up the new entry
        for (var attempt = 1; entityId == null && attempt <= EntityWaitAttempts; attempt++)
        {
            entityId = await ha.FindTodoEntityAsync(name, ct);
            if (entityId == null && attempt < EntityWaitAttempts) await Task.Delay(EntityWait, ct);
        }
        if (entityId == null) throw new HomeAssistantException($"Home Assistant has no to do entity for \"{name}\"");
        return (entityId, true);
    }

    private async Task SubscribeAsync(string entityId, string name, bool resubscribe, CancellationToken ct)
    {
        var socket = _socket;
        if (socket == null) return;
        if (resubscribe) socket.Forget(entityId);
        try
        {
            await socket.SubscribeItemsAsync(entityId, name, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            // The connection dropped; reconnecting syncs and subscribes again
        }
    }

    /// <summary>
    /// Deletes the Local To-do list with the name, if there is one; other kinds of todo lists are left alone.
    /// </summary>
    private async Task DeleteRemoteListAsync(HomeAssistantClient ha, string name, CancellationToken ct)
    {
        var entry = (await ha.GetLocalTodoEntriesAsync(ct))
            .FirstOrDefault(e => string.Equals(e.Title, name, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;

        var entityId = await ha.FindTodoEntityAsync(name, ct);
        await ha.DeleteEntryAsync(entry.EntryId, ct);
        if (entityId != null) _socket?.Forget(entityId);
        lock (_reported) _reported.Remove(name);
        _logger.LogInformation("Home Assistant to do list deleted: {Name}", name);
    }

    // ---------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------

    /// <summary>
    /// The items of each list as they were after its last sync, in _ToDo/.homeassistant.json.
    /// The file name starts with a dot and is not Markdown, so it does not show up as a note or list.
    /// </summary>
    private sealed class SyncState
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        private sealed record FileContent(Dictionary<string, List<SyncedItem>>? Lists);

        private readonly string _folder;
        private readonly Dictionary<string, List<SyncedItem>> _lists;
        private bool _changed;

        private SyncState(string folder, Dictionary<string, List<SyncedItem>> lists)
        {
            _folder = folder;
            _lists = lists;
        }

        private string FilePath => Path.Combine(_folder, StateFileName);

        public IEnumerable<string> Names => _lists.Keys;

        public static SyncState Load(string folder, ILogger logger)
        {
            var lists = new Dictionary<string, List<SyncedItem>>(StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(folder, StateFileName);
            try
            {
                if (File.Exists(path))
                {
                    var content = JsonSerializer.Deserialize<FileContent>(File.ReadAllText(path), JsonOptions);
                    foreach (var (name, items) in content?.Lists ?? new Dictionary<string, List<SyncedItem>>())
                    {
                        lists[name] = items;
                    }
                }
            }
            catch (JsonException ex)
            {
                // Without it, the next sync of each list pairs the items up by text again
                logger.LogWarning("Ignoring unreadable {File}: {Error}", path, ex.Message);
            }
            return new SyncState(folder, lists);
        }

        public List<SyncedItem> Get(string name) => _lists.TryGetValue(name, out var items) ? items : new List<SyncedItem>();

        public void Set(string name, List<SyncedItem> items)
        {
            // Keep the name as NodePad has it
            _lists.Remove(name);
            _lists[name] = items;
            _changed = true;
        }

        public void Remove(string name) => _changed |= _lists.Remove(name);

        public void Save()
        {
            if (!_changed) return;
            // Written in place: a temporary file next to it could vanish while a backup zips the folder
            Directory.CreateDirectory(_folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new FileContent(_lists), JsonOptions));
            _changed = false;
        }
    }
}
