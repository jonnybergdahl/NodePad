using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Bergdahl.NodePad.WebApp.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Item = Bergdahl.NodePad.WebApp.Tests.FakeHomeAssistant.Item;

namespace Bergdahl.NodePad.WebApp.Tests
{
    /// <summary>
    /// The ToDo sync against <see cref="FakeHomeAssistant"/>. "Today" is 2026-10-18.
    /// </summary>
    public class HomeAssistantSyncTests : IAsyncLifetime
    {
        private readonly string _root = TestApp.NewRoot();
        private FakeHomeAssistant _ha = null!;

        public async Task InitializeAsync() => _ha = await FakeHomeAssistant.StartAsync();

        public async Task DisposeAsync()
        {
            await _ha.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private string TodoDir => Path.Combine(_root, "Pages", "_ToDo");

        private void WriteList(string name, string content)
        {
            Directory.CreateDirectory(TodoDir);
            File.WriteAllText(Path.Combine(TodoDir, name + ".md"), content);
        }

        private string? ReadList(string name)
        {
            var path = Path.Combine(TodoDir, name + ".md");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private Dictionary<string, string?> SyncOn() => new()
        {
            ["HomeAssistant:Url"] = _ha.Url,
            ["HomeAssistant:Token"] = FakeHomeAssistant.Token,
            ["HomeAssistant:Active"] = "true",
        };

        /// <summary>
        /// Starts NodePad on the test's files and waits for the startup sync.
        /// </summary>
        private async Task<TestApp> StartAsync(Dictionary<string, string?>? settings = null)
        {
            var app = new TestApp(settings ?? SyncOn(), _root);
            var sync = app.Factory.Services.GetRequiredService<HomeAssistantSync>();
            if (settings == null) await Eventually(() => sync.Status.Connected && sync.Status.LastSync != null, () => "connected and synced");
            await sync.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            return app;
        }

        private static async Task<HttpClient> SignedInClient(TestApp app)
        {
            var client = app.CreateClient();
            var setup = await client.PostAsJsonAsync("/api/auth/setup", new { username = "jonny", password = "s3cret-password" });
            setup.EnsureSuccessStatusCode();
            return client;
        }

        private static async Task Eventually(Func<bool> condition, Func<string> describe)
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!condition())
            {
                if (DateTime.UtcNow > until) Assert.Fail("Timed out waiting for " + describe());
                await Task.Delay(25);
            }
        }

        private Task HaHas(string title, params Item[] items) =>
            Eventually(() => _ha.Items(title)?.SequenceEqual(items) == true,
                () => $"{title} in Home Assistant to be [{string.Join(", ", items.AsEnumerable())}], is [{string.Join(", ", _ha.Items(title) ?? new List<Item>())}]");

        private Task FileIs(string name, string content) =>
            Eventually(() => ReadList(name) == content, () => $"{name}.md to be\n{content}\nis\n{ReadList(name)}");

        [Fact]
        public async Task Startup_Syncs_Lists_Both_Ways()
        {
            WriteList("Groceries", "# Groceries\n\n- [ ] Milk\n- [x] Eggs ✅ 2026-10-18\n");
            WriteList("Work", "# Work\n\n- [ ] Call Bob\n");
            _ha.AddList("Groceries", new Item("Milk", false), new Item("Bread", false));

            using var app = await StartAsync();

            await HaHas("Groceries", new Item("Milk", false), new Item("Bread", false), new Item("Eggs", true));
            await HaHas("Work", new Item("Call Bob", false));
            await FileIs("Groceries", "# Groceries\n\n- [ ] Milk\n- [x] Eggs ✅ 2026-10-18\n- [ ] Bread\n");
            Assert.True(File.Exists(Path.Combine(TodoDir, HomeAssistantSync.StateFileName)));
        }

        [Fact]
        public async Task Changes_In_Home_Assistant_Reach_NodePad()
        {
            WriteList("Groceries", "# Groceries\n\n- [ ] Milk\n- [ ] Eggs\n- [ ] Bread\n");
            using var app = await StartAsync();
            await Eventually(() => _ha.SubscriberCount == 1, () => "the subscription");
            var changes = app.Factory.Services.GetRequiredService<TodoChangeNotifier>();
            var version = changes.Version;

            await _ha.SetDoneAsync("Groceries", "Milk", true);
            await FileIs("Groceries", "# Groceries\n\n- [x] Milk ✅ 2026-10-18\n- [ ] Eggs\n- [ ] Bread\n");
            // Open pages are told to reload
            await Eventually(() => changes.Version > version, () => "the change to be reported to open pages");

            await _ha.RenameItemAsync("Groceries", "Bread", "Rye bread");
            await _ha.RemoveItemAsync("Groceries", "Eggs");
            await _ha.AddItemAsync("Groceries", "Jam");
            await FileIs("Groceries", "# Groceries\n\n- [x] Milk ✅ 2026-10-18\n- [ ] Rye bread\n- [ ] Jam\n");

            await _ha.SetDoneAsync("Groceries", "Milk", false);
            await FileIs("Groceries", "# Groceries\n\n- [ ] Milk\n- [ ] Rye bread\n- [ ] Jam\n");
            // Nothing was sent back to Home Assistant
            await HaHas("Groceries", new Item("Milk", false), new Item("Rye bread", false), new Item("Jam", false));
        }

