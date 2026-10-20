using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Bergdahl.NodePad.WebApp.Tests
{
    /// <summary>
    /// ToDo lists over REST and MCP. "Today" is fixed at 2026-10-18 by <see cref="TestApp.Time"/>.
    /// </summary>
    public class TodoTests : IDisposable
    {
        private readonly TestApp _app = new();

        public void Dispose() => _app.Dispose();

        private string TodoDir => Path.Combine(_app.PagesPath, "_ToDo");

        private async Task<HttpClient> SignedInClient()
        {
            var client = _app.CreateClient();
            var setup = await client.PostAsJsonAsync("/api/auth/setup", new { username = "jonny", password = "s3cret-password" });
            setup.EnsureSuccessStatusCode();
            return client;
        }

        private void WriteList(string name, string content)
        {
            Directory.CreateDirectory(TodoDir);
            File.WriteAllText(Path.Combine(TodoDir, name + ".md"), content);
        }

        private string ReadList(string name) => File.ReadAllText(Path.Combine(TodoDir, name + ".md"));

        private static async Task<JsonElement> Json(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        private static string[] Texts(JsonElement list) =>
            list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()!).ToArray();

        [Fact]
        public async Task Create_List_Add_And_Check_Items()
        {
            var http = await SignedInClient();

            var created = await Json(await http.PostAsJsonAsync("/api/todos/lists", new { name = "Groceries" }));
            Assert.Equal("Groceries", created.GetProperty("name").GetString());
            Assert.Equal("# Groceries\n\n", ReadList("Groceries"));

            await Json(await http.PostAsJsonAsync("/api/todos/items?list=Groceries", new { text = "Milk" }));
            var list = await Json(await http.PostAsJsonAsync("/api/todos/items?list=Groceries", new { text = "Eggs" }));
            Assert.Equal(new[] { "Milk", "Eggs" }, Texts(list));

            // Checking records today's date
            list = await Json(await http.PutAsJsonAsync("/api/todos/items?list=Groceries&index=1", new { done = true }));
            var eggs = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("text").GetString() == "Eggs");
            Assert.True(eggs.GetProperty("done").GetBoolean());
            Assert.Equal("2026-10-18", eggs.GetProperty("doneDate").GetString());
            Assert.Equal("# Groceries\n\n- [ ] Milk\n- [x] Eggs ✅ 2026-10-18\n", ReadList("Groceries"));

            // Unchecking removes the date
            await Json(await http.PutAsJsonAsync("/api/todos/items?list=Groceries&index=1", new { done = false }));
            Assert.Equal("# Groceries\n\n- [ ] Milk\n- [ ] Eggs\n", ReadList("Groceries"));
        }

        [Fact]
        public async Task Changes_Are_Reported_On_The_Event_Stream()
        {
            var http = await SignedInClient();
            WriteList("Groceries", "# Groceries\n\n- [ ] Milk\n");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await http.GetAsync("/api/todos/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));

            async Task<string> NextData()
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(timeout.Token);
                    Assert.NotNull(line);
                    if (line!.StartsWith("data: ")) return line["data: ".Length..];
                }
            }

            // Wait for the stream to be set up before changing anything
            Assert.StartsWith("retry: ", await reader.ReadLineAsync(timeout.Token));

            await Json(await http.PutAsJsonAsync("/api/todos/items?list=Groceries&index=0", new { done = true }));
            var first = long.Parse(await NextData());

            // Changes in quick succession may become one message; the last one has the latest version
            await Json(await http.PostAsJsonAsync("/api/todos/items?list=Groceries", new { text = "Eggs" }));
            await Json(await http.PostAsJsonAsync("/api/todos/items?list=Groceries", new { text = "Bread" }));
            var latest = long.Parse(await NextData());
            if (latest < first + 2) latest = long.Parse(await NextData());
            Assert.Equal(first + 2, latest);
        }

        [Fact]
        public async Task The_Event_Stream_Requires_Sign_In()
        {
            var response = await _app.CreateClient().GetAsync("/api/todos/events");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Done_Items_Older_Than_Two_Weeks_Are_Hidden()
        {
            WriteList("Work", "# Work\n\n- [x] Old ✅ 2026-10-03\n- [x] Recent ✅ 2026-10-05\n- [ ] Open\n- [x] Undated\n");
            var http = await SignedInClient();

            // 15 days old is hidden, 13 days old is shown; open items first, then done newest first
            var lists = await Json(await http.GetAsync("/api/todos"));
            var work = lists.EnumerateArray().Single();
            Assert.Equal(new[] { "Open", "Recent", "Undated" }, Texts(work));
            Assert.Equal(1, work.GetProperty("hiddenDoneCount").GetInt32());

            var all = (await Json(await http.GetAsync("/api/todos?includeOldDone=true"))).EnumerateArray().Single();
            Assert.Equal(new[] { "Open", "Recent", "Old", "Undated" }, Texts(all));
            Assert.Equal(0, all.GetProperty("hiddenDoneCount").GetInt32());

            // Indexes follow the file, not the display order
            var old = all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("text").GetString() == "Old");
            Assert.Equal(0, old.GetProperty("index").GetInt32());
        }

        [Fact]
        public async Task Changes_Keep_Hand_Written_Lines()
        {
            WriteList("Mixed", "# Mixed\r\n\r\nSome notes about the list.\r\n\r\n* [ ] First\r\n  - [X] Nested ✅ 2026-10-17\r\n\r\nFooter text\r\n");
            var http = await SignedInClient();

            await Json(await http.PutAsJsonAsync("/api/todos/items?list=Mixed&index=0", new { text = "First, edited" }));
            await Json(await http.PostAsJsonAsync("/api/todos/items?list=Mixed", new { text = "Third" }));

            // Line endings, bullets, indentation, other text and the existing date are kept
            Assert.Equal("# Mixed\r\n\r\nSome notes about the list.\r\n\r\n* [ ] First, edited\r\n  - [X] Nested ✅ 2026-10-17\r\n- [ ] Third\r\n\r\nFooter text\r\n",
                ReadList("Mixed"));

            // Checking an already done item keeps its date; deleting removes just that line
            await Json(await http.PutAsJsonAsync("/api/todos/items?list=Mixed&index=1", new { done = true }));
            Assert.Contains("✅ 2026-10-17", ReadList("Mixed"));
            await Json(await http.DeleteAsync("/api/todos/items?list=Mixed&index=0"));
            Assert.DoesNotContain("First", ReadList("Mixed"));
            Assert.Contains("Footer text", ReadList("Mixed"));
        }

        [Fact]
        public async Task Rename_And_Delete_Lists()
        {
            var http = await SignedInClient();
            await Json(await http.PostAsJsonAsync("/api/todos/lists", new { name = "Home" }));
            await Json(await http.PostAsJsonAsync("/api/todos/lists", new { name = "Work" }));

            var renamed = await Json(await http.PostAsync("/api/todos/lists/rename?name=Home&newName=Home%20projects", null));
            Assert.Equal("Home projects", renamed.GetProperty("name").GetString());
            Assert.StartsWith("# Home projects", ReadList("Home projects"));
            Assert.False(File.Exists(Path.Combine(TodoDir, "Home.md")));

            Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync("/api/todos/lists/rename?name=Work&newName=home%20PROJECTS", null)).StatusCode);

            (await http.DeleteAsync("/api/todos/lists?name=Work")).EnsureSuccessStatusCode();
            var names = (await Json(await http.GetAsync("/api/todos"))).EnumerateArray().Select(l => l.GetProperty("name").GetString());
            Assert.Equal(new[] { "Home projects" }, names);
        }

        [Fact]
        public async Task Errors_Have_Status_Codes()
        {
            var http = await SignedInClient();
            await Json(await http.PostAsJsonAsync("/api/todos/lists", new { name = "Groceries" }));

            Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/api/todos/lists", new { name = "Groceries" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/todos/lists", new { name = "  " })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/todos/items?list=Nope", new { text = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.PutAsJsonAsync("/api/todos/items?list=Groceries&index=5", new { done = true })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/todos/items?list=Groceries", new { text = "a\nb" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync("/api/todos/lists?name=Nope")).StatusCode);

            // List names cannot escape the _ToDo folder
            var odd = await Json(await http.PostAsJsonAsync("/api/todos/lists", new { name = "../Escape" }));
            Assert.True(File.Exists(Path.Combine(TodoDir, odd.GetProperty("name").GetString() + ".md")));
            Assert.False(File.Exists(Path.Combine(_app.PagesPath, "Escape.md")));
        }

        [Fact]
        public async Task Todo_Folder_Is_Kept_Out_Of_Notes()
        {
            WriteList("Groceries", "# Groceries\n\n- [ ] Unique-milk-item\n");
            Directory.CreateDirectory(_app.PagesPath);
            File.WriteAllText(Path.Combine(_app.PagesPath, "note.md"), "# Note\n\nUnique-milk-item in a note");
            var http = await SignedInClient();

            var structure = await http.GetStringAsync("/api/pages/structure");
            Assert.DoesNotContain("_ToDo", structure);

            var hits = await Json(await http.GetAsync("/api/pages/search?query=Unique-milk-item"));
            Assert.Equal(new[] { "note.md" }, hits.EnumerateArray().Select(h => h.GetProperty("path").GetString()));

            Assert.DoesNotContain("_ToDo", await http.GetStringAsync("/api/pages/recent"));
            Assert.DoesNotContain("_ToDo", await http.GetStringAsync("/api/pages/untagged"));
            Assert.DoesNotContain("_ToDo", await http.GetStringAsync("/api/pages/tags-index"));

            // Notes cannot be created in, moved into or renamed to the _ToDo folder
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/api/pages/create?path=_ToDo/x.md&type=file", null)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/api/pages/move?source=note.md&destination=_ToDo", null)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.DeleteAsync("/api/pages/delete?path=_ToDo&recursive=true")).StatusCode);
            (await http.PostAsync("/api/pages/create?path=Other&type=directory", null)).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/api/pages/rename?path=Other&newName=_ToDo", null)).StatusCode);
            Assert.True(File.Exists(Path.Combine(TodoDir, "Groceries.md")));
        }

        [Fact]
        public async Task Mcp_Tools_Manage_Todos()
        {
            var key = _app.Credentials.GenerateApiKey();
            var http = _app.CreateClient();
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
            }, http, null, true);
            await using var client = await McpClient.CreateAsync(transport);

            static JsonElement Result(CallToolResult result)
            {
                var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True(result.IsError != true, text);
                return JsonDocument.Parse(text).RootElement;
            }

            Result(await client.CallToolAsync("create_todo_list", new Dictionary<string, object?> { ["name"] = "Errands" }));
            Result(await client.CallToolAsync("add_todo", new Dictionary<string, object?> { ["list"] = "Errands", ["text"] = "Post office" }));
            var list = Result(await client.CallToolAsync("set_todo_done", new Dictionary<string, object?> { ["list"] = "Errands", ["index"] = 0, ["done"] = true }));
            Assert.Equal("2026-10-18", list.GetProperty("items")[0].GetProperty("doneDate").GetString());

            var lists = Result(await client.CallToolAsync("list_todos", new Dictionary<string, object?>()));
            Assert.Equal("Errands", lists[0].GetProperty("name").GetString());

            var error = await client.CallToolAsync("add_todo", new Dictionary<string, object?> { ["list"] = "Nope", ["text"] = "x" });
            Assert.True(error.IsError);

            // edit_todo keeps the done state and date
            list = Result(await client.CallToolAsync("edit_todo", new Dictionary<string, object?> { ["list"] = "Errands", ["index"] = 0, ["text"] = "Post office, parcel" }));
            Assert.Equal("Post office, parcel", list.GetProperty("items")[0].GetProperty("text").GetString());
            Assert.Equal("2026-10-18", list.GetProperty("items")[0].GetProperty("doneDate").GetString());
            Assert.Equal("# Errands\n\n- [x] Post office, parcel ✅ 2026-10-18\n", ReadList("Errands"));

            // remove_todo deletes just that item
            Result(await client.CallToolAsync("add_todo", new Dictionary<string, object?> { ["list"] = "Errands", ["text"] = "Pharmacy" }));
            list = Result(await client.CallToolAsync("remove_todo", new Dictionary<string, object?> { ["list"] = "Errands", ["index"] = 0 }));
            Assert.Equal(new[] { "Pharmacy" }, Texts(list));
            Assert.Equal(0, list.GetProperty("items")[0].GetProperty("index").GetInt32());
            Assert.True((await client.CallToolAsync("remove_todo", new Dictionary<string, object?> { ["list"] = "Errands", ["index"] = 5 })).IsError);
            Assert.True((await client.CallToolAsync("edit_todo", new Dictionary<string, object?> { ["list"] = "Errands", ["index"] = 0, ["text"] = " " })).IsError);

            // Items can be removed, but whole lists (and notes) cannot be deleted over MCP
            var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToArray();
            Assert.Contains("edit_todo", tools);
            Assert.Contains("remove_todo", tools);
            Assert.DoesNotContain(tools, name => name.Contains("delete"));
        }
    }
}
