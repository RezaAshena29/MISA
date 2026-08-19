using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MISA.UI.Services;

public sealed class MisaChatOptions
{
    public string BaseUrl { get; set; } = "https://localhost:5443";

    public string Route { get; set; } = "/api/irt/chat";

    public string? FallbackBaseUrl { get; set; } = "http://127.0.0.1:7071";

    public string? FallbackRoute { get; set; } = "/api/irt/chat";

    public string Product { get; set; } = "par";

    public string Language { get; set; } = "en";
}

public sealed record ChatRequestPayload(
    string Message,
    string SessionId,
    bool ContextConsent = false,
    object? UdmContext = null);

public sealed record ChatStreamEvent(
    string Type,
    JsonElement? ContentElement,
    string? TextContent);

public sealed class MisaChatService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly MisaChatOptions _options;

    public MisaChatService(HttpClient httpClient, IOptions<MisaChatOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task StreamChatAsync(
        ChatRequestPayload payload,
        Func<ChatStreamEvent, Task> onEvent,
        CancellationToken cancellationToken)
    {
        var primaryEndpoint = BuildEndpoint(_options.BaseUrl, _options.Route);
        Exception? lastError = null;

        try
        {
            await StreamFromEndpointAsync(primaryEndpoint, payload, onEvent, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        var hasFallback = !string.IsNullOrWhiteSpace(_options.FallbackBaseUrl)
            && !string.IsNullOrWhiteSpace(_options.FallbackRoute);

        if (!hasFallback)
        {
            throw lastError ?? new InvalidOperationException("Chat stream failed and no fallback endpoint is configured.");
        }

        var fallbackEndpoint = BuildEndpoint(_options.FallbackBaseUrl!, _options.FallbackRoute!);
        await StreamFromEndpointAsync(fallbackEndpoint, payload, onEvent, cancellationToken).ConfigureAwait(false);
    }

    private async Task StreamFromEndpointAsync(
        string endpoint,
        ChatRequestPayload payload,
        Func<ChatStreamEvent, Task> onEvent,
        CancellationToken cancellationToken)
    {
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint);
        requestMessage.Headers.Accept.ParseAdd("text/event-stream");

        var body = new Dictionary<string, object?>
        {
            ["message"] = payload.Message,
            ["sessionId"] = payload.SessionId,
            ["session_id"] = payload.SessionId,
            ["product"] = _options.Product,
            ["language"] = _options.Language,
            ["contextConsent"] = payload.ContextConsent,
            ["context_consent"] = payload.ContextConsent,
            ["udmContext"] = payload.UdmContext,
            ["udm_context"] = payload.UdmContext
        };

        requestMessage.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient
            .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var eventType = "message";
        var dataLines = new List<string>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                await EmitFrameAsync(eventType, dataLines, onEvent).ConfigureAwait(false);
                eventType = "message";
                dataLines.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventType = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line[5..].Trim());
            }
        }

        if (dataLines.Count > 0)
        {
            await EmitFrameAsync(eventType, dataLines, onEvent).ConfigureAwait(false);
        }
    }

    private static async Task EmitFrameAsync(
        string eventType,
        List<string> dataLines,
        Func<ChatStreamEvent, Task> onEvent)
    {
        if (dataLines.Count == 0)
        {
            return;
        }

        var payloadText = string.Join("\n", dataLines);
        var normalizedType = eventType;
        JsonElement? contentElement = null;
        string? textContent = payloadText;

        try
        {
            using var document = JsonDocument.Parse(payloadText);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("type", out var typeElement)
                    && typeElement.ValueKind == JsonValueKind.String)
                {
                    normalizedType = typeElement.GetString() ?? eventType;
                }

                if (root.TryGetProperty("content", out var content))
                {
                    contentElement = content.Clone();
                    textContent = content.ValueKind == JsonValueKind.String
                        ? content.GetString()
                        : content.GetRawText();
                }
            }
        }
        catch (JsonException)
        {
            // Keep plain-text payload behavior for malformed or non-JSON frames.
        }

        await onEvent(new ChatStreamEvent(normalizedType, contentElement, textContent)).ConfigureAwait(false);
    }

    private static string BuildEndpoint(string baseUrl, string route)
    {
        var normalizedBase = baseUrl.TrimEnd('/');
        var normalizedRoute = route.StartsWith('/') ? route : $"/{route}";
        return normalizedBase + normalizedRoute;
    }
}
