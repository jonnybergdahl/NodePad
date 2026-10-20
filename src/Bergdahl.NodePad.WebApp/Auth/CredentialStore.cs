using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Primitives;

namespace Bergdahl.NodePad.WebApp.Auth;

/// <summary>
/// Holds the single NodePad account and the MCP API key
/// (Auth:Username / Auth:PasswordHash / Auth:ApiKeyHash in nodepad.auth.json).
/// </summary>
public class CredentialStore
{
    public const string FileName = "nodepad.auth.json";
    private const string ApiKeyPrefix = "np_";

    /// <summary>
    /// Taken around every read-modify-write of nodepad.auth.json, which also holds the Home Assistant settings.
    /// </summary>
    public static readonly object FileLock = new();

    private readonly IConfiguration _configuration;
    private readonly string _authFilePath;
    private readonly object _lock = new();

    private sealed record AuthSettings(string? Username, string? PasswordHash, string? ApiKeyHash);

    // Values written by this process; used until the configuration reload picks them up.
    private AuthSettings? _written;

    public CredentialStore(IConfiguration configuration, string authFilePath)
    {
        _configuration = configuration;
        _authFilePath = authFilePath;
        ChangeToken.OnChange(configuration.GetReloadToken, () => { lock (_lock) { _written = null; } });
    }

    public string? Username => Current().Username;

    public bool IsConfigured
    {
        get
        {
            var current = Current();
            return !string.IsNullOrWhiteSpace(current.Username) && !string.IsNullOrWhiteSpace(current.PasswordHash);
        }
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(Current().ApiKeyHash);

    /// <summary>
    /// Short fingerprint of the current password hash, stored in the auth cookie so that
    /// changing the password invalidates existing sessions.
    /// </summary>
    public string Stamp
    {
        get
        {
            var hash = Current().PasswordHash ?? string.Empty;
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(hash));
            return Convert.ToBase64String(digest, 0, 12);
        }
    }

    public bool Verify(string? username, string? password)
    {
        var current = Current();
        // Always run the hash check so timing does not reveal whether the username matched
        var passwordOk = PasswordHasher.Verify(password ?? string.Empty, current.PasswordHash);
        var userOk = !string.IsNullOrWhiteSpace(current.Username)
                     && string.Equals(current.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase);
        return passwordOk && userOk;
    }

    public void SetCredentials(string username, string password)
    {
        var hash = PasswordHasher.Hash(password);
        Update(current => current with { Username = username, PasswordHash = hash });
    }

    public bool VerifyApiKey(string? apiKey)
    {
        var stored = Current().ApiKeyHash;
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(stored)) return false;
        var actual = Encoding.ASCII.GetBytes(HashApiKey(apiKey));
        var expected = Encoding.ASCII.GetBytes(stored);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Creates a new API key, replacing any existing one. Only its hash is stored, so the
    /// returned key cannot be shown again.
    /// </summary>
    public string GenerateApiKey()
    {
        // 32 random bytes; the key has full entropy, so a plain SHA-256 hash is enough to store it
        var key = ApiKeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = HashApiKey(key);
        Update(current => current with { ApiKeyHash = hash });
        return key;
    }

    public void RevokeApiKey()
    {
        Update(current => current with { ApiKeyHash = null });
    }

    private static string HashApiKey(string apiKey)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
    }

    private void Update(Func<AuthSettings, AuthSettings> change)
    {
        lock (FileLock)
        lock (_lock)
        {
            var updated = change(CurrentUnlocked());

            JsonObject root;
            if (File.Exists(_authFilePath))
            {
                root = JsonNode.Parse(File.ReadAllText(_authFilePath)) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var auth = root["Auth"] as JsonObject ?? new JsonObject();
            SetOrRemove(auth, "Username", updated.Username);
            SetOrRemove(auth, "PasswordHash", updated.PasswordHash);
            SetOrRemove(auth, "ApiKeyHash", updated.ApiKeyHash);
            root["Auth"] = auth;

            File.WriteAllText(_authFilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _written = updated;
        }
    }

    private static void SetOrRemove(JsonObject obj, string name, string? value)
    {
        if (value == null) obj.Remove(name);
        else obj[name] = value;
    }

    private AuthSettings Current()
    {
        lock (_lock)
        {
            return CurrentUnlocked();
        }
    }

    private AuthSettings CurrentUnlocked()
    {
        return _written ?? new AuthSettings(
            _configuration["Auth:Username"],
            _configuration["Auth:PasswordHash"],
            _configuration["Auth:ApiKeyHash"]);
    }
}
