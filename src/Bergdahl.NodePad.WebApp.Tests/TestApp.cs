using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Bergdahl.NodePad.WebApp.Auth;
using Bergdahl.NodePad.WebApp.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Bergdahl.NodePad.WebApp.Tests
{
    /// <summary>
    /// The full web app running in memory, with pages, backups, keys and the auth file in a temp folder.
    /// </summary>
    public sealed class TestApp : IDisposable
    {
        private readonly bool _ownsRoot;

        public string Root { get; }
        public string PagesPath => Path.Combine(Root, "Pages");
        public string AuthFilePath => Path.Combine(Root, CredentialStore.FileName);
        public WebApplicationFactory<Program> Factory { get; }
        /// <summary>The app's clock; set <see cref="TestTimeProvider.Now"/> to control "today".</summary>
        public TestTimeProvider Time { get; } = new();

        /// <param name="settings">Extra configuration, as if it were in nodepad.json or nodepad.auth.json.</param>
        /// <param name="root">The temp folder to use, for tests that run the app more than once on the same files;
        /// the test removes it.</param>
        public TestApp(IDictionary<string, string?>? settings = null, string? root = null)
        {
            _ownsRoot = root == null;
            Root = root ?? NewRoot();
            Directory.CreateDirectory(Root);

            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                // Read before the app is built, so it has to be a host setting
                builder.UseSetting("DataProtectionKeysDirectory", Path.Combine(Root, "Keys"));
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    // Never read the account from a real nodepad.auth.json in the project folder
                    var authSource = config.Sources.OfType<JsonConfigurationSource>()
                        .FirstOrDefault(s => s.Path == CredentialStore.FileName);
                    if (authSource != null) config.Sources.Remove(authSource);

                    // Added after nodepad.json, so these win over the project's own settings
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["PagesDirectory"] = PagesPath,
                        ["BackupDirectory"] = Path.Combine(Root, "Backups"),
                    });
                    if (settings != null) config.AddInMemoryCollection(settings);
                });
                builder.ConfigureTestServices(services =>
                {
                    // Never touch a real nodepad.auth.json
                    services.AddSingleton(sp => new CredentialStore(sp.GetRequiredService<IConfiguration>(), AuthFilePath));
                    services.AddSingleton(sp => new HomeAssistantSettingsStore(sp.GetRequiredService<IConfiguration>(), AuthFilePath));
                    services.AddSingleton<TimeProvider>(Time);
                });
            });
        }

        public static string NewRoot() => Path.Combine(Path.GetTempPath(), "NodePad_AppTest_" + Guid.NewGuid().ToString("N"));

        public CredentialStore Credentials => Factory.Services.GetRequiredService<CredentialStore>();

        public HttpClient CreateClient() =>
            Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public void Dispose()
        {
            Factory.Dispose();
            if (_ownsRoot && Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    /// <summary>
    /// A clock that stays at <see cref="Now"/> (in UTC) until a test changes it.
    /// </summary>
    public sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
