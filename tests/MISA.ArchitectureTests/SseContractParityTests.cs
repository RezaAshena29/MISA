using MISA.Application;
using MISA.Contracts;
using MISA.Decisioning;
using MISA.Infrastructure;
using MISA.Knowledge;
using MISA.Orchestration.Akka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
	public async Task IllustrationRouteCalculationTimeoutUsesFallbackRecommendationAndPartialProgress()
	{
		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("illustration"),
			new StaticKnowledgeService(),
			new SlowDecisioningService(TimeSpan.FromSeconds(7)),
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("male age 45 non-smoker budget $100k", "session-calc-timeout");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		var fallbackPrevalidation = Assert.Single(
			events,
			evt =>
				evt.Type == ChatEventType.Prevalidation
				&& EventContent(evt).Contains("Resiliency fallback was activated", StringComparison.Ordinal));
		var warningContent = EventContent(fallbackPrevalidation);
		Assert.Contains("calculation branch", warningContent, StringComparison.OrdinalIgnoreCase);

		var fallbackProgress = Assert.Single(
			events,
			evt =>
				evt.Type == ChatEventType.Progress
				&& EventContent(evt).Contains("Applying fallback ranking", StringComparison.OrdinalIgnoreCase));
		Assert.Contains("partial results", EventContent(fallbackProgress), StringComparison.OrdinalIgnoreCase);

		var resultEvent = Assert.Single(events, evt => evt.Type == ChatEventType.Result);
		Assert.Contains("Fallback recommendation (partial execution)", EventContent(resultEvent), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task IllustrationRouteKnowledgeTimeoutUsesFallbackKnowledgeAndPartialProgress()
	{
		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("illustration"),
			new SlowKnowledgeService(TimeSpan.FromSeconds(4)),
			new StaticDecisioningService(),
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("male age 45 non-smoker budget $100k", "session-knowledge-timeout");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		var fallbackPrevalidation = Assert.Single(
			events,
			evt =>
				evt.Type == ChatEventType.Prevalidation
				&& EventContent(evt).Contains("Resiliency fallback was activated", StringComparison.Ordinal));
		Assert.Contains("knowledge branch timed out", EventContent(fallbackPrevalidation), StringComparison.OrdinalIgnoreCase);

		var fallbackProgress = Assert.Single(
			events,
			evt =>
				evt.Type == ChatEventType.Progress
				&& EventContent(evt).Contains("Applying fallback ranking", StringComparison.OrdinalIgnoreCase));
		Assert.Contains("partial results", EventContent(fallbackProgress), StringComparison.OrdinalIgnoreCase);

		var resultEvent = Assert.Single(events, evt => evt.Type == ChatEventType.Result);
		Assert.Contains("Supporting knowledge is temporarily unavailable", EventContent(resultEvent), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task IllustrationRouteBranchFailuresEmitCombinedFallbackWarningAndPartialProgress()
	{
		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("illustration"),
			new ThrowingKnowledgeService(),
			new ThrowingDecisioningService(),
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("male age 45 non-smoker budget $100k", "session-fallback-warning");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		var fallbackPrevalidation = Assert.Single(
			events,
			evt =>
				evt.Type == ChatEventType.Prevalidation
				&& EventContent(evt).Contains("Resiliency fallback was activated", StringComparison.Ordinal));
		var warningContent = EventContent(fallbackPrevalidation);
		Assert.Contains("calculation branch failed", warningContent, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("knowledge branch failed", warningContent, StringComparison.OrdinalIgnoreCase);

		var fallbackProgress = Assert.Single(
			events,
			evt =>
				evt.Type == ChatEventType.Progress
				&& EventContent(evt).Contains("Applying fallback ranking", StringComparison.OrdinalIgnoreCase));
		Assert.Contains("partial results", EventContent(fallbackProgress), StringComparison.OrdinalIgnoreCase);
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
	public async Task KnowledgeRouteWithMcpEnabledStillProducesThinkingThenResult()
	{
		var knowledgeService = new McpKnowledgeServiceDecorator(
			new KnowledgeService(),
			new StaticMcpToolBroker(McpToolCallResult.Succeeded("MCP knowledge response", TimeSpan.FromMilliseconds(10))),
			Options.Create(new KnowledgeMcpOptions
			{
				Enabled = true,
				ToolName = "knowledge.answer"
			}));

		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("knowledge"),
			knowledgeService,
			new StaticDecisioningService(),
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("what is participating policy", "session-knowledge-mcp-enabled");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		Assert.Equal(2, events.Count);
		Assert.Equal(ChatEventType.Thinking, events[0].Type);
		Assert.Equal(ChatEventType.Result, events[1].Type);
		Assert.Equal("MCP knowledge response", events[1].Content);
	}

	[Fact]
	public async Task KnowledgeRouteWithCoordinatorUsesSingleMcpPath()
	{
		var coordinator = new StubMcpCoordinator((route, _, _, _, _, _) =>
		{
			if (string.Equals(route, "knowledge", StringComparison.OrdinalIgnoreCase))
			{
				return Task.FromResult(McpToolCallResult.Succeeded("Coordinator knowledge response", TimeSpan.FromMilliseconds(5)));
			}

			return Task.FromResult(McpToolCallResult.Failed("unsupported_route", route, TimeSpan.FromMilliseconds(1)));
		});

		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("knowledge"),
			new ThrowingKnowledgeService(),
			new StaticDecisioningService(),
			new InMemoryChatSessionStore(),
			coordinator,
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("what is participating policy", "session-knowledge-coordinator");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		Assert.Equal(2, events.Count);
		Assert.Equal(ChatEventType.Thinking, events[0].Type);
		Assert.Equal(ChatEventType.Result, events[1].Type);
		Assert.Equal("Coordinator knowledge response", events[1].Content);
		Assert.Contains("knowledge", coordinator.InvokedRoutes, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ClarificationRouteWithCoordinatorUsesMcpPrompt()
	{
		var coordinator = new StubMcpCoordinator((route, _, _, _, _, _) =>
		{
			if (string.Equals(route, "clarification", StringComparison.OrdinalIgnoreCase))
			{
				return Task.FromResult(McpToolCallResult.Succeeded("Coordinator clarification prompt", TimeSpan.FromMilliseconds(5)));
			}

			return Task.FromResult(McpToolCallResult.Failed("unsupported_route", route, TimeSpan.FromMilliseconds(1)));
		});

		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("clarification"),
			new StaticKnowledgeService(),
			new StaticDecisioningService(),
			new InMemoryChatSessionStore(),
			coordinator,
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("need next inputs", "session-clarification-coordinator");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		var clarification = Assert.Single(events, evt => evt.Type == ChatEventType.Clarification);
		Assert.Equal("Coordinator clarification prompt", clarification.Content);
		Assert.Contains("clarification", coordinator.InvokedRoutes, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task IllustrationRouteWithCoordinatorUsesMcpForDecisioningAndKnowledge()
	{
		var decisioningPayload = System.Text.Json.JsonSerializer.Serialize(BuildMcpDecisioningTable());
		var coordinator = new StubMcpCoordinator((route, _, _, _, _, _) =>
		{
			if (string.Equals(route, "illustration", StringComparison.OrdinalIgnoreCase))
			{
				return Task.FromResult(McpToolCallResult.Succeeded(decisioningPayload, TimeSpan.FromMilliseconds(6)));
			}

			if (string.Equals(route, "knowledge", StringComparison.OrdinalIgnoreCase))
			{
				return Task.FromResult(McpToolCallResult.Succeeded("Coordinator knowledge note", TimeSpan.FromMilliseconds(6)));
			}

			return Task.FromResult(McpToolCallResult.Failed("unsupported_route", route, TimeSpan.FromMilliseconds(1)));
		});

		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("illustration"),
			new ThrowingKnowledgeService(),
			new ThrowingDecisioningService(),
			new InMemoryChatSessionStore(),
			coordinator,
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("male age 45 non-smoker budget $100k", "session-illustration-coordinator");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		var result = Assert.Single(events, evt => evt.Type == ChatEventType.Result);
		var resultText = result.Content?.ToString() ?? string.Empty;
		Assert.Contains("MCP decisioning scenario", resultText, StringComparison.Ordinal);
		Assert.Contains("Coordinator knowledge note", resultText, StringComparison.Ordinal);
		Assert.Contains("illustration", coordinator.InvokedRoutes, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("knowledge", coordinator.InvokedRoutes, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task KnowledgeRouteMcpDisabledAndEnabledKeepSameEventSequence()
	{
		var disabledKnowledge = new McpKnowledgeServiceDecorator(
			new KnowledgeService(),
			new ThrowingMcpToolBroker(),
			Options.Create(new KnowledgeMcpOptions
			{
				Enabled = false,
				ToolName = "knowledge.answer"
			}));

		var enabledKnowledge = new McpKnowledgeServiceDecorator(
			new KnowledgeService(),
			new StaticMcpToolBroker(McpToolCallResult.Succeeded("MCP knowledge response", TimeSpan.FromMilliseconds(12))),
			Options.Create(new KnowledgeMcpOptions
			{
				Enabled = true,
				ToolName = "knowledge.answer"
			}));

		await using var disabledRuntime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("knowledge"),
			disabledKnowledge,
			new StaticDecisioningService(),
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		await using var enabledRuntime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("knowledge"),
			enabledKnowledge,
			new StaticDecisioningService(),
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("what is participating policy", "session-knowledge-mcp-parity");
		var disabledEvents = await CollectAsync(disabledRuntime.ExecuteAsync(request, CancellationToken.None));
		var enabledEvents = await CollectAsync(enabledRuntime.ExecuteAsync(request, CancellationToken.None));

		Assert.Equal(disabledEvents.Count, enabledEvents.Count);
		Assert.Equal(disabledEvents.Select(evt => evt.Type), enabledEvents.Select(evt => evt.Type));
	}

	[Fact]
	public async Task IllustrationRouteWithDecisioningMcpEnabledKeepsEventShape()
	{
		var mcpTableJson = System.Text.Json.JsonSerializer.Serialize(BuildMcpDecisioningTable());
		var decisioning = new McpDecisioningServiceDecorator(
			new RuleBasedDecisioningService(),
			new StaticMcpToolBroker(McpToolCallResult.Succeeded(mcpTableJson, TimeSpan.FromMilliseconds(10))),
			Options.Create(new DecisioningMcpOptions
			{
				Enabled = true,
				RecommendationTableToolName = "decisioning.recommendation.table"
			}));

		await using var runtime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("illustration"),
			new StaticKnowledgeService(),
			decisioning,
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("male age 45 non-smoker budget $100k", "session-illustration-mcp-decisioning");
		var events = await CollectAsync(runtime.ExecuteAsync(request, CancellationToken.None));

		Assert.True(events.Count >= 6);
		Assert.Equal(ChatEventType.Thinking, events[0].Type);
		Assert.Equal(ChatEventType.Result, events[^1].Type);
		Assert.Contains("MCP decisioning scenario", EventContent(events[^1]), StringComparison.Ordinal);
	}

	[Fact]
	public async Task IllustrationRouteDecisioningMcpDisabledAndFailedKeepSameEventSequence()
	{
		var disabledDecisioning = new McpDecisioningServiceDecorator(
			new RuleBasedDecisioningService(),
			new ThrowingMcpToolBroker(),
			Options.Create(new DecisioningMcpOptions
			{
				Enabled = false,
				RecommendationTableToolName = "decisioning.recommendation.table"
			}));

		var failedDecisioning = new McpDecisioningServiceDecorator(
			new RuleBasedDecisioningService(),
			new StaticMcpToolBroker(McpToolCallResult.Failed("invalid_payload", "malformed table payload", TimeSpan.FromMilliseconds(8))),
			Options.Create(new DecisioningMcpOptions
			{
				Enabled = true,
				RecommendationTableToolName = "decisioning.recommendation.table"
			}));

		await using var disabledRuntime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("illustration"),
			new StaticKnowledgeService(),
			disabledDecisioning,
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		await using var failedRuntime = new AkkaClusterExecutionRuntime(
			new FixedRouteRouter("illustration"),
			new StaticKnowledgeService(),
			failedDecisioning,
			new InMemoryChatSessionStore(),
			NullLogger<AkkaClusterExecutionRuntime>.Instance);

		var request = new ChatRequestDto("male age 45 non-smoker budget $100k", "session-illustration-mcp-decisioning-failed");
		var disabledEvents = await CollectAsync(disabledRuntime.ExecuteAsync(request, CancellationToken.None));
		var failedEvents = await CollectAsync(failedRuntime.ExecuteAsync(request, CancellationToken.None));

		Assert.Equal(disabledEvents.Count, failedEvents.Count);
		Assert.Equal(disabledEvents.Select(evt => evt.Type), failedEvents.Select(evt => evt.Type));
		Assert.DoesNotContain(failedEvents, evt => evt.Type == ChatEventType.Error);
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

	private static string EventContent(ChatEventEnvelope envelope)
	{
		return envelope.Content?.ToString() ?? string.Empty;
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

	private sealed class StaticMcpToolBroker : IMcpToolBroker
	{
		private readonly McpToolCallResult _result;

		public StaticMcpToolBroker(McpToolCallResult result)
		{
			_result = result;
		}

		public Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(_result);
		}
	}

	private sealed class ThrowingMcpToolBroker : IMcpToolBroker
	{
		public Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
		{
			throw new InvalidOperationException("MCP broker must not be called when Knowledge MCP is disabled.");
		}
	}

	private sealed class StubMcpCoordinator : IMcpCoordinator
	{
		private readonly Func<string, ChatRequestDto, ChatSessionState?, string?, IReadOnlyDictionary<string, string?>?, CancellationToken, Task<McpToolCallResult>> _handler;

		public StubMcpCoordinator(
			Func<string, ChatRequestDto, ChatSessionState?, string?, IReadOnlyDictionary<string, string?>?, CancellationToken, Task<McpToolCallResult>> handler)
		{
			_handler = handler;
		}

		public List<string> InvokedRoutes { get; } = [];

		public Task<McpToolCallResult> InvokeForRouteAsync(
			string route,
			ChatRequestDto request,
			ChatSessionState? priorState,
			string? input,
			IReadOnlyDictionary<string, string?>? additionalAttributes,
			CancellationToken cancellationToken)
		{
			InvokedRoutes.Add(route);
			return _handler(route, request, priorState, input, additionalAttributes, cancellationToken);
		}
	}

	private sealed class SlowKnowledgeService : IKnowledgeService
	{
		private readonly TimeSpan _delay;

		public SlowKnowledgeService(TimeSpan delay)
		{
			_delay = delay;
		}

		public async Task<string> AnswerAsync(ChatRequestDto request, CancellationToken cancellationToken)
		{
			await Task.Delay(_delay, cancellationToken);
			return "Delayed knowledge response for timeout test.";
		}
	}

	private sealed class ThrowingKnowledgeService : IKnowledgeService
	{
		public Task<string> AnswerAsync(ChatRequestDto request, CancellationToken cancellationToken)
		{
			return Task.FromException<string>(new InvalidOperationException("knowledge test failure"));
		}
	}

	private sealed class SlowDecisioningService : IDecisioningService
	{
		private readonly TimeSpan _delay;

		public SlowDecisioningService(TimeSpan delay)
		{
			_delay = delay;
		}

		public async Task<string> BuildRecommendationAsync(ChatRequestDto request, CancellationToken cancellationToken)
		{
			await Task.Delay(_delay, cancellationToken);
			return "Delayed recommendation for timeout test.";
		}

		public async Task<RecommendationTable> BuildRecommendationTableAsync(ChatRequestDto request, CancellationToken cancellationToken)
		{
			await Task.Delay(_delay, cancellationToken);
			return BuildMinimalRecommendationTable();
		}
	}

	private sealed class ThrowingDecisioningService : IDecisioningService
	{
		public Task<string> BuildRecommendationAsync(ChatRequestDto request, CancellationToken cancellationToken)
		{
			return Task.FromException<string>(new InvalidOperationException("decisioning test failure"));
		}

		public Task<RecommendationTable> BuildRecommendationTableAsync(ChatRequestDto request, CancellationToken cancellationToken)
		{
			return Task.FromException<RecommendationTable>(new InvalidOperationException("decisioning test failure"));
		}
	}

	private static RecommendationTable BuildMinimalRecommendationTable()
	{
		return new RecommendationTable(
			ScenarioDescription: "Delayed recommendation",
			ScenarioType: RecommendationScenarios.MaximizeIrrAtLe,
			ClientSummary: "Test profile",
			PremiumBudget: 100000m,
			Columns:
			[
				new RecommendationColumn(
					Id: "delay",
					Label: "Delayed",
					BaseCoverageAmount: 1000000m,
					BaseAnnualPremium: 20000m,
					DepositOptionPayment: 0m,
					TotalAnnualOutlay: 20000m,
					CashValueYear10: 0m,
					CashValueYear5: 0m,
					CashValueYear20: 0m,
					CvEfficiencyYear10: 0m,
					IrrOnCsvYear10: 0m,
					DeathBenefitAtLeCurrent: 0m,
					IrrAtLeCurrent: 0m,
					DeathBenefitAtLeMinus2: 0m,
					IrrAtLeMinus2: 0m,
					QuickPayCurrent: 0,
					QuickPayMinus2: 0,
					Recommended: true,
					ExtendedPaymentsForStress: null,
					StressPaymentExtensionNote: "Delayed recommendation",
					LifeExpectancyAgeUsed: 84,
					Explain: "Delayed recommendation",
					Warnings: []),
			]);
	}

	private static RecommendationTable BuildMcpDecisioningTable()
	{
		return new RecommendationTable(
			ScenarioDescription: "MCP decisioning scenario",
			ScenarioType: RecommendationScenarios.MaximizeIrrAtLe,
			ClientSummary: "MCP generated profile",
			PremiumBudget: 100000m,
			Columns:
			[
				new RecommendationColumn(
					Id: "mcp-pay90",
					Label: "MCP Pay 90",
					BaseCoverageAmount: 1200000m,
					BaseAnnualPremium: 21000m,
					DepositOptionPayment: 0m,
					TotalAnnualOutlay: 21000m,
					CashValueYear10: 300000m,
					CashValueYear5: 120000m,
					CashValueYear20: 700000m,
					CvEfficiencyYear10: 103.4m,
					IrrOnCsvYear10: 6.9m,
					DeathBenefitAtLeCurrent: 2500000m,
					IrrAtLeCurrent: 5.1m,
					DeathBenefitAtLeMinus2: 2000000m,
					IrrAtLeMinus2: 4.3m,
					QuickPayCurrent: 9,
					QuickPayMinus2: 11,
					Recommended: true,
					ExtendedPaymentsForStress: null,
					StressPaymentExtensionNote: null,
					LifeExpectancyAgeUsed: 84,
					Explain: "MCP-backed recommendation",
					Warnings: []),
			]);
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