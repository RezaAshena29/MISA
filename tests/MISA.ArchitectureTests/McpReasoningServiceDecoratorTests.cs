using MISA.Application;
using MISA.Contracts;
using MISA.Reasoning;
using Microsoft.Extensions.Options;

namespace MISA.ArchitectureTests;

public sealed class McpReasoningServiceDecoratorTests
{
	[Fact]
	public async Task DisabledFlagUsesInnerReasoningWithoutCallingMcp()
	{
		var decorator = new McpReasoningServiceDecorator(
			new ReasoningService(new StaticKnowledgeService()),
			new ThrowingMcpToolBroker(),
			Options.Create(new ReasoningMcpOptions
			{
				Enabled = false,
				ToolName = "reasoning.mcp"
			}));

		var response = await decorator.BuildReasoningAsync(
			new ChatRequestDto("why this option", "reasoning-mcp-disabled"),
			new ChatSessionState("reasoning-mcp-disabled", LastRoute: "illustration", LastRecommendation: "Pay 90 is optimal."),
			CancellationToken.None);

		Assert.Contains("Pay 90 is optimal.", response, StringComparison.Ordinal);
		Assert.Contains("Knowledge reference for reasoning decorator test.", response, StringComparison.Ordinal);
	}

	[Fact]
	public async Task EnabledFlagUsesMcpResultWhenSuccessful()
	{
		var broker = new StubMcpToolBroker(request =>
		{
			Assert.Equal("reasoning", request.Route);
			Assert.Equal("reasoning.mcp", request.ToolName);
			return Task.FromResult(McpToolCallResult.Succeeded("mcp reasoning content", TimeSpan.FromMilliseconds(9)));
		});

		var decorator = new McpReasoningServiceDecorator(
			new ReasoningService(new StaticKnowledgeService()),
			broker,
			Options.Create(new ReasoningMcpOptions
			{
				Enabled = true,
				ToolName = "reasoning.mcp"
			}));

		var response = await decorator.BuildReasoningAsync(
			new ChatRequestDto("why this option", "reasoning-mcp-enabled"),
			new ChatSessionState("reasoning-mcp-enabled", LastRoute: "illustration", LastRecommendation: "Pay 90 is optimal."),
			CancellationToken.None);

		Assert.Equal("mcp reasoning content", response);
	}

	[Fact]
	public async Task EnabledFlagFallsBackWhenMcpFails()
	{
		var decorator = new McpReasoningServiceDecorator(
			new ReasoningService(new StaticKnowledgeService()),
			new StubMcpToolBroker(_ =>
				Task.FromResult(McpToolCallResult.Failed("timeout", "timed out", TimeSpan.FromMilliseconds(200)))),
			Options.Create(new ReasoningMcpOptions
			{
				Enabled = true,
				ToolName = "reasoning.mcp"
			}));

		var response = await decorator.BuildReasoningAsync(
			new ChatRequestDto("why this option", "reasoning-mcp-failed"),
			null,
			CancellationToken.None);

		Assert.Contains("I do not have a prior recommendation", response, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Knowledge reference for reasoning decorator test.", response, StringComparison.Ordinal);
	}

	private sealed class StaticKnowledgeService : IKnowledgeService
	{
		public Task<string> AnswerAsync(ChatRequestDto request, CancellationToken cancellationToken)
		{
			return Task.FromResult("Knowledge reference for reasoning decorator test.");
		}
	}

	private sealed class StubMcpToolBroker : IMcpToolBroker
	{
		private readonly Func<McpToolCallRequest, Task<McpToolCallResult>> _handler;

		public StubMcpToolBroker(Func<McpToolCallRequest, Task<McpToolCallResult>> handler)
		{
			_handler = handler;
		}

		public Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
		{
			return _handler(request);
		}
	}

	private sealed class ThrowingMcpToolBroker : IMcpToolBroker
	{
		public Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
		{
			throw new InvalidOperationException("MCP broker must not be called when Reasoning MCP is disabled.");
		}
	}
}
