using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SyncthingNotifier;

internal sealed class SyncthingApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public SyncthingApiClient(SyncthingConfiguration configuration)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = configuration.GuiUri,
            Timeout = TimeSpan.FromSeconds(75)
        };
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", configuration.ApiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyDictionary<string, double>> GetCompletionsAsync(
        IEnumerable<string> folderIds,
        CancellationToken cancellationToken)
    {
        var completions = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var folderId in folderIds)
        {
            completions[folderId] = await GetCompletionAsync(folderId, cancellationToken);
        }

        return completions;
    }

    public async Task<double> GetCompletionAsync(string folderId, CancellationToken cancellationToken)
    {
        var path = $"rest/db/completion?folder={Uri.EscapeDataString(folderId)}";
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("completion", out var completion) ||
            !completion.TryGetDouble(out var value))
        {
            throw new InvalidOperationException($"Syncthing did not return a completion percentage for '{folderId}'.");
        }

        return value;
    }

    public async Task<IReadOnlyList<SyncthingEvent>> GetEventsAsync(long since, CancellationToken cancellationToken)
    {
        var path = $"rest/events?since={since.ToString(CultureInfo.InvariantCulture)}&timeout=60";
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Syncthing returned an invalid events response.");
        }

        var events = new List<SyncthingEvent>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) ||
                !element.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            var type = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            DateTimeOffset? time = null;
            if (element.TryGetProperty("time", out var timeElement) &&
                timeElement.TryGetDateTimeOffset(out var parsedTime))
            {
                time = parsedTime;
            }

            var data = element.TryGetProperty("data", out var dataElement)
                ? dataElement.Clone()
                : default;
            events.Add(new SyncthingEvent(id, type, time, data));
        }

        return events;
    }

    public void Dispose() => _httpClient.Dispose();
}

internal sealed record SyncthingEvent(long Id, string Type, DateTimeOffset? Time, JsonElement Data)
{
    public string? FolderId =>
        Data.ValueKind == JsonValueKind.Object &&
        Data.TryGetProperty("folder", out var folder) &&
        folder.ValueKind == JsonValueKind.String
            ? folder.GetString()
            : null;

    public string? State =>
        Data.ValueKind == JsonValueKind.Object &&
        Data.TryGetProperty("to", out var state) &&
        state.ValueKind == JsonValueKind.String
            ? state.GetString()
            : null;

    public double? Completion =>
        Data.ValueKind == JsonValueKind.Object &&
        Data.TryGetProperty("completion", out var completion) &&
        completion.TryGetDouble(out var value)
            ? value
            : null;
}
