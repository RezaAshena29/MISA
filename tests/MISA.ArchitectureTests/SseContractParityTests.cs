using MISA.Application;
using MISA.Contracts;
using MISA.Infrastructure;
using MISA.Orchestration.Akka;
using Microsoft.Extensions.Logging.Abstractions;

namespace MISA.ArchitectureTests;

public sealed class SseContractParityTests
{
	[Fact]
	public async Task IllustrationRouteProducesExpectedOrderedEvents()
	{
		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("illustration"),
			new StaticKnowledgeService(),
			new StaticDecisioningService(),
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("male age 45 non-smoker budget $100k", "session-illustration");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		Assert.True(events.Count >= 6);
		Assert.Equal(ChatEventType.Thinking, events[0].Type);
		Assert.Equal(ChatEventType.Assumptions, events[1].Type);
		Assert.Equal(ChatEventType.Progress, events[2].Type);
		Assert.Equal(ChatEventType.Result, events[^1].Type);
	}

	[Fact]
	public async Task InsufficientIllustrationInputEndsWithClarification()
	{
		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("illustration"),
			new StaticKnowledgeService(),
			new StaticDecisioningService(),
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("male age 45", "session-clarification");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		Assert.Equal(2, events.Count);
		Assert.Equal(ChatEventType.Thinking, events[0].Type);
		Assert.Equal(ChatEventType.Clarification, events[1].Type);
	}

