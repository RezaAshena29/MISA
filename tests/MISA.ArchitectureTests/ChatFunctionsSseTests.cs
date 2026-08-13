using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MISA.Application;
using MISA.Contracts;
using MISA.Decisioning;
using MISA.Functions;
using MISA.Infrastructure;
using MISA.Knowledge;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MISA.ArchitectureTests;

public sealed class ChatFunctionsSseTests
{
	[Fact]
	public async Task ChatWritesSseEventAndDataFramesForPipelineEvents()
	{
		var pipeline = new StaticPipeline(
		[
			ChatEventEnvelope.Text(ChatEventType.Progress, "Running deterministic stage..."),
			ChatEventEnvelope.Text(ChatEventType.Result, "Top recommendation generated.")
		]);

		var functions = new ChatFunctions(
			pipeline,
			new InMemoryChatSessionStore(),
			NullLogger<ChatFunctions>.Instance);

		var request = CreateRequest("{\"message\":\"recommend plan\",\"sessionId\":\"session-func-1\"}");
		var response = await functions.Chat(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.True(response.Headers.TryGetValues("Content-Type", out var contentTypes));
		Assert.Contains("text/event-stream", contentTypes);

		var body = await ReadBodyAsStringAsync(response);
		Assert.Contains("event: progress\n", body, StringComparison.Ordinal);
		Assert.Contains("\"type\":\"progress\"", body, StringComparison.Ordinal);
		Assert.Contains("\"content\":\"Running deterministic stage...\"", body, StringComparison.Ordinal);
		Assert.Contains("event: result\n", body, StringComparison.Ordinal);
		Assert.Contains("\"type\":\"result\"", body, StringComparison.Ordinal);
		Assert.Contains("\"content\":\"Top recommendation generated.\"", body, StringComparison.Ordinal);
		Assert.Contains("\n\n", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChatInvalidPayloadReturnsErrorSseFrameAndSkipsPipeline()
	{
		var pipeline = new CountingPipeline();
		var functions = new ChatFunctions(
			pipeline,
			new InMemoryChatSessionStore(),
			NullLogger<ChatFunctions>.Instance);

		var request = CreateRequest("{\"message\":\"\",\"sessionId\":\"session-func-2\"}");
		var response = await functions.Chat(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(0, pipeline.CallCount);

		var body = await ReadBodyAsStringAsync(response);
		Assert.Contains("event: error\n", body, StringComparison.Ordinal);
		Assert.Contains("Invalid chat payload", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ClearSessionReturnsJsonCompatibilityPayload()
	{
		var store = new InMemoryChatSessionStore();
		await store.SaveAsync(new ChatSessionState("session-clear-1", LastRoute: "illustration"), CancellationToken.None);

		var functions = new ChatFunctions(
			new StaticPipeline([]),
			store,
			NullLogger<ChatFunctions>.Instance);

		var request = CreateRequest("{}", method: "DELETE", url: "https://localhost/api/irt/chat/session/session-clear-1");
		var response = await functions.ClearSession(request, "session-clear-1");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.True(response.Headers.TryGetValues("Content-Type", out var contentTypes));
		Assert.Contains("application/json", contentTypes);

		var body = await ReadBodyAsStringAsync(response);
		Assert.Contains("\"session_id\":\"session-clear-1\"", body, StringComparison.Ordinal);
		Assert.Contains("\"cleared\":true", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ChatKeepsSseEventSequenceParityForBaselineAndMcpLikeKnowledgeOutputs()
	{
		var baselineFunctions = new ChatFunctions(
			new StaticPipeline(
			[
				ChatEventEnvelope.Text(ChatEventType.Thinking, "Looking this up in the knowledge base..."),
				ChatEventEnvelope.Text(ChatEventType.Result, "Baseline knowledge response")
			]),
			new InMemoryChatSessionStore(),
			NullLogger<ChatFunctions>.Instance);

		var mcpLikeFunctions = new ChatFunctions(
			new StaticPipeline(
			[
				ChatEventEnvelope.Text(ChatEventType.Thinking, "Looking this up in the knowledge base..."),
				ChatEventEnvelope.Text(ChatEventType.Result, "MCP knowledge response")
			]),
			new InMemoryChatSessionStore(),
			NullLogger<ChatFunctions>.Instance);

		var requestBody = "{\"message\":\"what is participating policy\",\"sessionId\":\"session-func-parity\"}";
		var baselineResponse = await baselineFunctions.Chat(CreateRequest(requestBody));
		var mcpLikeResponse = await mcpLikeFunctions.Chat(CreateRequest(requestBody));

		var baselineBody = await ReadBodyAsStringAsync(baselineResponse);
		var mcpLikeBody = await ReadBodyAsStringAsync(mcpLikeResponse);

		Assert.Equal(ExtractEventTypes(baselineBody), ExtractEventTypes(mcpLikeBody));
		Assert.Equal(["thinking", "result"], ExtractEventTypes(mcpLikeBody));
		Assert.Contains("\"type\":\"thinking\"", baselineBody, StringComparison.Ordinal);
		Assert.Contains("\"type\":\"result\"", baselineBody, StringComparison.Ordinal);
		Assert.Contains("\"type\":\"thinking\"", mcpLikeBody, StringComparison.Ordinal);
		Assert.Contains("\"type\":\"result\"", mcpLikeBody, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChatWithConfigDrivenKnowledgeMcpTogglePreservesSseEventSequence()
	{
		var disabledFunctions = CreateConfiguredChatFunctions(
			knowledgeMcpEnabled: false,
			new ThrowingMcpToolBroker());
		var enabledFunctions = CreateConfiguredChatFunctions(
			knowledgeMcpEnabled: true,
			new StaticMcpToolBroker("MCP knowledge response"));

		var requestBody = "{\"message\":\"what is participating policy\",\"sessionId\":\"session-func-config-mcp\"}";
		var disabledResponse = await disabledFunctions.Chat(CreateRequest(requestBody));
		var enabledResponse = await enabledFunctions.Chat(CreateRequest(requestBody));

		var disabledBody = await ReadBodyAsStringAsync(disabledResponse);
		var enabledBody = await ReadBodyAsStringAsync(enabledResponse);

		Assert.Equal(ExtractEventTypes(disabledBody), ExtractEventTypes(enabledBody));
		Assert.Equal(["thinking", "thinking", "result"], ExtractEventTypes(enabledBody));
		Assert.Contains("MCP knowledge response", enabledBody, StringComparison.Ordinal);
		Assert.DoesNotContain("MCP knowledge response", disabledBody, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChatWithKnowledgeMcpFailureFallsBackWithoutErrorEvent()
	{
		var failingFunctions = CreateConfiguredChatFunctions(
			knowledgeMcpEnabled: true,
			new FailingMcpToolBroker());

		var requestBody = "{\"message\":\"hello there\",\"sessionId\":\"session-func-config-mcp-failure\"}";
		var response = await failingFunctions.Chat(CreateRequest(requestBody));
		var body = await ReadBodyAsStringAsync(response);

		Assert.Equal(["thinking", "thinking", "result"], ExtractEventTypes(body));
		Assert.DoesNotContain("event: error", body, StringComparison.Ordinal);
		Assert.DoesNotContain("MCP knowledge response", body, StringComparison.Ordinal);
		Assert.Contains("internal-kb/runtime/general-illustration-guidance", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChatWithKnowledgeMcpEnabledMasksSensitiveMcpOutput()
	{
		var functions = CreateConfiguredChatFunctions(
			knowledgeMcpEnabled: true,
			new StaticMcpToolBroker("secret contact user@example.com call +1 555 444 3322"));

		var requestBody = "{\"message\":\"what is participating policy\",\"sessionId\":\"session-func-config-mcp-mask\"}";
		var response = await functions.Chat(CreateRequest(requestBody));
		var body = await ReadBodyAsStringAsync(response);

		Assert.Equal(["thinking", "thinking", "result"], ExtractEventTypes(body));
		Assert.Contains("[redacted]", body, StringComparison.Ordinal);
		Assert.Contains("[redacted-email]", body, StringComparison.Ordinal);
		Assert.Contains("[redacted-phone]", body, StringComparison.Ordinal);
		Assert.DoesNotContain("user@example.com", body, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("secret contact", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ChatWithKnowledgeMalformedMcpPayloadFallsBackWithoutErrorEvent()
	{
		var functions = CreateConfiguredChatFunctions(
			knowledgeMcpEnabled: true,
			new MalformedPayloadMcpToolBroker());

		var requestBody = "{\"message\":\"what is participating policy\",\"sessionId\":\"session-func-config-mcp-malformed\"}";
		var response = await functions.Chat(CreateRequest(requestBody));
		var body = await ReadBodyAsStringAsync(response);

		Assert.Equal(["thinking", "thinking", "result"], ExtractEventTypes(body));
		Assert.DoesNotContain("event: error", body, StringComparison.Ordinal);
		Assert.Contains("\"type\":\"result\"", body, StringComparison.Ordinal);
		Assert.DoesNotContain("invalid_payload", body, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("response JSON was malformed", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ChatWithConfigDrivenDecisioningMcpTogglePreservesSseEventSequence()
	{
		var disabledFunctions = CreateConfiguredIllustrationChatFunctions(
			decisioningMcpEnabled: false,
			new ThrowingMcpToolBroker());
		var enabledFunctions = CreateConfiguredIllustrationChatFunctions(
			decisioningMcpEnabled: true,
			new StaticMcpToolBroker(BuildMcpDecisioningTableJson()));

		var requestBody = "{\"message\":\"male age 45 non-smoker budget $100k\",\"sessionId\":\"session-func-config-decisioning-mcp\"}";
		var disabledResponse = await disabledFunctions.Chat(CreateRequest(requestBody));
		var enabledResponse = await enabledFunctions.Chat(CreateRequest(requestBody));

		var disabledBody = await ReadBodyAsStringAsync(disabledResponse);
		var enabledBody = await ReadBodyAsStringAsync(enabledResponse);

		Assert.Equal(ExtractEventTypes(disabledBody), ExtractEventTypes(enabledBody));
		Assert.Equal(["thinking", "thinking", "progress", "result"], ExtractEventTypes(enabledBody));
		Assert.Contains("MCP decisioning scenario", enabledBody, StringComparison.Ordinal);
		Assert.DoesNotContain("MCP decisioning scenario", disabledBody, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChatWithDecisioningMcpFailureFallsBackWithoutErrorEvent()
	{
		var functions = CreateConfiguredIllustrationChatFunctions(
			decisioningMcpEnabled: true,
			new FailingMcpToolBroker());

		var requestBody = "{\"message\":\"male age 45 non-smoker budget $100k\",\"sessionId\":\"session-func-config-decisioning-mcp-failure\"}";
		var response = await functions.Chat(CreateRequest(requestBody));
		var body = await ReadBodyAsStringAsync(response);

		Assert.Equal(["thinking", "thinking", "progress", "result"], ExtractEventTypes(body));
		Assert.DoesNotContain("event: error", body, StringComparison.Ordinal);
		Assert.Contains("Maximize IRR at Life Expectancy", body, StringComparison.Ordinal);
		Assert.DoesNotContain("MCP decisioning scenario", body, StringComparison.Ordinal);
	}

	private static TestHttpRequestData CreateRequest(
		string bodyJson,
		string method = "POST",
		string url = "https://localhost/api/irt/chat")
	{
		var context = new TestFunctionContext();
		var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes(bodyJson));
		return new TestHttpRequestData(context, bodyStream, method, new Uri(url));
	}

	private static async Task<string> ReadBodyAsStringAsync(HttpResponseData response)
	{
		response.Body.Position = 0;
		using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
		return await reader.ReadToEndAsync();
	}

	private static List<string> ExtractEventTypes(string sseBody)
	{
		return sseBody
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Where(line => line.StartsWith("event: ", StringComparison.Ordinal))
			.Select(line => line[7..].Trim())
			.ToList();
	}

	private static ChatFunctions CreateConfiguredChatFunctions(bool knowledgeMcpEnabled, IMcpToolBroker mcpToolBroker)
	{
		var configurationValues = new Dictionary<string, string?>
		{
			["Misa:Mcp:Enabled"] = "true",
			["Misa:Mcp:BaseUrl"] = "http://unused-for-test",
			["Misa:Mcp:AllowedToolsByRoute:knowledge:0"] = "knowledge.answer",
			["Misa:Mcp:Knowledge:Enabled"] = knowledgeMcpEnabled ? "true" : "false",
			["Misa:Mcp:Knowledge:ToolName"] = "knowledge.answer"
		};
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(configurationValues)
			.Build();

		var services = new ServiceCollection();
		services.AddSingleton<IConfiguration>(configuration);
		services.AddLogging();
		services.AddMisaApplication();
		services.AddMisaInfrastructure(configuration);
		services.AddMisaKnowledge();
		services.AddSingleton<IAgentExecutionRuntime, KnowledgeOnlyExecutionRuntime>();
		services.AddSingleton<IMcpToolBroker>(mcpToolBroker);

		var serviceProvider = services.BuildServiceProvider();
		return new ChatFunctions(
			serviceProvider.GetRequiredService<IChatPipeline>(),
			serviceProvider.GetRequiredService<IChatSessionStore>(),
			NullLogger<ChatFunctions>.Instance);
	}

	private static ChatFunctions CreateConfiguredIllustrationChatFunctions(bool decisioningMcpEnabled, IMcpToolBroker mcpToolBroker)
	{
		var configurationValues = new Dictionary<string, string?>
		{
			["Misa:Mcp:Enabled"] = "true",
			["Misa:Mcp:BaseUrl"] = "http://unused-for-test",
			["Misa:Mcp:AllowedToolsByRoute:illustration:0"] = "decisioning.recommendation.table",
			["Misa:Mcp:Decisioning:Enabled"] = decisioningMcpEnabled ? "true" : "false",
			["Misa:Mcp:Decisioning:RecommendationTableToolName"] = "decisioning.recommendation.table"
		};
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(configurationValues)
			.Build();

		var services = new ServiceCollection();
		services.AddSingleton<IConfiguration>(configuration);
		services.AddLogging();
		services.AddMisaApplication();
		services.AddMisaInfrastructure(configuration);
		services.AddMisaDecisioning();
		services.AddSingleton<IAgentExecutionRuntime, IllustrationOnlyExecutionRuntime>();
		services.AddSingleton<IMcpToolBroker>(mcpToolBroker);

		var serviceProvider = services.BuildServiceProvider();
		return new ChatFunctions(
			serviceProvider.GetRequiredService<IChatPipeline>(),
			serviceProvider.GetRequiredService<IChatSessionStore>(),
			NullLogger<ChatFunctions>.Instance);
	}

	private static string BuildMcpDecisioningTableJson()
	{
		var table = new RecommendationTable(
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

		return JsonSerializer.Serialize(table);
	}

	private sealed class StaticPipeline : IChatPipeline
	{
		private readonly IReadOnlyList<ChatEventEnvelope> _events;

		public StaticPipeline(IReadOnlyList<ChatEventEnvelope> events)
		{
			_events = events;
		}

		public async IAsyncEnumerable<ChatEventEnvelope> RunAsync(
			ChatRequestDto request,
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			foreach (var evt in _events)
			{
				await Task.Yield();
				yield return evt;
			}
		}
	}

	private sealed class CountingPipeline : IChatPipeline
	{
		public int CallCount { get; private set; }

		public async IAsyncEnumerable<ChatEventEnvelope> RunAsync(
			ChatRequestDto request,
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			CallCount++;
			await Task.Yield();
			yield return ChatEventEnvelope.Text(ChatEventType.Result, "Should not execute for invalid payload.");
		}
	}

	private sealed class KnowledgeOnlyExecutionRuntime : IAgentExecutionRuntime
	{
		private readonly IKnowledgeService _knowledgeService;

		public KnowledgeOnlyExecutionRuntime(IKnowledgeService knowledgeService)
		{
			_knowledgeService = knowledgeService;
		}

		public async IAsyncEnumerable<ChatEventEnvelope> ExecuteAsync(
			ChatRequestDto request,
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
		{
			yield return ChatEventEnvelope.Text(ChatEventType.Thinking, "Looking this up in the knowledge base...");
			var answer = await _knowledgeService.AnswerAsync(request, cancellationToken).ConfigureAwait(false);
			yield return ChatEventEnvelope.Text(ChatEventType.Result, answer);
		}
	}

	private sealed class IllustrationOnlyExecutionRuntime : IAgentExecutionRuntime
	{
		private readonly IDecisioningService _decisioningService;

		public IllustrationOnlyExecutionRuntime(IDecisioningService decisioningService)
		{
			_decisioningService = decisioningService;
		}

		public async IAsyncEnumerable<ChatEventEnvelope> ExecuteAsync(
			ChatRequestDto request,
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
		{
			yield return ChatEventEnvelope.Text(
				ChatEventType.Thinking,
				"The user is asking me to help find the best policy for their client. Intention category: **Illustration**.");
			yield return ChatEventEnvelope.Text(ChatEventType.Progress, "Sending illustration calculations...");

			var recommendation = await _decisioningService
				.BuildRecommendationTableAsync(request, cancellationToken)
				.ConfigureAwait(false);

			yield return ChatEventEnvelope.Text(ChatEventType.Result, recommendation.ScenarioDescription);
		}
	}

	private sealed class StaticMcpToolBroker : IMcpToolBroker
	{
		private readonly string _content;

		public StaticMcpToolBroker(string content)
		{
			_content = content;
		}

		public Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(McpToolCallResult.Succeeded(_content, TimeSpan.FromMilliseconds(5)));
		}
	}

	private sealed class ThrowingMcpToolBroker : IMcpToolBroker
	{
		public Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
		{
			throw new InvalidOperationException("MCP broker should not be called when knowledge MCP is disabled.");
		}
	}

	private sealed class FailingMcpToolBroker : IMcpToolBroker
	{
		public Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(McpToolCallResult.Failed("timeout", "simulated timeout", TimeSpan.FromMilliseconds(200)));
		}
	}

	private sealed class MalformedPayloadMcpToolBroker : IMcpToolBroker
	{
		public Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(McpToolCallResult.Failed("invalid_payload", "response JSON was malformed", TimeSpan.FromMilliseconds(120)));
		}
	}

	private sealed class TestFunctionContext : FunctionContext
	{
		private readonly CancellationToken _cancellationToken;

		public TestFunctionContext(CancellationToken cancellationToken = default)
		{
			_cancellationToken = cancellationToken;
			Items = new Dictionary<object, object>();
		}

		public override string InvocationId => "test-invocation";

		public override string FunctionId => "test-function";

		public override TraceContext TraceContext => null!;

		public override BindingContext BindingContext => null!;

		public override RetryContext RetryContext => null!;

		public override IServiceProvider InstanceServices { get; set; } = null!;

		public override FunctionDefinition FunctionDefinition => null!;

		public override IDictionary<object, object> Items { get; set; }

		public override IInvocationFeatures Features => null!;

		public override CancellationToken CancellationToken => _cancellationToken;
	}

	private sealed class TestHttpRequestData : HttpRequestData
	{
		private readonly Stream _body;
		private readonly string _method;
		private readonly Uri _url;

		public TestHttpRequestData(FunctionContext functionContext, Stream body, string method, Uri url)
			: base(functionContext)
		{
			_body = body;
			_method = method;
			_url = url;
			Headers = new HttpHeadersCollection();
		}

		public override Stream Body => _body;

		public override HttpHeadersCollection Headers { get; }

		public override IReadOnlyCollection<IHttpCookie> Cookies => Array.Empty<IHttpCookie>();

		public override Uri Url => _url;

		public override IEnumerable<ClaimsIdentity> Identities => Array.Empty<ClaimsIdentity>();

		public override string Method => _method;

		public override HttpResponseData CreateResponse()
		{
			return new TestHttpResponseData(FunctionContext);
		}
	}

	private sealed class TestHttpResponseData : HttpResponseData
	{
		public TestHttpResponseData(FunctionContext functionContext)
			: base(functionContext)
		{
			Headers = new HttpHeadersCollection();
			Body = new MemoryStream();
		}

		public override HttpStatusCode StatusCode { get; set; }

		public override HttpHeadersCollection Headers { get; set; }

		public override Stream Body { get; set; }

		public override HttpCookies Cookies => null!;
	}
}