        [Fact]
        public async Task Changes_In_NodePad_Reach_Home_Assistant()
        {
            using var app = await StartAsync();
            var http = await SignedInClient(app);

            (await http.PostAsJsonAsync("/api/todos/lists", new { name = "Hardware" })).EnsureSuccessStatusCode();
            await HaHas("Hardware");

            (await http.PostAsJsonAsync("/api/todos/items?list=Hardware", new { text = "Nails" })).EnsureSuccessStatusCode();
            (await http.PostAsJsonAsync("/api/todos/items?list=Hardware", new { text = "Screws" })).EnsureSuccessStatusCode();
            await HaHas("Hardware", new Item("Nails", false), new Item("Screws", false));

            (await http.PutAsJsonAsync("/api/todos/items?list=Hardware&index=0", new { done = true })).EnsureSuccessStatusCode();
            await HaHas("Hardware", new Item("Nails", true), new Item("Screws", false));

            (await http.PutAsJsonAsync("/api/todos/items?list=Hardware&index=1", new { text = "Wood screws" })).EnsureSuccessStatusCode();
            await HaHas("Hardware", new Item("Nails", true), new Item("Wood screws", false));

            (await http.DeleteAsync("/api/todos/items?list=Hardware&index=0")).EnsureSuccessStatusCode();
            await HaHas("Hardware", new Item("Wood screws", false));

            (await http.PostAsync("/api/todos/lists/rename?name=Hardware&newName=Tools", null)).EnsureSuccessStatusCode();
            await HaHas("Tools", new Item("Wood screws", false));
            await Eventually(() => _ha.Items("Hardware") == null, () => "Hardware to be deleted");

            (await http.DeleteAsync("/api/todos/lists?name=Tools")).EnsureSuccessStatusCode();
            await Eventually(() => _ha.Titles().Count == 0, () => $"no lists, has {string.Join(", ", _ha.Titles())}");
        }

        [Fact]
        public async Task Changes_Made_While_Not_Running_Are_Merged_At_Startup()
        {
            WriteList("Groceries", "# Groceries\n\n- [ ] Milk\n- [ ] Eggs\n- [ ] Bread\n");
            WriteList("Work", "# Work\n\n- [ ] Call Bob\n");
            using (await StartAsync())
            {
                await HaHas("Groceries", new Item("Milk", false), new Item("Eggs", false), new Item("Bread", false));
                await HaHas("Work", new Item("Call Bob", false));
            }

            // While NodePad is stopped
            await _ha.SetDoneAsync("Groceries", "Milk", true);
            await _ha.RemoveItemAsync("Groceries", "Bread");
            WriteList("Groceries", "# Groceries\n\n- [ ] Milk\n- [ ] Bread\n- [ ] Jam\n");
            File.Delete(Path.Combine(TodoDir, "Work.md"));

            using var app = await StartAsync();

            await FileIs("Groceries", "# Groceries\n\n- [x] Milk ✅ 2026-10-18\n- [ ] Jam\n");
            await HaHas("Groceries", new Item("Milk", true), new Item("Jam", false));
            await Eventually(() => _ha.Items("Work") == null, () => "Work to be deleted");
        }

        [Fact]
        public async Task A_List_Deleted_In_Home_Assistant_Is_Recreated()
        {
            WriteList("Groceries", "# Groceries\n\n- [ ] Milk\n");
            using (await StartAsync())
            {
                await HaHas("Groceries", new Item("Milk", false));
            }

            // Deleted in Home Assistant while NodePad is stopped
            var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", FakeHomeAssistant.Token);
            var entries = JsonDocument.Parse(await http.GetStringAsync(_ha.Url + "/api/config/config_entries/entry")).RootElement;
            (await http.DeleteAsync(_ha.Url + "/api/config/config_entries/entry/" + entries[0].GetProperty("entry_id").GetString())).EnsureSuccessStatusCode();

            using var app = await StartAsync();

            await HaHas("Groceries", new Item("Milk", false));
            Assert.Equal("# Groceries\n\n- [ ] Milk\n", ReadList("Groceries"));
        }

        [Fact]
        public async Task Turning_On_Syncing_In_The_Settings_Connects_And_Syncs()
        {
            WriteList("Groceries", "# Groceries\n\n- [ ] Milk\n");
            using var app = await StartAsync(new Dictionary<string, string?>());
            var http = await SignedInClient(app);

            var saved = await http.PostAsJsonAsync("/api/settings/home-assistant", new { url = _ha.Url + "/", token = FakeHomeAssistant.Token, active = true });
            var body = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement;
            Assert.True(saved.IsSuccessStatusCode, body.GetRawText());
            Assert.Equal(_ha.Url, body.GetProperty("url").GetString());
            Assert.True(body.GetProperty("hasToken").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("connectionError").ValueKind);
            Assert.False(body.TryGetProperty("token", out _));

            await HaHas("Groceries", new Item("Milk", false));
            var status = JsonDocument.Parse(await http.GetStringAsync("/api/settings/home-assistant")).RootElement;
            Assert.True(status.GetProperty("connected").GetBoolean());

            // A wrong token is reported, and the sync disconnects
            saved = await http.PostAsJsonAsync("/api/settings/home-assistant", new { url = _ha.Url, token = "wrong", active = true });
            body = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("Home Assistant rejected the access token", body.GetProperty("connectionError").GetString());
            var sync = app.Factory.Services.GetRequiredService<HomeAssistantSync>();
            await Eventually(() => !sync.Status.Connected, () => "disconnected");
        }
    }
}
