using System.Runtime.CompilerServices;
using MISA.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MISA.Application;

/// <summary>
/// Main chat orchestration pipeline abstraction.
/// </summary>
public interface IChatPipeline
{
	/// <summary>
	/// Executes the chat pipeline and streams SSE events.
	/// </summary>
	IAsyncEnumerable<ChatEventEnvelope> RunAsync(
		ChatRequestDto request,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// Akka-backed execution runtime abstraction.
/// </summary>
public interface IAgentExecutionRuntime
{
	/// <summary>
	/// Executes an end-to-end route and streams ordered SSE events.
	/// </summary>
	IAsyncEnumerable<ChatEventEnvelope> ExecuteAsync(
		ChatRequestDto request,
		CancellationToken cancellationToken);
}

/// <summary>
/// Session state persisted across multi-turn chat interactions.
/// </summary>
public sealed record ChatSessionState(
	string SessionId,
	string? LastRoute = null,
	string? LastRecommendation = null,
	DateTimeOffset UpdatedAt = default);

/// <summary>
/// Session store abstraction for contextual follow-up behavior.
/// </summary>
public interface IChatSessionStore
{
	/// <summary>
	/// Gets a session state by session identifier.
	/// </summary>
	Task<ChatSessionState?> GetAsync(string sessionId, CancellationToken cancellationToken);

	/// <summary>
	/// Saves a session state snapshot.
	/// </summary>
	Task SaveAsync(ChatSessionState state, CancellationToken cancellationToken);

	/// <summary>
	/// Clears a session and returns true when state existed.
	/// </summary>
	bool Clear(string sessionId);
}

/// <summary>
/// Inbound prompt guard abstraction.
/// </summary>
public interface IPromptGuard
{
	/// <summary>
	/// Returns whether a prompt is safe for downstream agent processing.
	/// </summary>
	bool IsSafe(string prompt, out string? violationReason);
}

/// <summary>
/// Outbound response guard abstraction.
/// </summary>
public interface IResponseGuard
{
	/// <summary>
	/// Sanitizes response text before SSE emission.
	/// </summary>
	string Sanitize(string text);
}

/// <summary>
/// Agent route resolution abstraction (MAF-aligned implementation in MISA.Agents).
/// </summary>
public interface IAgentRouter
{
	/// <summary>
	/// Resolves the target orchestration route for the request.
	/// </summary>
	Task<string> ResolveRouteAsync(ChatRequestDto request, CancellationToken cancellationToken);
}

/// <summary>
/// Knowledge answer abstraction.
/// </summary>
public interface IKnowledgeService
{
	/// <summary>
	/// Builds a grounded knowledge response.
	/// </summary>
	Task<string> AnswerAsync(ChatRequestDto request, CancellationToken cancellationToken);
}

/// <summary>
/// Decisioning and ranking abstraction.
/// </summary>
public interface IDecisioningService
{
	/// <summary>
	/// Builds recommendation content for the active scenario.
	/// </summary>
	Task<string> BuildRecommendationAsync(ChatRequestDto request, CancellationToken cancellationToken);

	/// <summary>
	/// Builds a structured recommendation table used for rich chat rendering.
	/// </summary>
	Task<RecommendationTable> BuildRecommendationTableAsync(ChatRequestDto request, CancellationToken cancellationToken);
}

/// <summary>
/// Reasoning response abstraction.
/// </summary>
public interface IReasoningService
{
	/// <summary>
	/// Builds reasoning output, optionally grounded in prior session recommendation context.
	/// </summary>
	Task<string> BuildReasoningAsync(ChatRequestDto request, ChatSessionState? priorState, CancellationToken cancellationToken);
}

/// <summary>
/// Clarification prompt abstraction.
/// </summary>
public interface IClarificationService
{
	/// <summary>
	/// Builds a clarification prompt for missing or ambiguous user inputs.
	/// </summary>
	Task<string> BuildClarificationPromptAsync(ChatRequestDto request, ChatSessionState? priorState, CancellationToken cancellationToken);
}

/// <summary>
/// Scenario constants for recommendation outputs.
/// </summary>
public static class RecommendationScenarios
{
	public const string MaximizeIrrAtLe = "maximize_irr_at_le";
	public const string MaximizeEarlyCsv = "maximize_early_cash_surrender_value";
	public const string MaximizeDeathBenefit = "maximize_death_benefit";
}

/// <summary>
/// Structured recommendation table for rich output rendering.
/// </summary>
public sealed record RecommendationTable(
	string ScenarioDescription,
	string ScenarioType,
	string ClientSummary,
	decimal PremiumBudget,
	IReadOnlyList<RecommendationColumn> Columns);

/// <summary>
/// One recommendation column with metrics used by markdown and columns payloads.
/// </summary>
public sealed record RecommendationColumn(
	string Id,
	string Label,
	decimal BaseCoverageAmount,
	decimal BaseAnnualPremium,
	decimal DepositOptionPayment,
	decimal TotalAnnualOutlay,
	decimal CashValueYear10,
	decimal CashValueYear5,
	decimal CashValueYear20,
	decimal CvEfficiencyYear10,
	decimal IrrOnCsvYear10,
	decimal DeathBenefitAtLeCurrent,
	decimal IrrAtLeCurrent,
	decimal DeathBenefitAtLeMinus2,
	decimal IrrAtLeMinus2,
	int QuickPayCurrent,
	int QuickPayMinus2,
	bool Recommended,
	int? ExtendedPaymentsForStress,
	string? StressPaymentExtensionNote,
	int LifeExpectancyAgeUsed,
	string Explain,
	IReadOnlyList<string> Warnings);

/// <summary>
/// Pipeline implementation coordinating guards and Akka runtime execution.
/// </summary>
public sealed partial class ChatPipeline : IChatPipeline
{
	private readonly IPromptGuard _promptGuard;
	private readonly IResponseGuard _responseGuard;
	private readonly IAgentExecutionRuntime _executionRuntime;
	private readonly ILogger<ChatPipeline> _logger;

	/// <summary>
	/// Creates a new chat pipeline.
	/// </summary>
	public ChatPipeline(
		IPromptGuard promptGuard,
		IResponseGuard responseGuard,
		IAgentExecutionRuntime executionRuntime,
		ILogger<ChatPipeline> logger)
	{
		_promptGuard = promptGuard;
		_responseGuard = responseGuard;
		_executionRuntime = executionRuntime;
		_logger = logger;
	}

	/// <inheritdoc />
	public async IAsyncEnumerable<ChatEventEnvelope> RunAsync(
		ChatRequestDto request,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		yield return ChatEventEnvelope.Text(
			ChatEventType.Thinking,
			"Interpreting your question and identifying the client's needs...");

		if (!_promptGuard.IsSafe(request.Message, out var reason))
		{
			ChatPipelineLog.PromptBlocked(_logger, request.SessionId, reason);
			yield return ChatEventEnvelope.Text(ChatEventType.Error, $"Request blocked by guard policy: {reason}");
			yield break;
		}

		ChatEventEnvelope? failureEvent = null;
		await using var runtimeEnumerator = _executionRuntime
			.ExecuteAsync(request, cancellationToken)
			.GetAsyncEnumerator(cancellationToken);

		while (true)
		{
			ChatEventEnvelope runtimeEvent;
			try
			{
				if (!await runtimeEnumerator.MoveNextAsync().ConfigureAwait(false))
				{
					break;
				}

				runtimeEvent = runtimeEnumerator.Current;
			}
			catch (OperationCanceledException)
			{
				ChatPipelineLog.ExecutionCanceled(_logger, request.SessionId);
				failureEvent = ChatEventEnvelope.Text(ChatEventType.Error, "Request canceled by caller.");
				break;
			}
			catch (Exception ex)
			{
				ChatPipelineLog.ExecutionFailed(_logger, request.SessionId, ex);
				failureEvent = ChatEventEnvelope.Text(ChatEventType.Error, "The pipeline failed to generate a recommendation.");
				break;
			}

			if (runtimeEvent.Content is string text)
			{
				yield return runtimeEvent with { Content = _responseGuard.Sanitize(text) };
				continue;
			}

			yield return runtimeEvent;
		}

		if (failureEvent is not null)
		{
			yield return failureEvent;
			yield break;
		}
	}

	private static partial class ChatPipelineLog
	{
		[LoggerMessage(
			EventId = 1001,
			Level = LogLevel.Warning,
			Message = "Prompt blocked by inbound guard. SessionId={SessionId} Reason={Reason}")]
		public static partial void PromptBlocked(ILogger logger, string sessionId, string? reason);

		[LoggerMessage(
			EventId = 1002,
			Level = LogLevel.Warning,
			Message = "Pipeline execution canceled. SessionId={SessionId}")]
		public static partial void ExecutionCanceled(ILogger logger, string sessionId);

		[LoggerMessage(
			EventId = 1003,
			Level = LogLevel.Error,
			Message = "Pipeline execution failed. SessionId={SessionId}")]
		public static partial void ExecutionFailed(ILogger logger, string sessionId, Exception exception);
	}
}

/// <summary>
/// Registers application layer services.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
	/// <summary>
	/// Adds MISA application layer services.
	/// </summary>
	public static IServiceCollection AddMisaApplication(this IServiceCollection services)
	{
		services.AddSingleton<IMcpToolBroker, NullMcpToolBroker>();
		services.AddScoped<IChatPipeline, ChatPipeline>();
		return services;
	}
}

/// <summary>
/// Default MCP broker used when MCP integration is disabled.
/// </summary>
public sealed class NullMcpToolBroker : IMcpToolBroker
{
	/// <inheritdoc />
	public Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
	{
		return Task.FromResult(
			McpToolCallResult.Failed(
				errorCode: "mcp_disabled",
				errorMessage: "MCP broker is not enabled for this environment.",
				latency: TimeSpan.Zero));
	}
}
