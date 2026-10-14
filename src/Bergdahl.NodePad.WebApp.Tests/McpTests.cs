using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Bergdahl.NodePad.WebApp.Tests
{
    public class McpTests : IDisposable
    {
        private readonly TestApp _app = new();

        public void Dispose() => _app.Dispose();

        private async Task<McpClient> ConnectAsync(string apiKey)
        {
            var http = _app.CreateClient();
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
            }, http, null, true);
            return await McpClient.CreateAsync(transport);
        }

        private static async Task<CallToolResult> Call(McpClient client, string tool, Dictionary<string, object?> args)
        {
            return await client.CallToolAsync(tool, args);
        }

        private static string Text(CallToolResult result) =>
            string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

        private static JsonElement Json(CallToolResult result)
        {
            Assert.True(result.IsError != true, Text(result));
            return JsonDocument.Parse(Text(result)).RootElement;
        }

        private static Task<HttpResponseMessage> PostInitialize(HttpClient http, string? apiKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
                    Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (apiKey != null) request.Headers.Add("X-Api-Key", apiKey);
            return http.SendAsync(request);
        }

        [Fact]
        public async Task Mcp_Requires_Api_Key()
        {
            var http = _app.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostInitialize(http, null)).StatusCode);

            // Any key is rejected while none has been generated
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostInitialize(http, "np_guess")).StatusCode);

            var key = _app.Credentials.GenerateApiKey();
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostInitialize(http, key + "x")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PostInitialize(http, key)).StatusCode);
        }

        [Fact]
        public async Task Revoked_Key_Is_Rejected()
        {
            var http = _app.CreateClient();
            var key = _app.Credentials.GenerateApiKey();
            _app.Credentials.RevokeApiKey();
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostInitialize(http, key)).StatusCode);
        }

        [Fact]
        public async Task Api_Key_Does_Not_Grant_Rest_Access()
        {
            var key = _app.Credentials.GenerateApiKey();
            var http = _app.CreateClient();
            http.DefaultRequestHeaders.Add("X-Api-Key", key);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/pages/structure")).StatusCode);
        }

        [Fact]
        public async Task Api_Key_Endpoints_Require_Sign_In_And_Return_Key_Once()
        {
            var anonymous = _app.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/auth/api-key", null)).StatusCode);

            var http = _app.CreateClient();
            await http.PostAsJsonAsync("/api/auth/setup", new { username = "jonny", password = "s3cret-password" });

            var status = await http.GetFromJsonAsync<JsonElement>("/api/auth/api-key");
            Assert.False(status.GetProperty("exists").GetBoolean());

            var generated = await (await http.PostAsync("/api/auth/api-key", null)).Content.ReadFromJsonAsync<JsonElement>();
            var key = generated.GetProperty("apiKey").GetString()!;
            Assert.StartsWith("np_", key);
            Assert.DoesNotContain(key, File.ReadAllText(_app.AuthFilePath));

            Assert.Equal(HttpStatusCode.OK, (await PostInitialize(_app.CreateClient(), key)).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await http.DeleteAsync("/api/auth/api-key")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostInitialize(_app.CreateClient(), key)).StatusCode);
        }

        [Fact]
        public async Task Lists_Tools_Without_Delete()
        {
            await using var client = await ConnectAsync(_app.Credentials.GenerateApiKey());
            var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet();

            foreach (var expected in new[] { "search_notes", "list_notes", "read_note", "recent_notes", "list_tags",
                         "create_note", "update_note", "set_note_tags", "create_folder", "move", "rename" })
            {
                Assert.Contains(expected, tools);
            }
            Assert.DoesNotContain(tools, t => t.Contains("delete"));
        }

        [Fact]
        public async Task Create_Read_Update_Note()
        {
            await using var client = await ConnectAsync(_app.Credentials.GenerateApiKey());

            var created = Json(await Call(client, "create_note", new()
            {
                ["path"] = "Projects/idea.md",
                ["content"] = "# Big idea\n\nFirst draft",
                ["tags"] = new[] { "Work", " ideas " },
            }));
            Assert.Equal("Projects/idea.md", created.GetProperty("path").GetString());
            Assert.Equal("Big idea", created.GetProperty("title").GetString());
            Assert.True(File.Exists(Path.Combine(_app.PagesPath, "Projects", "idea.md")));

            // Creating again must not overwrite
            var again = await Call(client, "create_note", new() { ["path"] = "Projects/idea.md", ["content"] = "oops" });
            Assert.True(again.IsError);
            Assert.Contains("update_note", Text(again));

            var read = Json(await Call(client, "read_note", new() { ["path"] = "Projects/idea.md" }));
            Assert.Equal("# Big idea\n\nFirst draft", read.GetProperty("content").GetString());
            Assert.Equal(new[] { "work", "ideas" }, read.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

            // Update without tags keeps the tags
            Json(await Call(client, "update_note", new() { ["path"] = "Projects/idea.md", ["content"] = "# Big idea\n\nSecond draft" }));
            read = Json(await Call(client, "read_note", new() { ["path"] = "Projects/idea.md" }));
            Assert.Equal("# Big idea\n\nSecond draft", read.GetProperty("content").GetString());
            Assert.Equal(2, read.GetProperty("tags").GetArrayLength());

            var setTags = Json(await Call(client, "set_note_tags", new() { ["path"] = "Projects/idea.md", ["tags"] = new[] { "done" } }));
            Assert.Equal(new[] { "done" }, setTags.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

            var missing = await Call(client, "update_note", new() { ["path"] = "Projects/missing.md", ["content"] = "x" });
            Assert.True(missing.IsError);
            Assert.Contains("create_note", Text(missing));
        }

        [Fact]
        public async Task Search_List_And_Recent()
        {
            await using var client = await ConnectAsync(_app.Credentials.GenerateApiKey());
            Json(await Call(client, "create_note", new() { ["path"] = "a.md", ["content"] = "# Alpha\n\nabout pelicans", ["tags"] = new[] { "birds" } }));
            Json(await Call(client, "create_note", new() { ["path"] = "Sub/b.md", ["content"] = "# Beta\n\nnothing here" }));

            var hits = Json(await Call(client, "search_notes", new() { ["query"] = "pelican" }));
            Assert.Equal("a.md", Assert.Single(hits.EnumerateArray()).GetProperty("path").GetString());

            var tree = Json(await Call(client, "list_notes", new()));
            Assert.Equal("a.md", tree[0].GetProperty("name").GetString());
            Assert.Equal("Alpha", tree[0].GetProperty("title").GetString());
            Assert.Equal("Sub", tree[1].GetProperty("name").GetString());

            var recent = Json(await Call(client, "recent_notes", new() { ["tags"] = "birds" }));
            Assert.Equal("a.md", Assert.Single(recent.EnumerateArray()).GetProperty("path").GetString());

            var tags = Json(await Call(client, "list_tags", new()));
            Assert.Equal("birds", Assert.Single(tags.EnumerateArray()).GetProperty("tag").GetString());
        }

        [Fact]
        public async Task Folders_Move_And_Rename()
        {
            await using var client = await ConnectAsync(_app.Credentials.GenerateApiKey());
            Json(await Call(client, "create_note", new() { ["path"] = "note.md", ["content"] = "# Note", ["tags"] = new[] { "x" } }));
            var folder = await Call(client, "create_folder", new() { ["path"] = "Archive" });
            Assert.True(folder.IsError != true, Text(folder));
            Assert.True(Directory.Exists(Path.Combine(_app.PagesPath, "Archive")));

            var moved = Json(await Call(client, "move", new() { ["source"] = "note.md", ["destination"] = "Archive" }));
            Assert.Equal("Archive/note.md", moved.GetProperty("path").GetString());
            Assert.True(File.Exists(Path.Combine(_app.PagesPath, "Archive", "note.meta")));

            var renamed = Json(await Call(client, "rename", new() { ["path"] = "Archive/note.md", ["newName"] = "old-note" }));
            Assert.Equal("Archive/old-note.md", renamed.GetProperty("path").GetString());
            Assert.True(File.Exists(Path.Combine(_app.PagesPath, "Archive", "old-note.md")));
        }

        [Theory]
        [InlineData("../outside.md")]
        [InlineData("/etc/outside.md")]
        [InlineData("notes.txt")]
        public async Task Paths_Outside_Notes_Are_Rejected(string path)
        {
            await using var client = await ConnectAsync(_app.Credentials.GenerateApiKey());
            var result = await Call(client, "create_note", new() { ["path"] = path, ["content"] = "x" });
            Assert.True(result.IsError);
            Assert.False(File.Exists(Path.Combine(_app.Root, "outside.md")));
        }
    }
}
