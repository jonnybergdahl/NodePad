using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Bergdahl.NodePad.WebApp.Auth;

/// <summary>
/// Authenticates requests carrying the NodePad API key, either as
/// "Authorization: Bearer &lt;key&gt;" or as an "X-Api-Key" header. Used for the MCP endpoint.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly CredentialStore _credentials;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        CredentialStore credentials)
        : base(options, logger, encoder)
    {
        _credentials = credentials;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = ReadKey();
        if (key == null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!_credentials.VerifyApiKey(key))
        {
            Logger.LogWarning("Invalid API key from {Ip}", Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, _credentials.Username ?? "api"),
        }, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    private string? ReadKey()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = header["Bearer ".Length..].Trim();
            return value.Length > 0 ? value : null;
        }

        var apiKey = Request.Headers[HeaderName].ToString().Trim();
        return apiKey.Length > 0 ? apiKey : null;
    }
}
