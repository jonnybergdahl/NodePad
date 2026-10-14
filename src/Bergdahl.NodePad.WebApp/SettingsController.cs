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

    private static void SetOrRemove(JsonObject node, string key, string? value)
    {
        if (value == null) return;
        if (string.IsNullOrWhiteSpace(value))
            node.Remove(key);
        else
            node[key] = value.Trim();
    }
}