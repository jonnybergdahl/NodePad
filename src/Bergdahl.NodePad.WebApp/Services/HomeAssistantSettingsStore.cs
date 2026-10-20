using System.Text.Json;
using System.Text.Json.Nodes;
using Bergdahl.NodePad.WebApp.Auth;
using Microsoft.Extensions.Primitives;

namespace Bergdahl.NodePad.WebApp.Services;

/// <summary>
/// Home Assistant connection settings (HomeAssistant:Url / Token / Active). They are kept in
/// nodepad.auth.json next to the account, since the access token is a secret and that file stays
/// out of source control and publish output.
/// </summary>
public class HomeAssistantSettingsStore
{
    public sealed record Settings(string? Url, string? Token, bool Active)
    {
        /// <summary>Syncing is on and there is something to connect to.</summary>
        public bool IsUsable => Active && !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Token);
    }

    private readonly IConfiguration _configuration;
    private readonly string _filePath;
    private readonly object _lock = new();

    // Values written by this process; used until the configuration reload picks them up.
    private Settings? _written;

    public HomeAssistantSettingsStore(IConfiguration configuration, string filePath)
    {
        _configuration = configuration;
        _filePath = filePath;
        ChangeToken.OnChange(configuration.GetReloadToken, () =>
        {
            lock (_lock) { _written = null; }
            Changed?.Invoke();
        });
    }

    /// <summary>
    /// Raised after the settings were saved or the file was reloaded; the values may be unchanged.
    /// </summary>
    public event Action? Changed;

    public Settings Current
    {
        get
        {
            lock (_lock)
            {
                return _written ?? new Settings(
                    _configuration["HomeAssistant:Url"],
                    _configuration["HomeAssistant:Token"],
                    bool.TryParse(_configuration["HomeAssistant:Active"], out var active) && active);
            }
        }
    }

    public Settings Save(Settings settings)
    {
        Write(settings);
        Changed?.Invoke();
        return settings;
    }

    private void Write(Settings settings)
    {
        lock (CredentialStore.FileLock)
        lock (_lock)
        {
            JsonObject root;
            if (File.Exists(_filePath))
            {
                root = JsonNode.Parse(File.ReadAllText(_filePath)) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var section = new JsonObject { ["Active"] = settings.Active };
            if (!string.IsNullOrWhiteSpace(settings.Url)) section["Url"] = settings.Url;
            if (!string.IsNullOrWhiteSpace(settings.Token)) section["Token"] = settings.Token;
            root["HomeAssistant"] = section;

            File.WriteAllText(_filePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _written = settings;
        }
    }
}
