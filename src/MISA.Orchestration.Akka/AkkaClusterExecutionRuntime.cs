using Akka.Actor;
using Akka.Configuration;
using MISA.Application;
using MISA.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Linq;
using System.Text.Json;
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
			Props.Create(() => new OrchestratorAgentActor(agentRouter, knowledgeService, decisioningService, sessionStore)),
			"orchestrator-agent");

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

internal sealed class OrchestratorAgentActor : ReceiveActor
{
	private const string IllustrationRoute = "illustration";
	private const string ClarificationRoute = "clarification";
	private const string KnowledgeRoute = "knowledge";
	private const string ReasoningRoute = "reasoning";

	private static readonly Regex AgePattern = new(@"\bage\s*\d{1,2}\b|\b\d{1,2}\s*(?:yo|years?|yrs?)\b|\b(?:client|insured|applicant)\s*(?:is|:)\s*\d{1,2}\b|\b\d{1,2}\s*,\s*(?:male|female|man|woman)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex BudgetPattern = new(@"\$\s*\d|\bbudget\b|\bpremium\b|\b\d+k\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly HashSet<string> BudgetFieldNames =
	[
		"premiumbudget",
		"premium_budget",
		"premiumamount",
		"premium_amount",
		"annualpremium",
		"modalpremium",
		"depositamount",
		"deposit_amount",
		"coverageamount",
		"coverage_amount"
	];

	private readonly IAgentRouter _agentRouter;
	private readonly IKnowledgeService _knowledgeService;
	private readonly IDecisioningService _decisioningService;
	private readonly IChatSessionStore _sessionStore;

	public OrchestratorAgentActor(
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

	private readonly record struct RouteResolution(string Route, ChatSessionState? PriorState);

	private readonly record struct FanoutResult(RecommendationTable Recommendation, string Knowledge);

	private async Task HandleAsync(ExecuteChat command)
	{
		var replyTo = Sender;
		var cancellationToken = CancellationToken.None;
		var request = command.Request;
		var routeResolution = await RunIntentAnalyzerAndContextMemoryAgentsAsync(request, cancellationToken).ConfigureAwait(false);

		if (string.Equals(routeResolution.Route, KnowledgeRoute, StringComparison.OrdinalIgnoreCase))
		{
			replyTo.Tell(new ChatExecutionResult(
				await RunKnowledgeRouteAsync(request, routeResolution.PriorState, cancellationToken).ConfigureAwait(false)));
			return;
		}

		if (string.Equals(routeResolution.Route, ReasoningRoute, StringComparison.OrdinalIgnoreCase))
		{
			replyTo.Tell(new ChatExecutionResult(
				await RunReasoningRouteAsync(request, routeResolution.PriorState, cancellationToken).ConfigureAwait(false)));
			return;
		}

		if (string.Equals(routeResolution.Route, ClarificationRoute, StringComparison.OrdinalIgnoreCase))
		{
			replyTo.Tell(new ChatExecutionResult(
				await RunClarifierRouteAsync(request, routeResolution.PriorState, cancellationToken).ConfigureAwait(false)));
			return;
		}

		replyTo.Tell(new ChatExecutionResult(
			await RunIllustrationRouteAsync(request, cancellationToken).ConfigureAwait(false)));
	}

	private async Task<RouteResolution> RunIntentAnalyzerAndContextMemoryAgentsAsync(
		ChatRequestDto request,
		CancellationToken cancellationToken)
	{
		var routeTask = _agentRouter.ResolveRouteAsync(request, cancellationToken);
		var priorStateTask = _sessionStore.GetAsync(request.SessionId, cancellationToken);
		await Task.WhenAll(routeTask, priorStateTask).ConfigureAwait(false);

		return new RouteResolution(routeTask.Result, priorStateTask.Result);
	}

	private async Task<IReadOnlyList<ChatEventEnvelope>> RunKnowledgeRouteAsync(
		ChatRequestDto request,
		ChatSessionState? priorState,
		CancellationToken cancellationToken)
	{
		var knowledge = await _knowledgeService.AnswerAsync(request, cancellationToken).ConfigureAwait(false);
		await _sessionStore
			.SaveAsync(new ChatSessionState(request.SessionId, LastRoute: KnowledgeRoute, LastRecommendation: priorState?.LastRecommendation), cancellationToken)
			.ConfigureAwait(false);

		return
		[
			ChatEventEnvelope.Text(ChatEventType.Thinking, "Looking this up in the knowledge base..."),
			ChatEventEnvelope.Text(ChatEventType.Result, knowledge)
		];
	}

	private async Task<IReadOnlyList<ChatEventEnvelope>> RunReasoningRouteAsync(
		ChatRequestDto request,
		ChatSessionState? priorState,
		CancellationToken cancellationToken)
	{
		var reasoningContent = await BuildReasoningContentAsync(request, priorState, cancellationToken).ConfigureAwait(false);
		await _sessionStore
			.SaveAsync(new ChatSessionState(request.SessionId, LastRoute: ReasoningRoute, LastRecommendation: priorState?.LastRecommendation), cancellationToken)
			.ConfigureAwait(false);

		return
		[
			ChatEventEnvelope.Text(ChatEventType.Thinking, "Looking up why this recommendation was selected..."),
			ChatEventEnvelope.Text(ChatEventType.Result, reasoningContent)
		];
	}

	private async Task<IReadOnlyList<ChatEventEnvelope>> RunClarifierRouteAsync(
		ChatRequestDto request,
		ChatSessionState? priorState,
		CancellationToken cancellationToken)
	{
		await _sessionStore
			.SaveAsync(new ChatSessionState(request.SessionId, LastRoute: ClarificationRoute, LastRecommendation: priorState?.LastRecommendation), cancellationToken)
			.ConfigureAwait(false);

		return
		[
			ChatEventEnvelope.Text(
				ChatEventType.Clarification,
				"I need a bit more to run this case. Could you share age, gender, smoking status, and premium budget?")
		];
	}

	private async Task<IReadOnlyList<ChatEventEnvelope>> RunIllustrationRouteAsync(
		ChatRequestDto request,
		CancellationToken cancellationToken)
	{
		var illustrationEvents = await BuildIllustrationEventsAsync(request, cancellationToken).ConfigureAwait(false);
		var lastRecommendation = illustrationEvents
			.LastOrDefault(evt => evt.Type == ChatEventType.Result)?.Content?.ToString();

		await _sessionStore
			.SaveAsync(new ChatSessionState(request.SessionId, LastRoute: IllustrationRoute, LastRecommendation: lastRecommendation), cancellationToken)
			.ConfigureAwait(false);

		return illustrationEvents;
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

		if (!RunValidationGuardAgent(request, out var clarificationPrompt))
		{
			events.Add(ChatEventEnvelope.Text(ChatEventType.Clarification, clarificationPrompt));
			return events;
		}

		var assumptions = RunIllustrationPlannerAgent(request);
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

		var fanoutResult = await RunFanoutDispatcherAgentAsync(request, cancellationToken).ConfigureAwait(false);
		var faninResult = RunFaninAggregatorAgent(fanoutResult);
		var rankedRecommendation = RunDecisionRankerAgent(faninResult.Recommendation);

		events.Add(ChatEventEnvelope.Text(ChatEventType.Progress, "Received successful results. Comparing and ranking..."));

		events.Add(RunResponseComposerAgent(rankedRecommendation, faninResult.Knowledge));

		if (request.ContextConsent && request.UdmContext is not null)
		{
			events.Add(new ChatEventEnvelope(ChatEventType.Columns, BuildColumnsPayload(request, rankedRecommendation)));
		}

		return events;
	}

	private static bool RunValidationGuardAgent(ChatRequestDto request, out string clarificationPrompt)
		=> HasSufficientIllustrationInput(request, out clarificationPrompt);

	private static string RunIllustrationPlannerAgent(ChatRequestDto request)
		=> BuildAssumptions(request);

	private async Task<FanoutResult> RunFanoutDispatcherAgentAsync(
		ChatRequestDto request,
		CancellationToken cancellationToken)
	{
		var recommendationTask = _decisioningService.BuildRecommendationTableAsync(request, cancellationToken);
		var knowledgeTask = _knowledgeService.AnswerAsync(request, cancellationToken);
		await Task.WhenAll(recommendationTask, knowledgeTask).ConfigureAwait(false);

		return new FanoutResult(recommendationTask.Result, knowledgeTask.Result);
	}

	private static FanoutResult RunFaninAggregatorAgent(FanoutResult fanoutResult)
		=> fanoutResult;

	private static RecommendationTable RunDecisionRankerAgent(RecommendationTable recommendation)
		=> recommendation;

	private static ChatEventEnvelope RunResponseComposerAgent(RecommendationTable recommendation, string knowledge)
		=> ChatEventEnvelope.Text(ChatEventType.Result, FormatIllustrationResultMarkdown(recommendation, knowledge));

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

	private static bool HasSufficientIllustrationInput(ChatRequestDto request, out string clarificationPrompt)
	{
		var message = request.Message;
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

		if (request.UdmContext is { } udmContext)
		{
			var contextSignals = ExtractContextSignals(udmContext);
			hasAge = hasAge || contextSignals.HasAge;
			hasGender = hasGender || contextSignals.HasGender;
			hasSmoking = hasSmoking || contextSignals.HasSmoking;
			hasBudget = hasBudget || contextSignals.HasBudget;
		}

		if (hasAge && hasGender && hasSmoking && hasBudget)
		{
			clarificationPrompt = string.Empty;
			return true;
		}

		clarificationPrompt = "I need a bit more to run this case. Could you share age, gender, smoking status, and premium budget?";
		return false;
	}

	private static ContextSignals ExtractContextSignals(JsonElement udmContext)
	{
		var signals = new ContextSignals(false, false, false, false);

		if (TryGetPropertyIgnoreCase(udmContext, "udm", out var fullUdm))
		{
			CollectContextSignals(fullUdm, ref signals);
		}

		CollectContextSignals(udmContext, ref signals);
		return signals;
	}

	private static void CollectContextSignals(JsonElement element, ref ContextSignals signals)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (var property in element.EnumerateObject())
				{
					UpdateSignalsFromProperty(property.Name, property.Value, ref signals);
					CollectContextSignals(property.Value, ref signals);

					if (signals.HasAll)
					{
						return;
					}
				}
				break;

			case JsonValueKind.Array:
				foreach (var item in element.EnumerateArray())
				{
					CollectContextSignals(item, ref signals);

					if (signals.HasAll)
					{
						return;
					}
				}
				break;
		}
	}