	[Fact]
	public async Task ContextConsentProducesColumnsPayload()
	{
		var sessionStore = new InMemoryChatSessionStore();
		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("illustration"),
			new StaticKnowledgeService(),
			new StaticDecisioningService(),
			sessionStore,
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		using var document = System.Text.Json.JsonDocument.Parse("{\"illustration\":{\"id\":\"case-1\"}}");
		var request = new ChatRequestDto(
			"female age 40 non-smoker budget $80k",
			"session-columns",
			ContextConsent: true,
			UdmContext: document.RootElement);

		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));
		var columnsEvent = Assert.Single(events, evt => evt.Type == ChatEventType.Columns);
		var payload = Assert.IsType<ColumnsEventPayload>(columnsEvent.Content);
		Assert.Equal("session-columns", payload.SessionId);
		Assert.NotEmpty(payload.Columns);
	}

	[Fact]
	public async Task IllustrationWithoutContextConsentDoesNotProduceColumnsEvent()
	{
		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("illustration"),
			new StaticKnowledgeService(),
			new StaticDecisioningService(),
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		using var document = System.Text.Json.JsonDocument.Parse("{\"illustration\":{\"id\":\"case-2\"}}");
		var request = new ChatRequestDto(
			"female age 40 non-smoker budget $80k",
			"session-no-columns",
			ContextConsent: false,
			UdmContext: document.RootElement);

		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));
		Assert.DoesNotContain(events, evt => evt.Type == ChatEventType.Columns);
	}

	[Fact]
	public async Task KnowledgeRouteProducesThinkingThenResult()
	{
		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("knowledge"),
			new StaticKnowledgeService(),
			new StaticDecisioningService(),
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("what is participating policy", "session-knowledge");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		Assert.Equal(2, events.Count);
		Assert.Equal(ChatEventType.Thinking, events[0].Type);
		Assert.Equal(ChatEventType.Result, events[1].Type);
		Assert.Equal("Knowledge reference for contract test.", events[1].Content);
	}

	[Fact]
	public async Task ReasoningRouteWithoutPriorRecommendationReturnsFallbackExplanation()
	{
		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("reasoning"),
			new StaticKnowledgeService(),
			new StaticDecisioningService(),
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("why this option", "session-reasoning-empty");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		Assert.Equal(2, events.Count);
		Assert.Equal(ChatEventType.Thinking, events[0].Type);
		Assert.Equal(ChatEventType.Result, events[1].Type);
		Assert.Contains("I do not have a prior recommendation", events[1].Content.ToString(), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ReasoningRouteWithPriorRecommendationIncludesHistory()
	{
		var sessionStore = new InMemoryChatSessionStore();
		await sessionStore.SaveAsync(
			new ChatSessionState("session-reasoning-prior", LastRoute: "illustration", LastRecommendation: "Pay 90 is optimal for this profile."),
			CancellationToken.None);

		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("reasoning"),
			new StaticKnowledgeService(),
			new StaticDecisioningService(),
			sessionStore,
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("explain why", "session-reasoning-prior");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		var result = Assert.Single(events, evt => evt.Type == ChatEventType.Result);
		Assert.Contains("Pay 90 is optimal for this profile.", result.Content.ToString(), StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Knowledge reference for contract test.", result.Content.ToString(), StringComparison.OrdinalIgnoreCase);
	}

	private static async Task<List<ChatEventEnvelope>> CollectAsync(IAsyncEnumerable<ChatEventEnvelope> stream)
	{
		var results = new List<ChatEventEnvelope>();
		await foreach (var evt in stream)
		{
			results.Add(evt);
		}

		return results;
	}

	private sealed class FixedRouteRouter : IAgentRouter
	{
		private readonly string _route;

		public FixedRouteRouter(string route)
		{
			_route = route;
		}

		public Task<string> ResolveRouteAsync(ChatRequestDto request, CancellationToken cancellationToken)
		{
			return Task.FromResult(_route);
		}
	}

	private sealed class StaticKnowledgeService : IKnowledgeService
	{
		public Task<string> AnswerAsync(ChatRequestDto request, CancellationToken cancellationToken)
		{
			return Task.FromResult("Knowledge reference for contract test.");
		}
	}

	private sealed class StaticDecisioningService : IDecisioningService
	{
		public Task<string> BuildRecommendationAsync(ChatRequestDto request, CancellationToken cancellationToken)
		{
			return Task.FromResult("Recommendation generated for contract parity test.");
		}

		public Task<RecommendationTable> BuildRecommendationTableAsync(ChatRequestDto request, CancellationToken cancellationToken)
		{
			return Task.FromResult(new RecommendationTable(
				ScenarioDescription: "Maximize IRR at Life Expectancy: M45 NonSmoker, Single Life, $100,000/annual",
				ScenarioType: RecommendationScenarios.MaximizeIrrAtLe,
				ClientSummary: "M45 NonSmoker, Single Life",
				PremiumBudget: 100000m,
				Columns:
				[
					new RecommendationColumn(
						Id: "pay90-lvlmax",
						Label: "Pay 90, Lvl Max DO",
						BaseCoverageAmount: 1150000m,
						BaseAnnualPremium: 19311m,
						DepositOptionPayment: 30703m,
						TotalAnnualOutlay: 50014m,
						CashValueYear10: 543959m,
						CashValueYear5: 228332m,
						CashValueYear20: 1498820m,
						CvEfficiencyYear10: 108.8m,
						IrrOnCsvYear10: 7.2m,
						DeathBenefitAtLeCurrent: 12447867m,
						IrrAtLeCurrent: 5.92m,
						DeathBenefitAtLeMinus2: 6948928m,
						IrrAtLeMinus2: 4.02m,
						QuickPayCurrent: 5,
						QuickPayMinus2: 6,
						Recommended: true,
						ExtendedPaymentsForStress: 10,
						StressPaymentExtensionNote: "Stress scale required 10 additional payment years.",
						LifeExpectancyAgeUsed: 84,
						Explain: "Apply (recommended)",
						Warnings: ["Stress scale required 10 additional payment years."]),
					new RecommendationColumn(
						Id: "pay20-lvlmax",
						Label: "Pay 20, Lvl Max DO",
						BaseCoverageAmount: 910000m,
						BaseAnnualPremium: 32126m,
						DepositOptionPayment: 18218m,
						TotalAnnualOutlay: 50344m,
						CashValueYear10: 593007m,
						CashValueYear5: 250120m,
						CashValueYear20: 1602520m,
						CvEfficiencyYear10: 117.8m,
						IrrOnCsvYear10: 8.3m,
						DeathBenefitAtLeCurrent: 9770800m,
						IrrAtLeCurrent: 5.74m,
						DeathBenefitAtLeMinus2: 5126139m,
						IrrAtLeMinus2: 3.85m,
						QuickPayCurrent: 7,
						QuickPayMinus2: 10,
						Recommended: false,
						ExtendedPaymentsForStress: 40,
						StressPaymentExtensionNote: "Stress scale required 40 additional payment years.",
						LifeExpectancyAgeUsed: 84,
						Explain: "Apply to scenario",
						Warnings: ["Stress scale required 40 additional payment years."])
				]));
		}
	}
}