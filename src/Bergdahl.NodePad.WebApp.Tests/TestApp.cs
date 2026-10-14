using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Bergdahl.NodePad.WebApp.Auth;
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
        public string Root { get; }
        public string PagesPath => Path.Combine(Root, "Pages");
        public string AuthFilePath => Path.Combine(Root, CredentialStore.FileName);
        public WebApplicationFactory<Program> Factory { get; }

        public TestApp()
        {
            Root = Path.Combine(Path.GetTempPath(), "NodePad_AppTest_" + Guid.NewGuid().ToString("N"));
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
                });
                builder.ConfigureTestServices(services =>
                {
                    // Never touch a real nodepad.auth.json
                    services.AddSingleton(sp => new CredentialStore(sp.GetRequiredService<IConfiguration>(), AuthFilePath));
                });
            });
        }

        public CredentialStore Credentials => Factory.Services.GetRequiredService<CredentialStore>();

        public HttpClient CreateClient() =>
            Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public void Dispose()
        {
            Factory.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
