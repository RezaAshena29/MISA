using MISA.Application;
using MISA.Contracts;
using MISA.Knowledge;
using Microsoft.Extensions.Options;

namespace MISA.ArchitectureTests;

public sealed class McpKnowledgeServiceDecoratorTests
{
	[Fact]
	public async Task DisabledFlagUsesInnerKnowledgeService()
	{
		var inner = new KnowledgeService();
		var broker = new StubMcpToolBroker(_ =>
			Task.FromResult(McpToolCallResult.Succeeded("mcp", TimeSpan.FromMilliseconds(5))));
		var options = Options.Create(new KnowledgeMcpOptions
		{
			Enabled = false,
			ToolName = "knowledge.mcp"
		});
		var decorator = new McpKnowledgeServiceDecorator(inner, broker, options);

		var response = await decorator.AnswerAsync(new ChatRequestDto("hello there", "k-1"), CancellationToken.None);

		Assert.Contains("internal-kb/runtime/general-illustration-guidance", response, StringComparison.Ordinal);
	}

	[Fact]
	public async Task EnabledFlagUsesMcpResultWhenSuccessful()
	{
		var inner = new KnowledgeService();
		var broker = new StubMcpToolBroker(request =>
		{
			Assert.Equal("knowledge", request.Route);
			Assert.Equal("knowledge.mcp", request.ToolName);
			return Task.FromResult(McpToolCallResult.Succeeded("mcp knowledge content", TimeSpan.FromMilliseconds(8)));
		});
		var options = Options.Create(new KnowledgeMcpOptions
		{
			Enabled = true,
			ToolName = "knowledge.mcp"
		});
		var decorator = new McpKnowledgeServiceDecorator(inner, broker, options);

		var response = await decorator.AnswerAsync(new ChatRequestDto("what is par", "k-2"), CancellationToken.None);

		Assert.Equal("mcp knowledge content", response);
	}

	[Fact]
	public async Task EnabledFlagFallsBackWhenMcpFails()
	{
		var inner = new KnowledgeService();
		var broker = new StubMcpToolBroker(_ =>
			Task.FromResult(McpToolCallResult.Failed("timeout", "timed out", TimeSpan.FromMilliseconds(200))));
		var options = Options.Create(new KnowledgeMcpOptions
		{
			Enabled = true,
			ToolName = "knowledge.mcp"
		});
		var decorator = new McpKnowledgeServiceDecorator(inner, broker, options);

		var response = await decorator.AnswerAsync(new ChatRequestDto("hello there", "k-3"), CancellationToken.None);

		Assert.Contains("internal-kb/runtime/general-illustration-guidance", response, StringComparison.Ordinal);
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
}
