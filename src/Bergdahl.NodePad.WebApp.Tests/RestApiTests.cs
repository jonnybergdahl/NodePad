using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Bergdahl.NodePad.WebApp.Tests
{
    /// <summary>
    /// Checks the JSON returned over HTTP, which the frontend relies on.
    /// </summary>
    public class RestApiTests : IDisposable
    {
        private readonly TestApp _app = new();

        public void Dispose() => _app.Dispose();

        private async Task<HttpClient> SignedInClient()
        {
            var client = _app.CreateClient();
            var setup = await client.PostAsJsonAsync("/api/auth/setup", new { username = "jonny", password = "s3cret-password" });
            setup.EnsureSuccessStatusCode();
            return client;
        }

        private static string[] Keys(JsonElement element) =>
            element.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();

        [Fact]
        public async Task Json_Shapes_Are_Unchanged()
        {
            var client = await SignedInClient();

            var save = await client.PostAsync("/api/pages/save?path=Folder/note.md&tags=Work,Ideas",
                new StringContent("# Title\n\nsome searchable text", Encoding.UTF8, "text/plain"));
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);

            var info = await client.GetFromJsonAsync<JsonElement>("/api/pages/info?path=Folder/note.md");
            Assert.Equal(new[] { "lastModifiedUtc", "sizeBytes", "tags", "title" }, Keys(info));

            var hits = await client.GetFromJsonAsync<JsonElement>("/api/pages/search?query=searchable");
            var hit = Assert.Single(hits.EnumerateArray());
            Assert.Equal(new[] { "highlights", "path", "score", "snippet", "title" }, Keys(hit));
            Assert.Equal(new[] { "length", "start" }, Keys(hit.GetProperty("highlights")[0]));

            var content = await client.GetFromJsonAsync<JsonElement>("/api/pages/content?path=Folder/note.md&includeMeta=true");
            Assert.Equal(new[] { "name", "path" }, Keys(content.GetProperty("breadcrumbs")[0]));

            var valid = await client.GetFromJsonAsync<JsonElement>("/api/pages/validate-name?path=&type=file&name=fresh.md");
            Assert.Equal(new[] { "valid" }, Keys(valid));

            var tags = await client.GetFromJsonAsync<JsonElement>("/api/pages/tags");
            Assert.Equal(new[] { "count", "tag" }, Keys(tags[0]));

            var recent = await client.GetFromJsonAsync<JsonElement>("/api/pages/recent");
            Assert.Equal(new[] { "lastModifiedUtc", "path", "title" }, Keys(recent[0]));

            var moved = await (await client.PostAsync("/api/pages/move?source=Folder/note.md&destination=", null))
                .Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(new[] { "oldPath", "path", "type" }, Keys(moved));
        }

        [Fact]
        public async Task Errors_Keep_Their_Status_Codes()
        {
            var client = await SignedInClient();

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/pages/content?path=missing.md")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/pages/content?path=../x.md")).StatusCode);

            await client.PostAsync("/api/pages/create?path=Full&type=directory", null);
            await client.PostAsync("/api/pages/create?path=Full/a.md&type=file", null);
            Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync("/api/pages/delete?path=Full")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/pages/create?path=Full/a.md&type=file", null)).StatusCode);
        }
    }
}
