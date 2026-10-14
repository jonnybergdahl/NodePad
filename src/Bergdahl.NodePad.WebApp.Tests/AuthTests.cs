using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Bergdahl.NodePad.WebApp.Auth;
using Xunit;

namespace Bergdahl.NodePad.WebApp.Tests
{
    public class PasswordHasherTests
    {
        [Fact]
        public void Hash_Verifies_Correct_Password_Only()
        {
            var hash = PasswordHasher.Hash("correct horse");
            Assert.StartsWith("PBKDF2$SHA256$", hash);
            Assert.True(PasswordHasher.Verify("correct horse", hash));
            Assert.False(PasswordHasher.Verify("wrong horse", hash));
        }

        [Fact]
        public void Hash_Uses_Random_Salt()
        {
            Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("plaintext")]
        [InlineData("PBKDF2$SHA256$abc$def$ghi")]
        [InlineData("PBKDF2$SHA256$1000$not-base64$xx")]
        public void Verify_Rejects_Malformed_Hashes(string? stored)
        {
            Assert.False(PasswordHasher.Verify("anything", stored));
        }
    }

    public class AuthTests : IDisposable
    {
        private const string Username = "jonny";
        private const string Password = "s3cret-password";

        private readonly TestApp _app = new();

        public void Dispose() => _app.Dispose();

        private HttpClient CreateClient() => _app.CreateClient();

        private static Task<HttpResponseMessage> Setup(HttpClient client, string password = Password) =>
            client.PostAsJsonAsync("/api/auth/setup", new { username = Username, password });

        private static Task<HttpResponseMessage> Login(HttpClient client, string password = Password) =>
            client.PostAsJsonAsync("/api/auth/login", new { username = Username, password });

        [Fact]
        public async Task Api_Requires_Authentication()
        {
            var client = CreateClient();
            var response = await client.GetAsync("/api/pages/structure");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Ui_Redirects_To_Login_Page()
        {
            var client = CreateClient();
            var response = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/login.html", response.Headers.Location!.PathAndQuery);
        }

        [Theory]
        [InlineData("/login.html")]
        [InlineData("/assets/nodepad_logo.png")]
        [InlineData("/assets/nodepad_icon.png")]
        [InlineData("/favicon.ico")]
        public async Task Login_Page_Assets_Are_Public(string path)
        {
            var client = CreateClient();
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task First_Run_Setup_Creates_Account_And_Signs_In()
        {
            var client = CreateClient();
            var status = await client.GetFromJsonAsync<StatusDto>("/api/auth/status");
            Assert.False(status!.Configured);

            Assert.Equal(HttpStatusCode.OK, (await Setup(client)).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/pages/structure")).StatusCode);
            status = await client.GetFromJsonAsync<StatusDto>("/api/auth/status");
            Assert.True(status!.Configured);
            Assert.True(status.Authenticated);
            Assert.Equal(Username, status.Username);

            var saved = File.ReadAllText(_app.AuthFilePath);
            Assert.Contains("PBKDF2$SHA256$", saved);
            Assert.DoesNotContain(Password, saved);
        }

        [Fact]
        public async Task Setup_Is_Rejected_Once_Account_Exists()
        {
            var client = CreateClient();
            await Setup(client);
            var response = await Setup(CreateClient(), "another-password");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        [Fact]
        public async Task Setup_Rejects_Short_Password()
        {
            var response = await Setup(CreateClient(), "short");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Login_Checks_Password()
        {
            await Setup(CreateClient());

            var client = CreateClient();
            Assert.Equal(HttpStatusCode.BadRequest, (await Login(client, "wrong-password")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/pages/structure")).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await Login(client)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/pages/structure")).StatusCode);
        }

        [Fact]
        public async Task Logout_Ends_Session()
        {
            var client = CreateClient();
            await Setup(client);
            await client.PostAsync("/api/auth/logout", null);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/pages/structure")).StatusCode);
        }

        [Fact]
        public async Task Changing_Password_Signs_Out_Other_Sessions()
        {
            var first = CreateClient();
            await Setup(first);
            var second = CreateClient();
            await Login(second);

            var change = await first.PostAsJsonAsync("/api/auth/change-password",
                new { currentPassword = Password, newPassword = "new-password-123" });
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/pages/structure")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/pages/structure")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Login(CreateClient(), "new-password-123")).StatusCode);
        }

        [Fact]
        public async Task Login_Is_Rate_Limited()
        {
            await Setup(CreateClient());
            var client = CreateClient();
            var responses = new HttpResponseMessage[6];
            for (var i = 0; i < responses.Length; i++)
            {
                responses[i] = await Login(client, "wrong-password");
            }
            Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        }

        private class StatusDto
        {
            public bool Configured { get; set; }
            public bool Authenticated { get; set; }
            public string? Username { get; set; }
        }
    }
}
