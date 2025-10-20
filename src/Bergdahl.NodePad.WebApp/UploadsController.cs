using Microsoft.AspNetCore.Mvc;

namespace Bergdahl.NodePad.WebApp;

[ApiController]
[Route("api/uploads")]
public class UploadsController : ControllerBase
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml"
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg"
    };

    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<UploadsController> _logger;

    public UploadsController(IWebHostEnvironment env, IConfiguration config, ILogger<UploadsController> logger)
    {
        _env = env;
        _config = config;
        _logger = logger;
    }

    [HttpPost("images")]
    [RequestFormLimits(MultipartBodyLengthLimit = 25_000_000)]
    [RequestSizeLimit(25_000_000)]
    public async Task<IActionResult> UploadImage([FromForm] IFormFile? image, [FromQuery] string? pagePath)
    {
        if (image == null || image.Length == 0)
        {
            return BadRequest(new { error = "No image provided" });
        }

        // Read settings with sensible defaults
        var maxBytes = _config.GetValue<long?>("Uploads:MaxBytes") ?? 10L * 1024L * 1024L; // 10 MB default
        if (image.Length > maxBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = $"File too large. Max {maxBytes / (1024 * 1024)} MB" });
        }

        var contentType = image.ContentType ?? string.Empty;
        var ext = Path.GetExtension(image.FileName ?? string.Empty);

        // Validate type by content type and extension to be safe
        if (!AllowedContentTypes.Contains(contentType) || !AllowedExtensions.Contains(ext))
        {
            return BadRequest(new { error = "Unsupported image type" });
        }

        try
        {
            string? targetDir = null;
            string? returnedBaseUrl = null;

            // If we got a pagePath, try to place the image next to that markdown file under PagesDirectory
            if (!string.IsNullOrWhiteSpace(pagePath))
            {
                // Sanitize pagePath similar to PagesController
                var pagesRoot = _config.GetValue<string>("PagesDirectory") ?? Path.Combine(Directory.GetCurrentDirectory(), "Pages");
                var sanitized = pagePath.Replace("..", string.Empty).Replace("\\", "/");
                var fullPagePath = Path.GetFullPath(Path.Combine(pagesRoot, sanitized));
                var pagesRootFull = Path.GetFullPath(pagesRoot);

                if (fullPagePath.StartsWith(pagesRootFull, StringComparison.OrdinalIgnoreCase)
                    && fullPagePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    targetDir = Path.GetDirectoryName(fullPagePath);
                    if (!string.IsNullOrEmpty(targetDir))
                    {
                        // Return URL will be served from /pages via static file mapping
                        var relDir = fullPagePath.Substring(pagesRootFull.Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        relDir = Path.GetDirectoryName(relDir) ?? string.Empty;
                        returnedBaseUrl = "/pages/" + relDir.Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/');
                        if (returnedBaseUrl.EndsWith("/")) returnedBaseUrl = returnedBaseUrl.TrimEnd('/');
                    }
                }
            }

            // Fallback to wwwroot/uploads/images if pagePath is not provided or invalid
            if (string.IsNullOrEmpty(targetDir))
            {
                var uploadsRoot = Path.Combine(_env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "uploads", "images");
                targetDir = uploadsRoot;
                returnedBaseUrl = "/uploads/images";
            }

            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            var safeBaseName = Path.GetFileNameWithoutExtension(image.FileName);
            safeBaseName = string.IsNullOrWhiteSpace(safeBaseName) ? "image" : SanitizeFileName(safeBaseName);
            var finalFileName = $"{safeBaseName}{ext.ToLowerInvariant()}";

            var savePath = Path.Combine(targetDir, finalFileName);
            await using (var stream = new FileStream(savePath, FileMode.Create))
            {
                await image.CopyToAsync(stream);
            }

            var urlPath = $"{returnedBaseUrl}/{Uri.EscapeDataString(finalFileName)}";
            _logger.LogInformation("Image uploaded: {Url}", urlPath);
            return Ok(new { url = urlPath });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading image");
            return StatusCode(500, new { error = "Failed to upload image" });
        }
    }

    private static string SanitizeFileName(string name)
    {
        // Remove invalid characters and collapse spaces
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", "-");
        cleaned = cleaned.Trim('-');
        return cleaned.Length == 0 ? "image" : cleaned;
    }
}