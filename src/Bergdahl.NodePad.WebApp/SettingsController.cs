using Bergdahl.NodePad.WebApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bergdahl.NodePad.WebApp;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SettingsController> _logger;
    private readonly string _settingsPath;

    public SettingsController(IConfiguration configuration, ILogger<SettingsController> logger)
    {
        _configuration = configuration;
        _logger = logger;
        // Resolve nodepad.json in content root
        _settingsPath = Path.Combine(Directory.GetCurrentDirectory(), "nodepad.json");
    }

    public class AppSettingsDto
    {
        public string? AllowedHosts { get; set; }
        public string? PagesDirectory { get; set; }
        public string? BackupDirectory { get; set; }
    }

    [HttpGet]
    public IActionResult Get()
    {
        var dto = new AppSettingsDto
        {
            AllowedHosts = _configuration["AllowedHosts"],
            PagesDirectory = _configuration["PagesDirectory"],
            BackupDirectory = _configuration["BackupDirectory"],
        };
        return Ok(dto);
    }

    [HttpPost]
    public IActionResult Save([FromBody] AppSettingsDto input)
    {
        if (input == null) return BadRequest("No settings provided");

        try
        {
            // Load existing nodepad.json; it is not published, so start a new one if it is missing
            JsonObject? node = new JsonObject();
            if (System.IO.File.Exists(_settingsPath))
            {
                var json = System.IO.File.ReadAllText(_settingsPath);
                node = JsonNode.Parse(json) as JsonObject;
                if (node == null)
                {
                    return StatusCode(500, "Failed to parse nodepad.json");
                }
            }

            // Update values if provided; a blank value removes the setting so the default is used
            // (an empty AllowedHosts or PagesDirectory would otherwise break the app)
            SetOrRemove(node, "AllowedHosts", input.AllowedHosts);
            SetOrRemove(node, "PagesDirectory", input.PagesDirectory);
            SetOrRemove(node, "BackupDirectory", input.BackupDirectory);

            // Persist back pretty-printed
            var options = new JsonSerializerOptions { WriteIndented = true };
            System.IO.File.WriteAllText(_settingsPath, node.ToJsonString(options));

            // Ensure directories exist if set
            var pagesDir = (string?)node["PagesDirectory"];
            var backupDir = (string?)node["BackupDirectory"];

            if (!string.IsNullOrWhiteSpace(pagesDir))
            {
                var pagesFull = Path.IsPathRooted(pagesDir)
                    ? pagesDir
                    : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), pagesDir));
                if (!Directory.Exists(pagesFull)) Directory.CreateDirectory(pagesFull);
            }
            if (!string.IsNullOrWhiteSpace(backupDir))
            {
                var backupFull = Path.IsPathRooted(backupDir)
                    ? backupDir
                    : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), backupDir));
                if (!Directory.Exists(backupFull)) Directory.CreateDirectory(backupFull);
            }

            _logger.LogInformation("Settings updated: AllowedHosts={AllowedHosts}, PagesDirectory={PagesDirectory}, BackupDirectory={BackupDirectory}",
                (string?)node["AllowedHosts"],
                (string?)node["PagesDirectory"],
                (string?)node["BackupDirectory"]);

            // Return the settings as saved (the configuration only updates after reloadOnChange)
            return Ok(new AppSettingsDto
            {
                AllowedHosts = (string?)node["AllowedHosts"],
                PagesDirectory = (string?)node["PagesDirectory"],
                BackupDirectory = (string?)node["BackupDirectory"],
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            return StatusCode(500, "Failed to save settings");
        }
    }

    public class HomeAssistantSettingsDto
    {
        public string? Url { get; set; }
        /// <summary>Write only; empty keeps the current token.</summary>
        public string? Token { get; set; }
        public bool Active { get; set; }
    }

    [HttpGet("home-assistant")]
    public IActionResult GetHomeAssistant(
        [FromServices] HomeAssistantSettingsStore store, [FromServices] HomeAssistantSync sync)
    {
        return Ok(HomeAssistantResponse(store.Current, sync, null));
    }

    /// <summary>
    /// Saves the Home Assistant settings. While syncing is active, every save checks the address and token;
    /// changed settings make the sync reconnect, which syncs all lists.
    /// </summary>
    [HttpPost("home-assistant")]
    public async Task<IActionResult> SaveHomeAssistant([FromBody] HomeAssistantSettingsDto input,
        [FromServices] HomeAssistantSettingsStore store, [FromServices] HomeAssistantSync sync, CancellationToken ct)
    {
        if (input == null) return BadRequest(new { error = "No settings provided" });

        var url = (input.Url ?? string.Empty).Trim().TrimEnd('/');
        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            return BadRequest(new { error = "The address must start with http:// or https://" });
        }
        var token = string.IsNullOrWhiteSpace(input.Token) ? store.Current.Token : input.Token.Trim();
        if (input.Active && (url.Length == 0 || string.IsNullOrWhiteSpace(token)))
        {
            return BadRequest(new { error = "Enter the address and an access token to turn on syncing" });
        }

        try
        {
            var saved = store.Save(new HomeAssistantSettingsStore.Settings(url.Length > 0 ? url : null, token, input.Active));
            _logger.LogInformation("Home Assistant settings updated: Url={Url}, Active={Active}", saved.Url, saved.Active);

            string? connectionError = null;
            if (saved.IsUsable) connectionError = await sync.CheckConnectionAsync(ct);
            return Ok(HomeAssistantResponse(saved, sync, connectionError));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "Failed to save Home Assistant settings");
            return StatusCode(500, new { error = "Failed to save settings" });
        }
    }

    private static object HomeAssistantResponse(HomeAssistantSettingsStore.Settings settings, HomeAssistantSync sync, string? connectionError)
    {
        var status = sync.Status;
        return new
        {
            url = settings.Url,
            active = settings.Active,
            hasToken = !string.IsNullOrWhiteSpace(settings.Token),
            connected = status.Connected,
            lastSync = status.LastSync,
            lastError = status.LastError,
            connectionError,
        };
    }

    private static void SetOrRemove(JsonObject node, string key, string? value)
    {
        if (value == null) return;
        if (string.IsNullOrWhiteSpace(value))
            node.Remove(key);
        else
            node[key] = value.Trim();
    }
}