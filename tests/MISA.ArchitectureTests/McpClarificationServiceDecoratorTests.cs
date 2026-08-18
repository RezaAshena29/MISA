using MISA.Application;
using MISA.Clarification;
using MISA.Contracts;
using Microsoft.Extensions.Options;

namespace MISA.ArchitectureTests;

public sealed class McpClarificationServiceDecoratorTests
{
	[Fact]
	public async Task DisabledFlagUsesInnerClarificationWithoutCallingMcp()
	{
		var decorator = new McpClarificationServiceDecorator(
			new ClarificationService(),
			new ThrowingMcpToolBroker(),
			Options.Create(new ClarificationMcpOptions
			{
				Enabled = false,
				ToolName = "clarification.mcp"
			}));

		var response = await decorator.BuildClarificationPromptAsync(
			new ChatRequestDto("need guidance", "clarification-mcp-disabled"),
			null,
			CancellationToken.None);

		Assert.Contains("I need a bit more to run this case", response, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task EnabledFlagUsesMcpResultWhenSuccessful()
	{
		var broker = new StubMcpToolBroker(request =>
		{
			Assert.Equal("clarification", request.Route);
			Assert.Equal("clarification.mcp", request.ToolName);
			return Task.FromResult(McpToolCallResult.Succeeded("mcp clarification prompt", TimeSpan.FromMilliseconds(7)));
		});

		var decorator = new McpClarificationServiceDecorator(
			new ClarificationService(),
			broker,
			Options.Create(new ClarificationMcpOptions
			{
				Enabled = true,
				ToolName = "clarification.mcp"
			}));

		var response = await decorator.BuildClarificationPromptAsync(
			new ChatRequestDto("need guidance", "clarification-mcp-enabled"),
			null,
			CancellationToken.None);

		Assert.Equal("mcp clarification prompt", response);
	}

	[Fact]
	public async Task EnabledFlagFallsBackWhenMcpFails()
	{
		var decorator = new McpClarificationServiceDecorator(
			new ClarificationService(),
			new StubMcpToolBroker(_ =>
				Task.FromResult(McpToolCallResult.Failed("timeout", "timed out", TimeSpan.FromMilliseconds(200)))),
			Options.Create(new ClarificationMcpOptions
			{
				Enabled = true,
				ToolName = "clarification.mcp"
			}));

		var response = await decorator.BuildClarificationPromptAsync(
			new ChatRequestDto("need guidance", "clarification-mcp-failed"),
			null,
			CancellationToken.None);

		Assert.Contains("I need a bit more to run this case", response, StringComparison.OrdinalIgnoreCase);
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
			throw new InvalidOperationException("MCP broker must not be called when Clarification MCP is disabled.");
		}
	}
}
