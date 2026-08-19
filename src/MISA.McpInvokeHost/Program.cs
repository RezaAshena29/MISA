using System.Text.Json;
using MISA.Contracts;
using MISA.Decisioning;
using MISA.Knowledge;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var knowledgeService = new KnowledgeService();
var decisioningService = new RuleBasedDecisioningService();
const string clarificationPrompt = "I need a bit more to run this case. Could you share age, gender, smoking status, and premium budget?";

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

	if (IsToolName(request.ToolName, "knowledge.mcp", "knowledge.answer"))
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

	if (IsToolName(request.ToolName, "decisioning.mcp", "decisioning.recommendation.table"))
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

	if (IsToolName(request.ToolName, "reasoning.mcp", "reasoning.explain"))
	{
		var lastRecommendation = GetAttributeValue(request.Attributes, "lastRecommendation");
		var knowledge = await knowledgeService.AnswerAsync(chatRequest, cancellationToken).ConfigureAwait(false);
		var content = string.IsNullOrWhiteSpace(lastRecommendation)
			? "I do not have a prior recommendation in this session yet. " + knowledge
			: $"Previous recommendation:\n{lastRecommendation}\n\nSupporting explanation:\n{knowledge}";

		return Results.Json(new
		{
			success = true,
			content,
			errorCode = (string?)null,
			errorMessage = (string?)null
		});
	}

	if (IsToolName(request.ToolName, "clarification.mcp", "clarification.ask"))
	{
		return Results.Json(new
		{
			success = true,
			content = clarificationPrompt,
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

static bool IsToolName(string toolName, params string[] candidates)
{
	return candidates.Any(candidate => string.Equals(toolName, candidate, StringComparison.OrdinalIgnoreCase));
}

public sealed record McpInvokeRequest(
	string Route,
	string ToolName,
	string SessionId,
	string Input,
	JsonElement? Attributes,
	string? CorrelationId);
