using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bergdahl.NodePad.WebApp.Services;

public class HomeAssistantException : Exception
{
    public HomeAssistantException(string message) : base(message) { }
}

/// <summary>
/// The parts of the Home Assistant REST API that the ToDo sync uses. NodePad lists become entries of
/// the Local To-do integration (created through its config flow, which needs an administrator's token);
/// items are read and changed with the todo services. Changes made in Home Assistant arrive through
/// <see cref="HomeAssistantSocket"/>.
/// </summary>
public class HomeAssistantClient
{
    public const string HttpClientName = "HomeAssistant";
    private const string LocalTodoDomain = "local_todo";

    public sealed record ConfigEntry(string EntryId, string Title);

    public sealed record Item(string Uid, string Summary, bool Done);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;

    public HomeAssistantClient(HttpClient http, string url, string token)
    {
        _http = http;
        _baseUrl = url.TrimEnd('/') + "/";
        _token = token;
    }

    /// <summary>
    /// Throws a <see cref="HomeAssistantException"/> if Home Assistant cannot be reached or rejects the token.
    /// </summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        await SendAsync(HttpMethod.Get, "api/", null, ct);
    }

    // ---------------------------------------------------------------------
    // Lists
    // ---------------------------------------------------------------------

    public async Task<List<ConfigEntry>> GetLocalTodoEntriesAsync(CancellationToken ct)
    {
        var entries = await SendAsync(HttpMethod.Get, $"api/config/config_entries/entry?domain={LocalTodoDomain}", null, ct);
        return entries.EnumerateArray()
            .Where(e => String(e, "domain") is null or LocalTodoDomain)
            .Select(e => new ConfigEntry(String(e, "entry_id") ?? string.Empty, String(e, "title") ?? string.Empty))
            .Where(e => e.EntryId.Length > 0)
            .ToList();
    }

    public async Task CreateLocalTodoAsync(string name, CancellationToken ct)
    {
        var flow = await SendAsync(HttpMethod.Post, "api/config/config_entries/flow", new { handler = LocalTodoDomain }, ct);
        var flowId = String(flow, "flow_id") ?? throw new HomeAssistantException("Home Assistant did not start the Local To-do setup");
        var result = await SendAsync(HttpMethod.Post, $"api/config/config_entries/flow/{flowId}", new { todo_list_name = name }, ct);
        if (String(result, "type") != "create_entry")
        {
            var reason = String(result, "reason") ?? String(result, "type") ?? "unknown";
            throw new HomeAssistantException($"Home Assistant did not create the to do list \"{name}\" ({reason})");
        }
    }

    public async Task DeleteEntryAsync(string entryId, CancellationToken ct)
    {
        await SendAsync(HttpMethod.Delete, $"api/config/config_entries/entry/{entryId}", null, ct);
    }

    /// <summary>
    /// The todo entity for a list: the one whose name matches (Local To-do capitalizes it, so case is ignored),
    /// else the entity id Home Assistant derives from the name.
    /// </summary>
    public async Task<string?> FindTodoEntityAsync(string name, CancellationToken ct)
    {
        var states = await SendAsync(HttpMethod.Get, "api/states", null, ct);
        var slugId = "todo." + Slugify(name);
        string? bySlug = null;
        foreach (var state in states.EnumerateArray())
        {
            var entityId = String(state, "entity_id");
            if (entityId == null || !entityId.StartsWith("todo.", StringComparison.Ordinal)) continue;
            var friendlyName = state.TryGetProperty("attributes", out var attributes) ? String(attributes, "friendly_name") : null;
            if (string.Equals(friendlyName, name, StringComparison.OrdinalIgnoreCase)) return entityId;
            if (entityId == slugId) bySlug = entityId;
        }
        return bySlug;
    }

    // ---------------------------------------------------------------------
    // Items
    // ---------------------------------------------------------------------

    public async Task<List<Item>> GetItemsAsync(string entityId, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Post, "api/services/todo/get_items?return_response",
            new { entity_id = entityId, status = new[] { "needs_action", "completed" } }, ct);
        if (!result.TryGetProperty("service_response", out var response)
            || !response.TryGetProperty(entityId, out var entity)
            || !entity.TryGetProperty("items", out var items))
        {
            throw new HomeAssistantException($"Home Assistant returned no items for {entityId}");
        }
        return ParseItems(items);
    }

    /// <summary>
    /// Items as NodePad sees them: the text on one line with whitespace collapsed; items without text are left out.
    /// </summary>
    internal static List<Item> ParseItems(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) return new List<Item>();
        return items.EnumerateArray()
            .Select(i => new Item(
                String(i, "uid") ?? string.Empty,
                Regex.Replace(String(i, "summary") ?? string.Empty, @"\s+", " ").Trim(),
                String(i, "status") == "completed"))
            .Where(i => i.Uid.Length > 0 && i.Summary.Length > 0)
            .ToList();
    }

    public async Task AddItemAsync(string entityId, string text, CancellationToken ct)
    {
        await SendAsync(HttpMethod.Post, "api/services/todo/add_item", new { entity_id = entityId, item = text }, ct);
    }

    public async Task SetItemDoneAsync(string entityId, string uid, bool done, CancellationToken ct)
    {
        await SendAsync(HttpMethod.Post, "api/services/todo/update_item",
            new { entity_id = entityId, item = uid, status = done ? "completed" : "needs_action" }, ct);
    }

    public async Task RemoveItemsAsync(string entityId, IEnumerable<string> uids, CancellationToken ct)
    {
        await SendAsync(HttpMethod.Post, "api/services/todo/remove_item", new { entity_id = entityId, item = uids.ToArray() }, ct);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body != null) request.Content = JsonContent.Create(body);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new HomeAssistantException($"Could not connect to Home Assistant: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HomeAssistantException("Home Assistant did not answer in time");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new HomeAssistantException("Home Assistant rejected the access token");
            if (!response.IsSuccessStatusCode)
                throw new HomeAssistantException($"Home Assistant returned {(int)response.StatusCode} {response.ReasonPhrase} for {method} /{path}");

            var text = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return default;
            try
            {
                return JsonDocument.Parse(text).RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new HomeAssistantException($"Home Assistant returned an unexpected answer for {method} /{path}");
            }
        }
    }

    internal static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Home Assistant's slugify: accents removed, lowercase, runs of other characters become "_".
    /// </summary>
    internal static string Slugify(string text)
    {
        var withoutAccents = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) withoutAccents.Append(ch);
        }
        var slug = Regex.Replace(withoutAccents.ToString().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        return slug.Length == 0 ? "unknown" : slug;
    }
}
