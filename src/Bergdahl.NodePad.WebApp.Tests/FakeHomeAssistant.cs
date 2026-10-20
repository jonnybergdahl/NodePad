using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Bergdahl.NodePad.WebApp.Tests
{
    /// <summary>
    /// Just enough of Home Assistant for the ToDo sync, on a local port: the Local To-do config flow,
    /// /api/states, the todo services and todo/item/subscribe on /api/websocket. The methods below
    /// change lists as a user in Home Assistant would, notifying subscribers.
    /// </summary>
    public sealed class FakeHomeAssistant : IAsyncDisposable
    {
        public const string Token = "test-token";

        public sealed record Item(string Summary, bool Done);

        private sealed class FakeItem
        {
            public string Uid = string.Empty;
            public string Summary = string.Empty;
            public bool Done;
        }

        private sealed class FakeList
        {
            public string EntryId = string.Empty;
            public string Title = string.Empty;
            public string EntityId = string.Empty;
            public List<FakeItem> Items = new();
        }

        private sealed record Subscriber(WebSocket Socket, SemaphoreSlim SendLock, int Id, string EntityId);

        private readonly object _lock = new();
        private readonly List<FakeList> _lists = new();
        private readonly List<Subscriber> _subscribers = new();
        private readonly WebApplication _app;
        private int _nextId;

        private FakeHomeAssistant(WebApplication app)
        {
            _app = app;
        }

        public string Url { get; private set; } = string.Empty;

        public static async Task<FakeHomeAssistant> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(FakeHomeAssistant).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var fake = new FakeHomeAssistant(builder.Build());
            fake.MapEndpoints();
            await fake._app.StartAsync();
            fake.Url = fake._app.Urls.First();
            return fake;
        }

        // ---------------------------------------------------------------------
        // Changes made "in Home Assistant"
        // ---------------------------------------------------------------------

        public void AddList(string title, params Item[] items)
        {
            lock (_lock)
            {
                var list = NewList(title);
                list.Items.AddRange(items.Select(i => new FakeItem { Uid = NewUid(), Summary = i.Summary, Done = i.Done }));
            }
        }

        public Task AddItemAsync(string title, string summary) =>
            ChangeAsync(title, list => list.Items.Add(new FakeItem { Uid = NewUid(), Summary = summary }));

        public Task SetDoneAsync(string title, string summary, bool done) =>
            ChangeAsync(title, list => list.Items.First(i => i.Summary == summary).Done = done);

        public Task RenameItemAsync(string title, string summary, string newSummary) =>
            ChangeAsync(title, list => list.Items.First(i => i.Summary == summary).Summary = newSummary);

        public Task RemoveItemAsync(string title, string summary) =>
            ChangeAsync(title, list => list.Items.Remove(list.Items.First(i => i.Summary == summary)));

        /// <summary>
        /// The list's items in order, or null if there is no such list.
        /// </summary>
        public List<Item>? Items(string title)
        {
            lock (_lock)
            {
                return Find(title)?.Items.Select(i => new Item(i.Summary, i.Done)).ToList();
            }
        }

        public List<string> Titles()
        {
            lock (_lock) return _lists.Select(l => l.Title).ToList();
        }

        public int SubscriberCount
        {
            get { lock (_lock) return _subscribers.Count; }
        }

        private async Task ChangeAsync(string title, Action<FakeList> change)
        {
            string entityId;
            lock (_lock)
            {
                var list = Find(title) ?? throw new InvalidOperationException($"No list {title}");
                change(list);
                entityId = list.EntityId;
            }
            await NotifyAsync(entityId);
        }

        // ---------------------------------------------------------------------
        // Endpoints
        // ---------------------------------------------------------------------

        private void MapEndpoints()
        {
            _app.UseWebSockets();

            _app.MapGet("/api/", (HttpContext ctx) => Authorized(ctx) ? Results.Json(new { message = "API running." }) : Results.Unauthorized());

            _app.MapGet("/api/config/config_entries/entry", (HttpContext ctx) =>
            {
                if (!Authorized(ctx)) return Results.Unauthorized();
                lock (_lock) return Results.Json(_lists.Select(l => new { entry_id = l.EntryId, domain = "local_todo", title = l.Title }).ToList());
            });

            _app.MapPost("/api/config/config_entries/flow", (HttpContext ctx) =>
                Authorized(ctx) ? Results.Json(new { flow_id = "flow-" + Interlocked.Increment(ref _nextId), type = "form", step_id = "user" }) : Results.Unauthorized());

            _app.MapPost("/api/config/config_entries/flow/{flowId}", async (HttpContext ctx) =>
            {
                if (!Authorized(ctx)) return Results.Unauthorized();
                var body = await Body(ctx);
                lock (_lock) NewList(body.GetProperty("todo_list_name").GetString()!);
                return Results.Json(new { type = "create_entry" });
            });

            _app.MapDelete("/api/config/config_entries/entry/{entryId}", (HttpContext ctx, string entryId) =>
            {
                if (!Authorized(ctx)) return Results.Unauthorized();
                lock (_lock) _lists.RemoveAll(l => l.EntryId == entryId);
                return Results.Json(new { require_restart = false });
            });

            _app.MapGet("/api/states", (HttpContext ctx) =>
            {
                if (!Authorized(ctx)) return Results.Unauthorized();
                lock (_lock)
                {
                    return Results.Json(_lists.Select(l => new
                    {
                        entity_id = l.EntityId,
                        state = l.Items.Count(i => !i.Done).ToString(),
                        attributes = new { friendly_name = l.Title },
                    }).ToList());
                }
            });

            _app.MapPost("/api/services/todo/{service}", async (HttpContext ctx, string service) =>
            {
                if (!Authorized(ctx)) return Results.Unauthorized();
                var body = await Body(ctx);
                var entityId = body.GetProperty("entity_id").GetString()!;
                object? response = null;
                lock (_lock)
                {
                    var list = _lists.FirstOrDefault(l => l.EntityId == entityId);
                    if (list == null) return Results.BadRequest();
                    switch (service)
                    {
                        case "get_items":
                            response = new Dictionary<string, object> { [entityId] = new { items = ItemsJson(list) } };
                            break;
                        case "add_item":
                            list.Items.Add(new FakeItem { Uid = NewUid(), Summary = body.GetProperty("item").GetString()! });
                            break;
                        case "update_item":
                            var key = body.GetProperty("item").GetString();
                            var item = list.Items.FirstOrDefault(i => i.Uid == key) ?? list.Items.First(i => i.Summary == key);
                            if (body.TryGetProperty("status", out var status)) item.Done = status.GetString() == "completed";
                            if (body.TryGetProperty("rename", out var rename)) item.Summary = rename.GetString()!;
                            break;
                        case "remove_item":
                            var keys = body.GetProperty("item").EnumerateArray().Select(k => k.GetString()).ToHashSet();
                            list.Items.RemoveAll(i => keys.Contains(i.Uid) || keys.Contains(i.Summary));
                            break;
                        default:
                            return Results.BadRequest();
                    }
                }
                if (service != "get_items") await NotifyAsync(entityId);
                return Results.Json(new { changed_states = Array.Empty<object>(), service_response = response });
            });

            _app.Map("/api/websocket", HandleSocketAsync);
        }

        private async Task HandleSocketAsync(HttpContext ctx)
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var sendLock = new SemaphoreSlim(1, 1);
            try
            {
                await SendAsync(socket, sendLock, new { type = "auth_required" });
                var auth = await ReceiveAsync(socket);
                if (auth == null || auth.Value.GetProperty("access_token").GetString() != Token)
                {
                    await SendAsync(socket, sendLock, new { type = "auth_invalid", message = "Invalid access token" });
                    return;
                }
                await SendAsync(socket, sendLock, new { type = "auth_ok" });

                while (await ReceiveAsync(socket) is { } message)
                {
                    var id = message.GetProperty("id").GetInt32();
                    if (message.GetProperty("type").GetString() != "todo/item/subscribe")
                    {
                        await SendAsync(socket, sendLock, new { id, type = "result", success = false });
                        continue;
                    }
                    var entityId = message.GetProperty("entity_id").GetString()!;
                    object[]? items;
                    lock (_lock)
                    {
                        var list = _lists.FirstOrDefault(l => l.EntityId == entityId);
                        items = list == null ? null : ItemsJson(list);
                        if (list != null) _subscribers.Add(new Subscriber(socket, sendLock, id, entityId));
                    }
                    if (items == null)
                    {
                        await SendAsync(socket, sendLock, new { id, type = "result", success = false, error = new { code = "not_found" } });
                        continue;
                    }
                    await SendAsync(socket, sendLock, new { id, type = "result", success = true, result = (object?)null });
                    await SendAsync(socket, sendLock, new { id, type = "event", @event = new { items } });
                }
            }
            catch (WebSocketException)
            {
                // The client went away
            }
            finally
            {
                lock (_lock) _subscribers.RemoveAll(s => s.Socket == socket);
            }
        }

        private async Task NotifyAsync(string entityId)
        {
            List<Subscriber> subscribers;
            object[] items;
            lock (_lock)
            {
                var list = _lists.FirstOrDefault(l => l.EntityId == entityId);
                if (list == null) return;
                items = ItemsJson(list);
                subscribers = _subscribers.Where(s => s.EntityId == entityId).ToList();
            }
            foreach (var subscriber in subscribers)
            {
                try
                {
                    await SendAsync(subscriber.Socket, subscriber.SendLock, new { id = subscriber.Id, type = "event", @event = new { items } });
                }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
                {
                    // Gone
                }
            }
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private FakeList NewList(string title)
        {
            var slug = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
            var list = new FakeList { EntryId = "entry-" + Interlocked.Increment(ref _nextId), Title = title, EntityId = "todo." + slug };
            _lists.Add(list);
            return list;
        }

        private FakeList? Find(string title) => _lists.FirstOrDefault(l => string.Equals(l.Title, title, StringComparison.OrdinalIgnoreCase));

        private string NewUid() => "uid-" + Interlocked.Increment(ref _nextId);

        private static object[] ItemsJson(FakeList list) =>
            list.Items.Select(i => (object)new { uid = i.Uid, summary = i.Summary, status = i.Done ? "completed" : "needs_action" }).ToArray();

        private static bool Authorized(HttpContext ctx) => ctx.Request.Headers.Authorization == "Bearer " + Token;

        private static async Task<JsonElement> Body(HttpContext ctx)
        {
            using var document = await JsonDocument.ParseAsync(ctx.Request.Body);
            return document.RootElement.Clone();
        }

        private static async Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, object message)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await sendLock.WaitAsync();
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                sendLock.Release();
            }
        }

        private static async Task<JsonElement?> ReceiveAsync(WebSocket socket)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }
            using var document = JsonDocument.Parse(message.ToArray());
            return document.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
