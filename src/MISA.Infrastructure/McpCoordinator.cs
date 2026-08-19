using System.Diagnostics;
using MISA.Application;
using MISA.Contracts;
using Microsoft.Extensions.Options;

namespace MISA.Infrastructure;

/// <summary>
/// MCP coordinator settings loaded from configuration.
/// </summary>
public sealed class McpCoordinatorOptions
{
	/// <summary>
	/// Route-to-tool mapping used by the coordinator.
	/// </summary>
	public Dictionary<string, string> ToolByRoute { get; set; } = new(StringComparer.OrdinalIgnoreCase)
	{
		["knowledge"] = "knowledge.mcp",
		["illustration"] = "decisioning.mcp",
		["reasoning"] = "reasoning.mcp",
		["clarification"] = "clarification.mcp"
	};
}

/// <summary>
/// Route-aware MCP coordinator that centralizes tool selection and broker invocation.
/// </summary>
public sealed class McpCoordinator : IMcpCoordinator
{
	private readonly IMcpToolBroker _broker;
	private readonly IOptions<McpCoordinatorOptions> _options;

	/// <summary>
	/// Creates a coordinator instance.
	/// </summary>
	public McpCoordinator(IMcpToolBroker broker, IOptions<McpCoordinatorOptions> options)
	{
		_broker = broker;
		_options = options;
	}

	/// <inheritdoc />
	public Task<McpToolCallResult> InvokeForRouteAsync(
		string route,
		ChatRequestDto request,
		ChatSessionState? priorState,
		string? input,
		IReadOnlyDictionary<string, string?>? additionalAttributes,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(route))
		{
			return Task.FromResult(
				McpToolCallResult.Failed(
					errorCode: "invalid_route",
					errorMessage: "Route is required for MCP coordination.",
					latency: TimeSpan.Zero));
		}

		if (!_options.Value.ToolByRoute.TryGetValue(route, out var toolName)
			|| string.IsNullOrWhiteSpace(toolName))
		{
			return Task.FromResult(
				McpToolCallResult.Failed(
					errorCode: "tool_not_mapped",
					errorMessage: $"No MCP tool is mapped for route '{route}'.",
					latency: TimeSpan.Zero));
		}

		var attributes = BuildAttributes(request, priorState, additionalAttributes);
		var effectiveInput = string.IsNullOrWhiteSpace(input)
			? request.Message
			: input;
		var correlationId = Activity.Current?.TraceId.ToString();

		return _broker.InvokeAsync(
			new McpToolCallRequest(
				Route: route,
				ToolName: toolName,
				SessionId: request.SessionId,
				Input: effectiveInput,
				Attributes: attributes,
				CorrelationId: correlationId),
			cancellationToken);
	}

	private static IReadOnlyDictionary<string, string?> BuildAttributes(
		ChatRequestDto request,
		ChatSessionState? priorState,
		IReadOnlyDictionary<string, string?>? additionalAttributes)
	{
		var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
		{
			["sessionId"] = request.SessionId,
			["product"] = request.Product,
			["language"] = request.Language,
			["lastRoute"] = priorState?.LastRoute,
			["lastRecommendation"] = priorState?.LastRecommendation,
			["contextConsent"] = request.ContextConsent ? "true" : "false"
		};

		if (additionalAttributes is null)
		{
			return attributes;
		}

		foreach (var entry in additionalAttributes)
		{
			attributes[entry.Key] = entry.Value;
		}

		return attributes;
	}
}