	private static void UpdateSignalsFromProperty(string propertyName, JsonElement value, ref ContextSignals signals)
	{
		var key = propertyName.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

		if (!signals.HasAge && key == "age" && IsPositiveNumber(value))
		{
			signals = signals with { HasAge = true };
		}

		if (!signals.HasGender && (key == "gender" || key == "sex") && IsGenderValue(value))
		{
			signals = signals with { HasGender = true };
		}

		if (!signals.HasSmoking && IsSmokingField(key) && IsSmokingValue(value))
		{
			signals = signals with { HasSmoking = true };
		}

		if (!signals.HasBudget && BudgetFieldNames.Contains(key) && IsPositiveNumber(value))
		{
			signals = signals with { HasBudget = true };
		}
	}

	private static bool IsSmokingField(string key)
		=> key is "smokingstatus" or "smoking_status" or "smokerstatus" or "healthstyle";

	private static bool IsGenderValue(JsonElement value)
	{
		if (value.ValueKind != JsonValueKind.String)
		{
			return false;
		}

		var text = value.GetString() ?? string.Empty;
		return text.Contains("male", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("female", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsSmokingValue(JsonElement value)
	{
		if (value.ValueKind != JsonValueKind.String)
		{
			return false;
		}

		var text = value.GetString() ?? string.Empty;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		if (text.Contains("smoker", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("non-smoker", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("nonsmoker", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("non smoker", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		// HSx/health-style values still indicate smoking classification was provided.
		return text.StartsWith("HS", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsPositiveNumber(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.Number)
		{
			return value.TryGetDecimal(out var numeric) && numeric > 0;
		}

		if (value.ValueKind == JsonValueKind.String)
		{
			return decimal.TryParse(value.GetString(), out var parsed) && parsed > 0;
		}

		return false;
	}

	private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (var property in element.EnumerateObject())
			{
				if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
				{
					value = property.Value;
					return true;
				}
			}
		}

		value = default;
		return false;
	}

	private readonly record struct ContextSignals(bool HasAge, bool HasGender, bool HasSmoking, bool HasBudget)
	{
		public bool HasAll => HasAge && HasGender && HasSmoking && HasBudget;
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

	private static string FormatIllustrationResultMarkdown(RecommendationTable recommendation, string knowledge)
	{
		var columns = recommendation.Columns;
		var lines = new List<string>
		{
			$"**{recommendation.ScenarioDescription}**",
			string.Empty,
			"| Metric | " + string.Join(" | ", columns.Select(column => EscapeForMarkdownTable(column.Label))) + " |",
			"| --- | " + string.Join(" | ", columns.Select(_ => "---")) + " |",
			"| **Base Coverage Amount** | " + string.Join(" | ", columns.Select(column => FormatCurrency(column.BaseCoverageAmount))) + " |",
			"| **Base Annual Premium** | " + string.Join(" | ", columns.Select(column => FormatCurrency(column.BaseAnnualPremium))) + " |",
			"| **Deposit Option Payment** | " + string.Join(" | ", columns.Select(column => column.DepositOptionPayment > 0 ? FormatCurrency(column.DepositOptionPayment) : "-")) + " |",
			"| **Total Annual Outlay** | " + string.Join(" | ", columns.Select(column => FormatCurrency(column.TotalAnnualOutlay))) + " |",
			"| **Cash Value @ Year 10** | " + string.Join(" | ", columns.Select(column => FormatCurrency(column.CashValueYear10))) + " |"
		};

		if (columns.Any(column => column.CashValueYear5 > 0))
		{
			lines.Add("| **Cash Value @ Year 5** | " + string.Join(" | ", columns.Select(column => FormatCurrency(column.CashValueYear5))) + " |");
		}

		if (columns.Any(column => column.CashValueYear20 > 0))
		{
			lines.Add("| **Cash Value @ Year 20** | " + string.Join(" | ", columns.Select(column => FormatCurrency(column.CashValueYear20))) + " |");
		}

		if (string.Equals(recommendation.ScenarioType, RecommendationScenarios.MaximizeEarlyCsv, StringComparison.Ordinal))
		{
			lines.Add("| **CV Efficiency @ Y10** | " + string.Join(" | ", columns.Select(column => FormatPercent(column.CvEfficiencyYear10, 1))) + " |");
		}

		lines.Add("| **IRR on CSV @ Year 10** | " + string.Join(" | ", columns.Select(column => FormatPercent(column.IrrOnCsvYear10, 2))) + " |");
		lines.Add("| **Death Benefit @ LE (Current DIR)** | " + string.Join(" | ", columns.Select(column => $"{FormatCurrency(column.DeathBenefitAtLeCurrent)} (IRR {FormatPercent(column.IrrAtLeCurrent, 2)})")) + " |");
		lines.Add("| **Death Benefit @ LE (Current -2%)** | " + string.Join(" | ", columns.Select(column => $"{FormatCurrency(column.DeathBenefitAtLeMinus2)} (IRR {FormatPercent(column.IrrAtLeMinus2, 2)})")) + " |");
		lines.Add("| **Quick Pay @ Current** | " + string.Join(" | ", columns.Select(column => $"{column.QuickPayCurrent} years")) + " |");
		lines.Add("| **Quick Pay @ Current -2%** | " + string.Join(" | ", columns.Select(column => $"{column.QuickPayMinus2} years")) + " |");

		var budgetWarningRows = columns
			.Where(column => recommendation.PremiumBudget > 0 && column.TotalAnnualOutlay > recommendation.PremiumBudget * 1.05m)
			.Select(column =>
			{
				var overPct = ((column.TotalAnnualOutlay / recommendation.PremiumBudget) - 1m) * 100m;
				return $"- **{column.Label}** - total outlay {FormatCurrency(column.TotalAnnualOutlay)}/yr ({overPct:+0;-0;0}% vs budget).";
			})
			.ToList();

		var columnWarnings = columns
			.SelectMany(column => column.Warnings.Select(warning => $"- **{column.Label}** - {EscapeForMarkdownTable(warning)}"))
			.Distinct(StringComparer.Ordinal)
			.ToList();

		if (budgetWarningRows.Count > 0 || columnWarnings.Count > 0)
		{
			lines.Add(string.Empty);
			if (budgetWarningRows.Count > 0)
			{
				lines.Add($"- **Budget caveat:** stated budget is {FormatCurrency(recommendation.PremiumBudget)}/yr. Over-budget options are shown for comparison.");
				lines.AddRange(budgetWarningRows);
			}

			if (columnWarnings.Count > 0)
			{
				if (budgetWarningRows.Count > 0)
				{
					lines.Add(string.Empty);
				}
				lines.AddRange(columnWarnings);
			}
		}

		var leValues = columns
			.Select(column => column.LifeExpectancyAgeUsed)
			.Distinct()
			.OrderBy(value => value)
			.ToArray();

		if (leValues.Length > 0)
		{
			lines.Add(string.Empty);
			lines.Add($"_Life expectancy age used for DB/IRR at LE: {string.Join(", ", leValues)}._");
		}

		if (!string.IsNullOrWhiteSpace(knowledge))
		{
			lines.Add(string.Empty);
			lines.Add($"Supporting note: {knowledge}");
		}

		return string.Join("\n", lines);
	}

	private static string EscapeForMarkdownTable(string value)
	{
		return value.Replace("|", "\\|").Replace("\n", " ");
	}

	private static string FormatCurrency(decimal value)
	{
		return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
	}

	private static string FormatPercent(decimal value, int decimals)
	{
		return value.ToString($"N{decimals}", CultureInfo.InvariantCulture) + "%";
	}

	private static ColumnsEventPayload BuildColumnsPayload(ChatRequestDto request, RecommendationTable recommendation)
	{
		var columns = recommendation.Columns
			.Select(column => new UdmPatchColumnDto(
				Label: column.Label,
				Operations: BuildColumnOperations(column, recommendation),
				Id: column.Id,
				Recommended: column.Recommended,
				RequiresConfirmation: true,
				Explain: column.Explain,
				Warnings: column.Warnings,
				IrtUdm: request.UdmContext.HasValue ? request.UdmContext.Value.Clone() : null,
				IrtCalcResponse: new
				{
					configuration = column.Label,
					scenario_type = recommendation.ScenarioType,
					generated_by = "MISA_Agentic"
				},
				IrtMetrics: new
				{
					base_coverage_amount = column.BaseCoverageAmount,
					annual_premium = column.BaseAnnualPremium,
					deposit_option_payment = column.DepositOptionPayment,
					total_annual_outlay = column.TotalAnnualOutlay,
					cash_value_year_5 = column.CashValueYear5,
					cash_value_year_10 = column.CashValueYear10,
					cash_value_year_20 = column.CashValueYear20,
					cv_efficiency_year_10 = column.CvEfficiencyYear10,
					irr_on_csv_year_10 = column.IrrOnCsvYear10,
					death_benefit_at_le_current = column.DeathBenefitAtLeCurrent,
					irr_at_le_current = column.IrrAtLeCurrent,
					death_benefit_at_le_minus2 = column.DeathBenefitAtLeMinus2,
					irr_at_le_minus2 = column.IrrAtLeMinus2,
					quick_pay_current = column.QuickPayCurrent,
					quick_pay_minus2 = column.QuickPayMinus2,
					extended_payments_for_stress = column.ExtendedPaymentsForStress,
					life_expectancy_age_used = column.LifeExpectancyAgeUsed
				}))
			.ToList();

		return new ColumnsEventPayload(
			Version: "1.0",
			SessionId: request.SessionId,
			Columns: columns,
			IrtUdm: request.UdmContext.HasValue ? request.UdmContext.Value.Clone() : null,
			IrtCalcResponse: new
			{
				scenario_type = recommendation.ScenarioType,
				scenario_description = recommendation.ScenarioDescription,
				column_count = recommendation.Columns.Count,
				generated_by = "MISA_Agentic"
			},
			IrtMetrics: new
			{
				client_summary = recommendation.ClientSummary,
				premium_budget = recommendation.PremiumBudget,
				generated_at_utc = DateTimeOffset.UtcNow
			});
	}

	private static IReadOnlyList<UdmPatchOperationDto> BuildColumnOperations(
		RecommendationColumn column,
		RecommendationTable recommendation)
	{
		var strategy = ResolveDepositStrategy(column.Label);
		var operations = new List<UdmPatchOperationDto>
		{
			new("replace", "/payments/premiumDuration", ResolvePremiumDuration(column.Label)),
			new("replace", "/coverage/amount", column.BaseCoverageAmount),
			new("replace", "/deposits/depositOptionStrategy", strategy),
			new("replace", "/illustration/scenario/scenarioType", recommendation.ScenarioType),
			new("replace", "/illustration/scenario/selectedConfig", column.Label),
			new("replace", "/illustration/scenario/premiumBudget", recommendation.PremiumBudget)
		};

		if (string.Equals(strategy, "Specified", StringComparison.Ordinal))
		{
			operations.Add(new UdmPatchOperationDto("replace", "/deposits/depositOptionPct", 0.25m));
		}

		if (string.Equals(strategy, "LevelMax", StringComparison.Ordinal))
		{
			operations.Add(new UdmPatchOperationDto("replace", "/deposits/depositOptionAmount", column.DepositOptionPayment));
		}

		return operations;
	}

	private static string ResolvePremiumDuration(string label)
	{
		if (label.Contains("pay 10", StringComparison.OrdinalIgnoreCase))
		{
			return "Pay10";
		}

		if (label.Contains("pay 20", StringComparison.OrdinalIgnoreCase))
		{
			return "Pay20";
		}

		if (label.Contains("pay 100", StringComparison.OrdinalIgnoreCase))
		{
			return "Pay100";
		}

		return "Pay90";
	}

	private static string ResolveDepositStrategy(string label)
	{
		if (label.Contains("25%", StringComparison.OrdinalIgnoreCase))
		{
			return "Specified";
		}

		if (label.Contains("lvl max", StringComparison.OrdinalIgnoreCase)
			|| label.Contains("level max", StringComparison.OrdinalIgnoreCase))
		{
			return "LevelMax";
		}

		return "None";
	}
}
