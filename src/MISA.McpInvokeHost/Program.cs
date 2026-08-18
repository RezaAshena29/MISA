using System.Text.Json;
using MISA.Application;
using MISA.Clarification;
using MISA.Contracts;
using MISA.Decisioning;
using MISA.Knowledge;
using MISA.Reasoning;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var knowledgeService = new KnowledgeService();
var decisioningService = new RuleBasedDecisioningService();
var reasoningService = new ReasoningService(knowledgeService);
var clarificationService = new ClarificationService();

app.MapGet("/mcp/health", () =>
{
	return Results.Json(new
	{
		status = "ok",
		service = "MISA.McpInvokeHost"
	});
});

app.MapPost("/mcp/tools/invoke", async (McpInvokeRequest request, CancellationToken cancellationToken) =>
{
	var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
		? "inspector-session"
		: request.SessionId;

	var input = request.Input ?? string.Empty;
	var product = GetAttributeValue(request.Attributes, "product");
	var language = GetAttributeValue(request.Attributes, "language");

	app.Logger.LogInformation(
		"MCP invoke request received. Route={Route} Tool={Tool} SessionId={SessionId} CorrelationId={CorrelationId}",
		request.Route,
		request.ToolName,
		sessionId,
		request.CorrelationId);

	var chatRequest = new ChatRequestDto(
		Message: input,
		SessionId: sessionId,
		Product: product,
		Language: language);

	if (string.Equals(request.ToolName, "knowledge.mcp", StringComparison.OrdinalIgnoreCase))
	{
		var answer = await knowledgeService.AnswerAsync(chatRequest, cancellationToken).ConfigureAwait(false);
		return Results.Json(new
		{
			success = true,
			content = answer,
			errorCode = (string?)null,
			errorMessage = (string?)null
		});
	}

	if (string.Equals(request.ToolName, "decisioning.mcp", StringComparison.OrdinalIgnoreCase))
	{
		var table = await decisioningService.BuildRecommendationTableAsync(chatRequest, cancellationToken).ConfigureAwait(false);
		return Results.Json(new
		{
			success = true,
			content = JsonSerializer.Serialize(table),
			errorCode = (string?)null,
			errorMessage = (string?)null
		});
	}

	if (string.Equals(request.ToolName, "reasoning.mcp", StringComparison.OrdinalIgnoreCase))
	{
		var priorState = new ChatSessionState(sessionId);
		var reasoning = await reasoningService.BuildReasoningAsync(chatRequest, priorState, cancellationToken).ConfigureAwait(false);
		return Results.Json(new
		{
			success = true,
			content = reasoning,
			errorCode = (string?)null,
			errorMessage = (string?)null
		});
	}

	if (string.Equals(request.ToolName, "clarification.mcp", StringComparison.OrdinalIgnoreCase))
	{
		var priorState = new ChatSessionState(sessionId);
		var clarification = await clarificationService.BuildClarificationPromptAsync(chatRequest, priorState, cancellationToken).ConfigureAwait(false);
		return Results.Json(new
		{
			success = true,
			content = clarification,
			errorCode = (string?)null,
			errorMessage = (string?)null
		});
	}

	return Results.Json(new
	{
		success = false,
		content = (string?)null,
		errorCode = "unsupported_tool",
		errorMessage = $"Unsupported tool: {request.ToolName}"
	});
});

var listenUrl = Environment.GetEnvironmentVariable("MCP_LISTEN_URL") ?? "http://127.0.0.1:19082";
app.Logger.LogInformation("Starting MCP invoke host on {ListenUrl}", listenUrl);
app.Run(listenUrl);

static string? GetAttributeValue(JsonElement? attributes, string key)
{
	if (attributes is null)
	{
		return null;
	}

	var root = attributes.Value;
	if (root.ValueKind == JsonValueKind.String)
	{
		var raw = root.GetString();
		if (!string.IsNullOrWhiteSpace(raw))
		{
			try
			{
				using var parsed = JsonDocument.Parse(raw);
				root = parsed.RootElement.Clone();
			}
			catch (JsonException)
			{
				return null;
			}
		}
	}

	if (root.ValueKind != JsonValueKind.Object)
	{
		return null;
	}

	if (!root.TryGetProperty(key, out var value))
	{
		return null;
	}

	return value.ValueKind == JsonValueKind.String
		? value.GetString()
		: null;
}

public sealed record McpInvokeRequest(
	string Route,
	string ToolName,
	string SessionId,
	string Input,
	JsonElement? Attributes,
	string? CorrelationId);
