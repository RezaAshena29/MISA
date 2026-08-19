using MISA.Application;
using MISA.Contracts;
using MISA.Infrastructure;
using Microsoft.Extensions.Options;

namespace MISA.ArchitectureTests;

public sealed class McpCoordinatorTests
{
	[Fact]
	public async Task InvokeForRouteAsyncUsesMappedToolAndPropagatesContextAttributes()
	{
		var broker = new CapturingBroker(McpToolCallResult.Succeeded("ok", TimeSpan.FromMilliseconds(5)));
		var options = Options.Create(new McpCoordinatorOptions
		{
			ToolByRoute = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["knowledge"] = "knowledge.mcp"
			}
		});
		var coordinator = new McpCoordinator(broker, options);
		var request = new ChatRequestDto("what is participating policy", "session-1", Product: "par", Language: "en", ContextConsent: true);
		var priorState = new ChatSessionState("session-1", LastRoute: "illustration", LastRecommendation: "Pay 90 plan");

		var result = await coordinator.InvokeForRouteAsync(
			route: "knowledge",
			request: request,
			priorState: priorState,
			input: request.Message,
			additionalAttributes: new Dictionary<string, string?>
			{
				["custom"] = "value"
			},
			cancellationToken: CancellationToken.None);

		Assert.True(result.Success);
		Assert.NotNull(broker.LastRequest);
		Assert.Equal("knowledge", broker.LastRequest!.Route);
		Assert.Equal("knowledge.mcp", broker.LastRequest.ToolName);
		Assert.Equal("session-1", broker.LastRequest.SessionId);
		Assert.Equal("what is participating policy", broker.LastRequest.Input);
		Assert.NotNull(broker.LastRequest.Attributes);
		Assert.Equal("par", broker.LastRequest.Attributes!["product"]);
		Assert.Equal("en", broker.LastRequest.Attributes!["language"]);
		Assert.Equal("illustration", broker.LastRequest.Attributes!["lastRoute"]);
		Assert.Equal("Pay 90 plan", broker.LastRequest.Attributes!["lastRecommendation"]);
		Assert.Equal("true", broker.LastRequest.Attributes!["contextConsent"]);
		Assert.Equal("value", broker.LastRequest.Attributes!["custom"]);
	}

	[Fact]
	public async Task InvokeForRouteAsyncReturnsToolNotMappedWhenRouteMissingInConfiguration()
	{
		var broker = new CapturingBroker(McpToolCallResult.Succeeded("unused", TimeSpan.FromMilliseconds(2)));
		var coordinator = new McpCoordinator(
			broker,
			Options.Create(new McpCoordinatorOptions
			{
				ToolByRoute = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			}));

		var result = await coordinator.InvokeForRouteAsync(
			route: "reasoning",
			request: new ChatRequestDto("why", "session-2"),
			priorState: null,
			input: "why",
			additionalAttributes: null,
			cancellationToken: CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("tool_not_mapped", result.ErrorCode);
		Assert.Null(broker.LastRequest);
	}

	[Fact]
	public async Task InvokeForRouteAsyncReturnsInvalidRouteForBlankRoute()
	{
		var broker = new CapturingBroker(McpToolCallResult.Succeeded("unused", TimeSpan.FromMilliseconds(2)));
		var coordinator = new McpCoordinator(broker, Options.Create(new McpCoordinatorOptions()));

		var result = await coordinator.InvokeForRouteAsync(
			route: " ",
			request: new ChatRequestDto("what is par", "session-3"),
			priorState: null,
			input: "what is par",
			additionalAttributes: null,
			cancellationToken: CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("invalid_route", result.ErrorCode);
		Assert.Null(broker.LastRequest);
	}

	private sealed class CapturingBroker : IMcpToolBroker
	{
		private readonly McpToolCallResult _result;

		public CapturingBroker(McpToolCallResult result)
		{
			_result = result;
		}

		public McpToolCallRequest? LastRequest { get; private set; }

		public Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(_result);
		}
	}
}
