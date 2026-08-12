using System.Net;
using System.Text;
using MISA.Application;
using MISA.Infrastructure;
using Microsoft.Extensions.Options;

namespace MISA.ArchitectureTests;

public sealed class RemoteHttpMcpToolBrokerTests
{
	[Fact]
	public async Task InvokeAsyncSuccessfulResponseReturnsSanitizedContent()
	{
		var broker = CreateBroker((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(
				"{\"success\":true,\"content\":\"secret contact user@example.com\"}",
				Encoding.UTF8,
				"application/json")
		}));

		var result = await broker.InvokeAsync(CreateRequest(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.NotNull(result.Content);
		Assert.Contains("[redacted]", result.Content, StringComparison.Ordinal);
		Assert.Contains("[redacted-email]", result.Content, StringComparison.Ordinal);
	}

	[Fact]
	public async Task InvokeAsyncForbiddenResponseReturnsToolForbidden()
	{
		var broker = CreateBroker((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));

		var result = await broker.InvokeAsync(CreateRequest(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("tool_forbidden", result.ErrorCode);
	}

	[Fact]
	public async Task InvokeAsyncMalformedJsonReturnsInvalidPayload()
	{
		var broker = CreateBroker((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("{invalid", Encoding.UTF8, "application/json")
		}));

		var result = await broker.InvokeAsync(CreateRequest(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("invalid_payload", result.ErrorCode);
	}

	[Fact]
	public async Task InvokeAsyncTimeoutReturnsTimeoutError()
	{
		var broker = CreateBroker(
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

		var result = await broker.InvokeAsync(CreateRequest(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("timeout", result.ErrorCode);
	}

	[Fact]
	public async Task InvokeAsyncDisallowedToolReturnsNotAllowedWithoutCallingTransport()
	{
		var callCount = 0;
		var broker = CreateBroker(
			(_, _) =>
			{
				callCount++;
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
			},
			new McpOptions
			{
				Enabled = true,
				BaseUrl = "http://mcp.local",
				AllowedToolsByRoute = new Dictionary<string, string[]>
				{
					["knowledge"] = ["different.tool"]
				}
			});

		var result = await broker.InvokeAsync(CreateRequest(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("tool_not_allowed", result.ErrorCode);
		Assert.Equal(0, callCount);
	}

	private static McpToolCallRequest CreateRequest()
	{
		return new McpToolCallRequest(
			Route: "knowledge",
			ToolName: "knowledge.answer",
			SessionId: "session-1",
			Input: "what is par");
	}

	private static RemoteHttpMcpToolBroker CreateBroker(
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
		var responseGuard = new DefaultResponseGuard(Options.Create(new GuardOptions
		{
			BlockedResponseTerms = ["secret"],
			MaskToken = "[redacted]",
			MaskEmails = true,
			MaskPhoneNumbers = false
		}));

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
