using System.Net;
using System.Text.Json;
using MISA.Application;
using MISA.Contracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace MISA.Functions;

/// <summary>
/// Contract-compatible IRT chat and health HTTP triggers.
/// </summary>
public sealed partial class ChatFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IChatPipeline _pipeline;
    private readonly IChatSessionStore _sessionStore;
    private readonly ILogger<ChatFunctions> _logger;

    /// <summary>
    /// Creates function handlers.
    /// </summary>
    public ChatFunctions(
        IChatPipeline pipeline,
        IChatSessionStore sessionStore,
        ILogger<ChatFunctions> logger)
    {
        _pipeline = pipeline;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    /// <summary>
    /// Streams chat events in SSE format.
    /// </summary>
    [Function(nameof(Chat))]
    public async Task<HttpResponseData> Chat(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = ChatContracts.ChatRoute)] HttpRequestData request)
    {
        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/event-stream");
        response.Headers.Add("Cache-Control", "no-cache");
        response.Headers.Add("X-Accel-Buffering", "no");

        var payload = await JsonSerializer.DeserializeAsync<ChatRequestDto>(
            request.Body,
            JsonOptions,
            request.FunctionContext.CancellationToken).ConfigureAwait(false);

        if (payload is null || string.IsNullOrWhiteSpace(payload.Message))
        {
            await WriteSseAsync(
                response,
                ChatEventEnvelope.Text(ChatEventType.Error, "Invalid chat payload. 'message' is required.")).ConfigureAwait(false);
            return response;
        }

        var normalizedPayload = payload with
        {
            SessionId = string.IsNullOrWhiteSpace(payload.SessionId)
                ? $"misa-{Guid.NewGuid():N}"
                : payload.SessionId
        };

        ChatFunctionsLog.ChatAccepted(_logger, normalizedPayload.SessionId);

        await foreach (var chatEvent in _pipeline.RunAsync(normalizedPayload, request.FunctionContext.CancellationToken))
        {
            await WriteSseAsync(response, chatEvent).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>
    /// Clears chat session context.
    /// </summary>
    [Function(nameof(ClearSession))]
    public async Task<HttpResponseData> ClearSession(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = ChatContracts.ChatSessionRoute)] HttpRequestData request,
        string sessionId)
    {
        var cleared = _sessionStore.Clear(sessionId);
        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");

        var payload = JsonSerializer.Serialize(new { session_id = sessionId, cleared }, JsonOptions);
        await response.WriteStringAsync(payload).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Lightweight health endpoint.
    /// </summary>
    [Function(nameof(Health))]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = ChatContracts.HealthRoute)] HttpRequestData request)
    {
        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync("{\"status\":\"ok\",\"service\":\"MISA.Functions\"}").ConfigureAwait(false);
        return response;
    }

    private static async Task WriteSseAsync(HttpResponseData response, ChatEventEnvelope chatEvent)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                type = chatEvent.WireType,
                content = chatEvent.Content
            },
            JsonOptions);

        await response.WriteStringAsync($"event: {chatEvent.WireType}\n").ConfigureAwait(false);
        await response.WriteStringAsync($"data: {payload}\n\n").ConfigureAwait(false);
    }

    private static partial class ChatFunctionsLog
    {
        [LoggerMessage(
            EventId = 4001,
            Level = LogLevel.Information,
            Message = "Accepted chat request. SessionId={SessionId}")]
        public static partial void ChatAccepted(ILogger logger, string sessionId);
    }
}