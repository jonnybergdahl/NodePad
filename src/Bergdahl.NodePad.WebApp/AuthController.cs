using System.Security.Claims;
using Bergdahl.NodePad.WebApp.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bergdahl.NodePad.WebApp;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    public const string StampClaim = "nodepad:stamp";
    public const string LoginRateLimitPolicy = "login";
    private const int MinPasswordLength = 6;

    private readonly CredentialStore _credentials;
    private readonly ILogger<AuthController> _logger;

    public AuthController(CredentialStore credentials, ILogger<AuthController> logger)
    {
        _credentials = credentials;
        _logger = logger;
    }

    public class LoginRequest
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool RememberMe { get; set; }
    }

    public class SetupRequest
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    public class ChangePasswordRequest
    {
        public string? CurrentPassword { get; set; }
        public string? NewPassword { get; set; }
    }

    [HttpGet("status")]
    [AllowAnonymous]
    public IActionResult Status()
    {
        var authenticated = User.Identity?.IsAuthenticated == true;
        return Ok(new
        {
            configured = _credentials.IsConfigured,
            authenticated,
            username = authenticated ? User.Identity!.Name : null,
        });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimitPolicy)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (!_credentials.IsConfigured)
        {
            return Conflict(new { error = "No account has been set up yet" });
        }

        if (!_credentials.Verify(request.Username, request.Password))
        {
            _logger.LogWarning("Failed login attempt for {Username} from {Ip}", request.Username, HttpContext.Connection.RemoteIpAddress);
            return BadRequest(new { error = "Invalid username or password" });
        }

        await SignInAsync(_credentials.Username!, request.RememberMe);
        _logger.LogInformation("User {Username} signed in", _credentials.Username);
        return Ok(new { username = _credentials.Username });
    }

    /// <summary>
    /// First-run account creation. Only allowed while no account exists in nodepad.auth.json.
    /// </summary>
    [HttpPost("setup")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimitPolicy)]
    public async Task<IActionResult> Setup([FromBody] SetupRequest request)
    {
        if (_credentials.IsConfigured)
        {
            return Conflict(new { error = "An account already exists" });
        }

        var username = request.Username?.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            return BadRequest(new { error = "Username is required" });
        }
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < MinPasswordLength)
        {
            return BadRequest(new { error = $"Password must be at least {MinPasswordLength} characters" });
        }

        _credentials.SetCredentials(username, request.Password);
        _logger.LogInformation("Account {Username} created from {Ip}", username, HttpContext.Connection.RemoteIpAddress);

        await SignInAsync(username, rememberMe: false);
        return Ok(new { username });
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok();
    }

    [HttpPost("change-password")]
    [EnableRateLimiting(LoginRateLimitPolicy)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var username = _credentials.Username;
        if (username == null || !_credentials.Verify(username, request.CurrentPassword))
        {
            return BadRequest(new { error = "Current password is incorrect" });
        }
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < MinPasswordLength)
        {
            return BadRequest(new { error = $"Password must be at least {MinPasswordLength} characters" });
        }

        _credentials.SetCredentials(username, request.NewPassword);
        _logger.LogInformation("Password changed for {Username}", username);

        // Re-issue this session's cookie with the new stamp; other sessions become invalid
        var current = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await SignInAsync(username, rememberMe: current.Properties?.IsPersistent == true);
        return Ok();
    }

    /// <summary>
    /// Whether an API key for the MCP endpoint exists. The key itself is never returned here.
    /// </summary>
    [HttpGet("api-key")]
    public IActionResult GetApiKeyStatus()
    {
        return Ok(new { exists = _credentials.HasApiKey });
    }

    /// <summary>
    /// Creates a new API key for the MCP endpoint, replacing any existing one.
    /// The key is only returned in this response.
    /// </summary>
    [HttpPost("api-key")]
    public IActionResult GenerateApiKey()
    {
        var apiKey = _credentials.GenerateApiKey();
        _logger.LogInformation("API key generated by {Username}", User.Identity?.Name);
        return Ok(new { apiKey });
    }

    [HttpDelete("api-key")]
    public IActionResult RevokeApiKey()
    {
        _credentials.RevokeApiKey();
        _logger.LogInformation("API key revoked by {Username}", User.Identity?.Name);
        return Ok();
    }

    private Task SignInAsync(string username, bool rememberMe)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(StampClaim, _credentials.Stamp),
        }, CookieAuthenticationDefaults.AuthenticationScheme);

        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = rememberMe });
    }
}
