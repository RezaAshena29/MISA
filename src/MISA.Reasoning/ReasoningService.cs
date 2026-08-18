using System.Diagnostics;
using System.Diagnostics.Metrics;
using MISA.Application;
using MISA.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MISA.Reasoning;

/// <summary>
/// Deterministic reasoning service for follow-up explanation responses.
/// </summary>
public sealed class ReasoningService : IReasoningService
{
	private static readonly ActivitySource ActivitySource = new("MISA.Reasoning");
	private static readonly Meter Meter = new("MISA.Reasoning");
	private static readonly Counter<long> ReasoningCounter = Meter.CreateCounter<long>("misa.reasoning.lookups");

	private readonly IKnowledgeService _knowledgeService;

	/// <summary>
	/// Creates deterministic reasoning service.
	/// </summary>
	public ReasoningService(IKnowledgeService knowledgeService)
	{
		_knowledgeService = knowledgeService;
	}

	/// <inheritdoc />
	public async Task<string> BuildReasoningAsync(ChatRequestDto request, ChatSessionState? priorState, CancellationToken cancellationToken)
	{
		using var activity = ActivitySource.StartActivity("reasoning.build", ActivityKind.Internal);
		activity?.SetTag("session.id", request.SessionId);

		var knowledge = await _knowledgeService.AnswerAsync(request, cancellationToken).ConfigureAwait(false);
		ReasoningCounter.Add(1);

		if (string.IsNullOrWhiteSpace(priorState?.LastRecommendation))
		{
			return "I do not have a prior recommendation in this session yet. " + knowledge;
		}

		return $"Previous recommendation:\n{priorState.LastRecommendation}\n\nSupporting explanation:\n{knowledge}";
	}
}

/// <summary>
/// Reasoning module MCP feature flags.
/// </summary>
public sealed class ReasoningMcpOptions
{
	/// <summary>
	/// Enables MCP for reasoning responses.
	/// </summary>
	public bool Enabled { get; set; }

	/// <summary>
	/// MCP tool name used for reasoning responses.
	/// </summary>
	public string ToolName { get; set; } = "reasoning.mcp";
}

/// <summary>
/// Decorates reasoning service with optional MCP-based response path.
/// </summary>
public sealed class McpReasoningServiceDecorator : IReasoningService
{
	private readonly ReasoningService _inner;
	private readonly IMcpToolBroker _mcpToolBroker;
	private readonly IOptions<ReasoningMcpOptions> _options;

	/// <summary>
	/// Creates MCP-enabled reasoning service decorator.
	/// </summary>
	public McpReasoningServiceDecorator(
		ReasoningService inner,
		IMcpToolBroker mcpToolBroker,
		IOptions<ReasoningMcpOptions> options)
	{
		_inner = inner;
		_mcpToolBroker = mcpToolBroker;
		_options = options;
	}

	/// <inheritdoc />
	public async Task<string> BuildReasoningAsync(ChatRequestDto request, ChatSessionState? priorState, CancellationToken cancellationToken)
	{
		if (!_options.Value.Enabled)
		{
			return await _inner.BuildReasoningAsync(request, priorState, cancellationToken).ConfigureAwait(false);
		}

		var result = await _mcpToolBroker
			.InvokeAsync(
				new McpToolCallRequest(
					Route: "reasoning",
					ToolName: _options.Value.ToolName,
					SessionId: request.SessionId,
					Input: request.Message,
					Attributes: new Dictionary<string, string?>
					{
						["product"] = request.Product,
						["language"] = request.Language,
						["priorRoute"] = priorState?.LastRoute,
						["hasPriorRecommendation"] = (!string.IsNullOrWhiteSpace(priorState?.LastRecommendation)).ToString()
					}),
				cancellationToken)
			.ConfigureAwait(false);

		if (result.Success && !string.IsNullOrWhiteSpace(result.Content))
		{
			return result.Content;
		}

		return await _inner.BuildReasoningAsync(request, priorState, cancellationToken).ConfigureAwait(false);
	}
}

/// <summary>
/// Registers reasoning services.
/// </summary>
public static class ReasoningServiceCollectionExtensions
{
	/// <summary>
	/// Adds reasoning module services.
	/// </summary>
	public static IServiceCollection AddMisaReasoning(this IServiceCollection services)
	{
		services.AddOptions<ReasoningMcpOptions>()
			.BindConfiguration("Misa:Mcp:Reasoning");
		services.AddSingleton<ReasoningService>();
		services.AddSingleton<IReasoningService, McpReasoningServiceDecorator>();
		return services;
	}
}
