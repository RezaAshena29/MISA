using System.Net;
using System.Text;
using MISA.Application;
using MISA.Contracts;
using MISA.Infrastructure;
using MISA.Knowledge;
using Microsoft.Extensions.Options;

namespace MISA.ArchitectureTests;

public sealed class McpKnowledgeIntegrationTests
{
	[Fact]
	public async Task KnowledgeDecoratorUsesRemoteMcpWhenBrokerSucceeds()
	{
		var broker = CreateRemoteBroker((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("{\"success\":true,\"content\":\"mcp answer\"}", Encoding.UTF8, "application/json")
		}));
		var decorator = new McpKnowledgeServiceDecorator(
			new KnowledgeService(),
			broker,
			Options.Create(new KnowledgeMcpOptions
			{
				Enabled = true,
				ToolName = "knowledge.answer"
			}));

		var response = await decorator.AnswerAsync(new ChatRequestDto("what is par", "k-int-1"), CancellationToken.None);

		Assert.Equal("mcp answer", response);
	}

	[Fact]
	public async Task KnowledgeDecoratorFallsBackWhenRemoteMcpTimesOut()
	{
		var broker = CreateRemoteBroker(
			async (_, cancellationToken) =>
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return new HttpResponseMessage(HttpStatusCode.OK);
			},
			new McpOptions
			{
				Enabled = true,
				BaseUrl = "http://mcp.local",
				DefaultTimeoutMs = 20,
				AllowedToolsByRoute = new Dictionary<string, string[]>
				{
					["knowledge"] = ["knowledge.answer"]
				}
			});
		var decorator = new McpKnowledgeServiceDecorator(
			new KnowledgeService(),
			broker,
			Options.Create(new KnowledgeMcpOptions
			{
				Enabled = true,
				ToolName = "knowledge.answer"
			}));

		var response = await decorator.AnswerAsync(new ChatRequestDto("hello there", "k-int-2"), CancellationToken.None);

		Assert.Contains("internal-kb/runtime/general-illustration-guidance", response, StringComparison.Ordinal);
	}

	private static RemoteHttpMcpToolBroker CreateRemoteBroker(
		Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
		McpOptions? options = null)
	{
		var mcpOptions = options ?? new McpOptions
		{
			Enabled = true,
			BaseUrl = "http://mcp.local",
			DefaultTimeoutMs = 500,
			AllowedToolsByRoute = new Dictionary<string, string[]>
			{
				["knowledge"] = ["knowledge.answer"]
			}
		};

		var client = new HttpClient(new StubHttpMessageHandler(sendAsync));
		var clientFactory = new StubHttpClientFactory(client);
		var policyGuard = new McpPolicyGuard(Options.Create(mcpOptions));
		var responseGuard = new DefaultResponseGuard(Options.Create(new GuardOptions()));

		return new RemoteHttpMcpToolBroker(
			clientFactory,
			policyGuard,
			responseGuard,
			Options.Create(mcpOptions));
	}

	private sealed class StubHttpClientFactory : IHttpClientFactory
	{
		private readonly HttpClient _client;

		public StubHttpClientFactory(HttpClient client)
		{
			_client = client;
		}

		public HttpClient CreateClient(string name)
		{
			return _client;
		}
	}

	private sealed class StubHttpMessageHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;

		public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
		{
			_sendAsync = sendAsync;
		}

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return _sendAsync(request, cancellationToken);
		}
	}
}
