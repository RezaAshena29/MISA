using Akka.Actor;
using Akka.Configuration;
using MISA.Application;
using MISA.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MISA.Orchestration.Akka;

/// <summary>
/// Akka cluster runtime implementation used by the application pipeline.
/// </summary>
public sealed partial class AkkaClusterExecutionRuntime : IAgentExecutionRuntime, IAsyncDisposable
{
	private readonly ActorSystem _actorSystem;
	private readonly IActorRef _coordinator;
	private readonly ILogger<AkkaClusterExecutionRuntime> _logger;

	/// <summary>
	/// Creates and starts the cluster actor system.
	/// </summary>
	public AkkaClusterExecutionRuntime(
		IAgentRouter agentRouter,
		IKnowledgeService knowledgeService,
		IDecisioningService decisioningService,
		IChatSessionStore sessionStore,
		ILogger<AkkaClusterExecutionRuntime> logger)
	{
		_logger = logger;

		var config = ConfigurationFactory.ParseString(@"
akka {
  actor.provider = cluster
	log-dead-letters = off
	log-dead-letters-during-shutdown = off
  remote.dot-netty.tcp {
	hostname = ""127.0.0.1""
	port = 0
  }
	cluster.roles = [""misa-orchestrator""]
}");

		_actorSystem = ActorSystem.Create("misa-agentic-system", config);
		_coordinator = _actorSystem.ActorOf(
			Props.Create(() => new FanOutCoordinatorActor(agentRouter, knowledgeService, decisioningService, sessionStore)),
			"fanout-coordinator");

		AkkaRuntimeLog.RuntimeInitialized(_logger);
	}

	/// <inheritdoc />
	public async IAsyncEnumerable<ChatEventEnvelope> ExecuteAsync(
		ChatRequestDto request,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var result = await _coordinator
			.Ask<ChatExecutionResult>(new ExecuteChat(request), cancellationToken)
			.ConfigureAwait(false);

		foreach (var chatEvent in result.Events)
		{
			yield return chatEvent;
		}
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		AkkaRuntimeLog.RuntimeStopping(_logger);
		await _actorSystem.Terminate().ConfigureAwait(false);
		await _actorSystem.WhenTerminated.ConfigureAwait(false);
	}

	private static partial class AkkaRuntimeLog
	{
		[LoggerMessage(
			EventId = 3001,
			Level = LogLevel.Information,
			Message = "Akka cluster runtime initialized.")]
		public static partial void RuntimeInitialized(ILogger logger);

		[LoggerMessage(
			EventId = 3002,
			Level = LogLevel.Information,
			Message = "Stopping Akka cluster runtime.")]
		public static partial void RuntimeStopping(ILogger logger);
	}
}

/// <summary>
/// Registers Akka orchestration services.
/// </summary>
public static class OrchestrationAkkaServiceCollectionExtensions
{
	/// <summary>
	/// Adds Akka-based orchestration runtime.
	/// </summary>
	public static IServiceCollection AddMisaOrchestrationAkka(this IServiceCollection services)
	{
		services.AddSingleton<AkkaClusterExecutionRuntime>();
		services.AddSingleton<IAgentExecutionRuntime>(sp => sp.GetRequiredService<AkkaClusterExecutionRuntime>());
		return services;
	}
}

internal sealed record ExecuteChat(ChatRequestDto Request);

internal sealed record ChatExecutionResult(IReadOnlyList<ChatEventEnvelope> Events);

internal sealed class FanOutCoordinatorActor : ReceiveActor
{
	private static readonly Regex AgePattern = new(@"\bage\s*\d{1,2}\b|\b\d{1,2}\s*(?:yo|years?|yrs?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex BudgetPattern = new(@"\$\s*\d|\bbudget\b|\bpremium\b|\b\d+k\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private readonly IAgentRouter _agentRouter;
	private readonly IKnowledgeService _knowledgeService;
	private readonly IDecisioningService _decisioningService;
	private readonly IChatSessionStore _sessionStore;

	public FanOutCoordinatorActor(
		IAgentRouter agentRouter,
		IKnowledgeService knowledgeService,
		IDecisioningService decisioningService,
		IChatSessionStore sessionStore)
	{
		_agentRouter = agentRouter;
		_knowledgeService = knowledgeService;
		_decisioningService = decisioningService;
		_sessionStore = sessionStore;

		ReceiveAsync<ExecuteChat>(HandleAsync);
	}

	private async Task HandleAsync(ExecuteChat command)
	{
		var replyTo = Sender;
		var cancellationToken = CancellationToken.None;
		var request = command.Request;

		var routeTask = _agentRouter.ResolveRouteAsync(request, cancellationToken);
		var priorStateTask = _sessionStore.GetAsync(request.SessionId, cancellationToken);
		await Task.WhenAll(routeTask, priorStateTask).ConfigureAwait(false);

		var route = routeTask.Result;
		var priorState = priorStateTask.Result;

		if (string.Equals(route, "knowledge", StringComparison.OrdinalIgnoreCase))
		{
			var knowledge = await _knowledgeService.AnswerAsync(request, cancellationToken).ConfigureAwait(false);
			await _sessionStore
				.SaveAsync(new ChatSessionState(request.SessionId, LastRoute: "knowledge", LastRecommendation: priorState?.LastRecommendation), cancellationToken)
				.ConfigureAwait(false);

			replyTo.Tell(new ChatExecutionResult(
			[
				ChatEventEnvelope.Text(ChatEventType.Thinking, "Looking this up in the knowledge base..."),
				ChatEventEnvelope.Text(ChatEventType.Result, knowledge)
			]));
			return;
		}

		if (string.Equals(route, "reasoning", StringComparison.OrdinalIgnoreCase))
		{
			var reasoningContent = await BuildReasoningContentAsync(request, priorState, cancellationToken).ConfigureAwait(false);
			await _sessionStore
				.SaveAsync(new ChatSessionState(request.SessionId, LastRoute: "reasoning", LastRecommendation: priorState?.LastRecommendation), cancellationToken)
				.ConfigureAwait(false);

			replyTo.Tell(new ChatExecutionResult(
			[
				ChatEventEnvelope.Text(ChatEventType.Thinking, "Looking up why this recommendation was selected..."),
				ChatEventEnvelope.Text(ChatEventType.Result, reasoningContent)
			]));
			return;
		}

		if (string.Equals(route, "clarification", StringComparison.OrdinalIgnoreCase))
		{
			await _sessionStore
				.SaveAsync(new ChatSessionState(request.SessionId, LastRoute: "clarification", LastRecommendation: priorState?.LastRecommendation), cancellationToken)
				.ConfigureAwait(false);

			replyTo.Tell(new ChatExecutionResult(
			[
				ChatEventEnvelope.Text(
					ChatEventType.Clarification,
					"I need a bit more to run this case. Could you share age, gender, smoking status, and premium budget?")
			]));
			return;
		}

		var illustrationEvents = await BuildIllustrationEventsAsync(request, cancellationToken).ConfigureAwait(false);
		var lastRecommendation = illustrationEvents
			.LastOrDefault(evt => evt.Type == ChatEventType.Result)?.Content?.ToString();

		await _sessionStore
			.SaveAsync(new ChatSessionState(request.SessionId, LastRoute: "illustration", LastRecommendation: lastRecommendation), cancellationToken)
			.ConfigureAwait(false);

		replyTo.Tell(new ChatExecutionResult(illustrationEvents));
	}

	private async Task<IReadOnlyList<ChatEventEnvelope>> BuildIllustrationEventsAsync(
		ChatRequestDto request,
		CancellationToken cancellationToken)
	{
		var events = new List<ChatEventEnvelope>
		{
			ChatEventEnvelope.Text(
				ChatEventType.Thinking,
				"The user is asking me to help find the best policy for their client. Intention category: **Illustration**.")
		};

		if (!HasSufficientIllustrationInput(request.Message, out var clarificationPrompt))
		{
			events.Add(ChatEventEnvelope.Text(ChatEventType.Clarification, clarificationPrompt));
			return events;
		}

		var assumptions = BuildAssumptions(request);
		if (!string.IsNullOrWhiteSpace(assumptions))
		{
			events.Add(ChatEventEnvelope.Text(ChatEventType.Assumptions, assumptions));
		}

		events.Add(ChatEventEnvelope.Text(ChatEventType.Progress, "Determining illustration configurations to call: Illustration."));
		events.Add(ChatEventEnvelope.Text(ChatEventType.Progress, "Will compare 3 configurations: Pay 10, Pay 20, Pay 90."));
		events.Add(ChatEventEnvelope.Text(ChatEventType.Progress, "Sending 3 requests to calculation services..."));

		if (request.Message.Contains("do", StringComparison.OrdinalIgnoreCase))
		{
			events.Add(ChatEventEnvelope.Text(
				ChatEventType.Prevalidation,
				"Pre-validation warnings (kept, review before quoting):\n- **Pay 90 25% DO**: fallback may switch to No-DO when constraints are infeasible."));
		}

		// Fan-out: compute recommendation and supporting knowledge concurrently.
		var recommendationTask = _decisioningService.BuildRecommendationAsync(request, cancellationToken);
		var knowledgeTask = _knowledgeService.AnswerAsync(request, cancellationToken);
		await Task.WhenAll(recommendationTask, knowledgeTask).ConfigureAwait(false);

		events.Add(ChatEventEnvelope.Text(ChatEventType.Progress, "Received successful results. Comparing and ranking..."));

		events.Add(ChatEventEnvelope.Text(
			ChatEventType.Result,
			FormatIllustrationResultMarkdown(recommendationTask.Result, knowledgeTask.Result)));

		if (request.ContextConsent && request.UdmContext is not null)
		{
			events.Add(new ChatEventEnvelope(ChatEventType.Columns, BuildColumnsPayload(request)));
		}

		return events;
	}

	private async Task<string> BuildReasoningContentAsync(
		ChatRequestDto request,
		ChatSessionState? priorState,
		CancellationToken cancellationToken)
	{
		var knowledge = await _knowledgeService.AnswerAsync(request, cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(priorState?.LastRecommendation))
		{
			return "I do not have a prior recommendation in this session yet. " + knowledge;
		}

		return $"Previous recommendation:\n{priorState.LastRecommendation}\n\nSupporting explanation:\n{knowledge}";
	}

	private static bool HasSufficientIllustrationInput(string message, out string clarificationPrompt)
	{
		var hasAge = AgePattern.IsMatch(message);
		var hasGender = message.Contains("male", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("female", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("man", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("woman", StringComparison.OrdinalIgnoreCase);
		var hasSmoking = message.Contains("smoker", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("non-smoker", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("nonsmoker", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("non smoker", StringComparison.OrdinalIgnoreCase);
		var hasBudget = BudgetPattern.IsMatch(message);

		if (hasAge && hasGender && hasSmoking && hasBudget)
		{
			clarificationPrompt = string.Empty;
			return true;
		}

		clarificationPrompt = "I need a bit more to run this case. Could you share age, gender, smoking status, and premium budget?";
		return false;
	}

	private static string BuildAssumptions(ChatRequestDto request)
	{
		var assumptions = new List<string>();
		if (string.IsNullOrWhiteSpace(request.Product))
		{
			assumptions.Add("- **product**: Par — defaulted because no product was specified.");
		}
		if (string.IsNullOrWhiteSpace(request.Language))
		{
			assumptions.Add("- **language**: en — defaulted from request context.");
		}

		return string.Join("\n", assumptions);
	}

	private static string FormatIllustrationResultMarkdown(string recommendation, string knowledge)
	{
		return
			"Here are the recommended options for your client:\n\n" +
			"| Rank | Configuration | Recommendation |\n" +
			"|---|---|---|\n" +
			$"| 1 | Pay 90 | {EscapeForMarkdownTable(recommendation)} |\n" +
			"| 2 | Pay 20 | Balanced premium duration comparator |\n" +
			"| 3 | Pay 10 | Short premium duration comparator |\n\n" +
			$"Supporting note: {knowledge}";
	}

	private static string EscapeForMarkdownTable(string value)
	{
		return value.Replace("|", "\\|").Replace("\n", " ");
	}

	private static ColumnsEventPayload BuildColumnsPayload(ChatRequestDto request)
	{
		var operations = new List<UdmPatchOperationDto>
		{
			new("replace", "/illustration/scenario/selectedConfig", "Pay 90"),
			new("replace", "/illustration/scenario/premiumBudget", 100000)
		};

		var columns = new List<UdmPatchColumnDto>
		{
			new("Pay 90", operations)
		};

		return new ColumnsEventPayload(
			Version: "1.0",
			SessionId: request.SessionId,
			Columns: columns,
			IrtUdm: request.UdmContext.HasValue ? request.UdmContext.Value.Clone() : null,
			IrtCalcResponse: new { status = "stub", generated_by = "MISA_Agentic" },
			IrtMetrics: new { generated_at_utc = DateTimeOffset.UtcNow });
	}
}
