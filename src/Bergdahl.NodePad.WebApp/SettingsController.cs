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
            // Load existing nodepad.json
            if (!System.IO.File.Exists(_settingsPath))
            {
                return StatusCode(500, "nodepad.json not found");
            }

            var json = System.IO.File.ReadAllText(_settingsPath);
            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null)
            {
                return StatusCode(500, "Failed to parse nodepad.json");
            }

            // Update values if provided (allow empty string to be set explicitly)
            if (input.AllowedHosts != null)
                node["AllowedHosts"] = input.AllowedHosts;

            if (input.PagesDirectory != null)
                node["PagesDirectory"] = input.PagesDirectory;

            if (input.BackupDirectory != null)
                node["BackupDirectory"] = input.BackupDirectory;

            // Persist back pretty-printed
            var options = new JsonSerializerOptions { WriteIndented = true };
            System.IO.File.WriteAllText(_settingsPath, node.ToJsonString(options));

            // Ensure directories exist if set
            var pagesDir = input.PagesDirectory ?? _configuration["PagesDirectory"];
            var backupDir = input.BackupDirectory ?? _configuration["BackupDirectory"];

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
                input.AllowedHosts ?? _configuration["AllowedHosts"],
                input.PagesDirectory ?? _configuration["PagesDirectory"],
                input.BackupDirectory ?? _configuration["BackupDirectory"]);

            // Return the effective settings from configuration (may update after reloadOnChange)
            return Ok(new AppSettingsDto
            {
                AllowedHosts = input.AllowedHosts ?? _configuration["AllowedHosts"],
                PagesDirectory = input.PagesDirectory ?? _configuration["PagesDirectory"],
                BackupDirectory = input.BackupDirectory ?? _configuration["BackupDirectory"],
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            return StatusCode(500, "Failed to save settings");
        }
    }
}