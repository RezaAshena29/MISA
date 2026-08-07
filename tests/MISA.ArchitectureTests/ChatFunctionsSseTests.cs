using System.Net;
using System.Security.Claims;
using System.Text;
using MISA.Application;
using MISA.Contracts;
using MISA.Functions;
using MISA.Infrastructure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
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