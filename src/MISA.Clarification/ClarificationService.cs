using System.Diagnostics;
using System.Diagnostics.Metrics;
using MISA.Application;
using MISA.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MISA.Clarification;

/// <summary>
/// Deterministic clarification service for follow-up prompts.
/// </summary>
public sealed class ClarificationService : IClarificationService
{
	/// <summary>
	/// Default clarification prompt used when no MCP response is available.
	/// </summary>
	public const string DefaultPrompt = "I need a bit more to run this case. Could you share age, gender, smoking status, and premium budget?";

	private static readonly ActivitySource ActivitySource = new("MISA.Clarification");
	private static readonly Meter Meter = new("MISA.Clarification");
	private static readonly Counter<long> ClarificationCounter = Meter.CreateCounter<long>("misa.clarification.lookups");

	/// <inheritdoc />
	public Task<string> BuildClarificationPromptAsync(ChatRequestDto request, ChatSessionState? priorState, CancellationToken cancellationToken)
	{
		using var activity = ActivitySource.StartActivity("clarification.build", ActivityKind.Internal);
		activity?.SetTag("session.id", request.SessionId);
		activity?.SetTag("prior.route", priorState?.LastRoute ?? string.Empty);
		ClarificationCounter.Add(1);
		return Task.FromResult(DefaultPrompt);
	}
}

/// <summary>
/// Clarification module MCP feature flags.
/// </summary>
public sealed class ClarificationMcpOptions
{
	/// <summary>
	/// Enables MCP for clarification prompts.
	/// </summary>
	public bool Enabled { get; set; }

	/// <summary>
	/// MCP tool name used for clarification prompts.
	/// </summary>
	public string ToolName { get; set; } = "clarification.mcp";
}

/// <summary>
/// Decorates clarification service with optional MCP-based response path.
/// </summary>
public sealed class McpClarificationServiceDecorator : IClarificationService
{
	private readonly ClarificationService _inner;
	private readonly IMcpToolBroker _mcpToolBroker;
	private readonly IOptions<ClarificationMcpOptions> _options;

	/// <summary>
	/// Creates MCP-enabled clarification service decorator.
	/// </summary>
	public McpClarificationServiceDecorator(
		ClarificationService inner,
		IMcpToolBroker mcpToolBroker,
		IOptions<ClarificationMcpOptions> options)
	{
		_inner = inner;
		_mcpToolBroker = mcpToolBroker;
		_options = options;
	}

	/// <inheritdoc />
	public async Task<string> BuildClarificationPromptAsync(ChatRequestDto request, ChatSessionState? priorState, CancellationToken cancellationToken)
	{
		if (!_options.Value.Enabled)
		{
			return await _inner.BuildClarificationPromptAsync(request, priorState, cancellationToken).ConfigureAwait(false);
		}

		var result = await _mcpToolBroker
			.InvokeAsync(
				new McpToolCallRequest(
					Route: "clarification",
					ToolName: _options.Value.ToolName,
					SessionId: request.SessionId,
					Input: request.Message,
					Attributes: new Dictionary<string, string?>
					{
						["product"] = request.Product,
						["language"] = request.Language,
						["priorRoute"] = priorState?.LastRoute
					}),
				cancellationToken)
			.ConfigureAwait(false);

		if (result.Success && !string.IsNullOrWhiteSpace(result.Content))
		{
			return result.Content;
		}

		return await _inner.BuildClarificationPromptAsync(request, priorState, cancellationToken).ConfigureAwait(false);
	}
}

/// <summary>
/// Registers clarification services.
/// </summary>
public static class ClarificationServiceCollectionExtensions
{
	/// <summary>
	/// Adds clarification module services.
	/// </summary>
	public static IServiceCollection AddMisaClarification(this IServiceCollection services)
	{
		services.AddOptions<ClarificationMcpOptions>()
			.BindConfiguration("Misa:Mcp:Clarification");
		services.AddSingleton<ClarificationService>();
		services.AddSingleton<IClarificationService, McpClarificationServiceDecorator>();
		return services;
	}
}
