using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MISA.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MISA.Infrastructure;

/// <summary>
/// MCP integration settings loaded from configuration.
/// </summary>
public sealed class McpOptions
{
	/// <summary>
	/// Enables MCP broker usage.
	/// </summary>
	public bool Enabled { get; set; }

	/// <summary>
	/// MCP server base URL.
	/// </summary>
	public string BaseUrl { get; set; } = "";

	/// <summary>
	/// Tool names allowed for each route.
	/// </summary>
	public Dictionary<string, string[]> AllowedToolsByRoute { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Default request timeout in milliseconds.
	/// </summary>
	public int DefaultTimeoutMs { get; set; } = 2500;

	/// <summary>
	/// Per-tool timeout overrides in milliseconds.
	/// </summary>
	public Dictionary<string, int> ToolTimeoutsMs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Policy guard for MCP tool calls.
/// </summary>
public sealed class McpPolicyGuard
{
	private readonly McpOptions _options;
	private readonly ConcurrentDictionary<string, HashSet<string>> _allowCache = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Creates MCP policy guard.
	/// </summary>
	public McpPolicyGuard(IOptions<McpOptions> options)
	{
		_options = options.Value;
	}

	/// <summary>
	/// Returns whether an MCP call is allowed.
	/// </summary>
	public bool IsAllowed(string route, string toolName)
	{
		if (!_options.Enabled)
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(route) || string.IsNullOrWhiteSpace(toolName))
		{
			return false;
		}

		var allowed = _allowCache.GetOrAdd(route, CreateAllowSet);
		return allowed.Contains(toolName);
	}

	/// <summary>
	/// Returns effective timeout for a tool call.
	/// </summary>
	public TimeSpan GetTimeout(string toolName)
	{
		if (_options.ToolTimeoutsMs.TryGetValue(toolName, out var timeoutMs) && timeoutMs > 0)
		{
			return TimeSpan.FromMilliseconds(timeoutMs);
		}

		return TimeSpan.FromMilliseconds(_options.DefaultTimeoutMs > 0 ? _options.DefaultTimeoutMs : 2500);
	}

	private HashSet<string> CreateAllowSet(string route)
	{
		if (!_options.AllowedToolsByRoute.TryGetValue(route, out var tools) || tools is null)
		{
			return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		}

		return new HashSet<string>(
			tools.Where(tool => !string.IsNullOrWhiteSpace(tool)),
			StringComparer.OrdinalIgnoreCase);
	}
}

/// <summary>
/// Remote MCP broker backed by HTTP.
/// </summary>
public sealed class RemoteHttpMcpToolBroker : IMcpToolBroker
{
	private static readonly ActivitySource ActivitySource = new("MISA.MCP.Adapter");
	private static readonly Meter Meter = new("MISA.MCP.Adapter");
	private static readonly Counter<long> InvocationCounter = Meter.CreateCounter<long>("misa.mcp.invocations");
	private static readonly Counter<long> FailureCounter = Meter.CreateCounter<long>("misa.mcp.failures");
	private static readonly Counter<long> DeniedCounter = Meter.CreateCounter<long>("misa.mcp.denied");
	private static readonly Histogram<double> DurationMs = Meter.CreateHistogram<double>("misa.mcp.duration.ms");

	private readonly IHttpClientFactory _httpClientFactory;
	private readonly McpPolicyGuard _policyGuard;
	private readonly IResponseGuard _responseGuard;
	private readonly IOptions<McpOptions> _options;

	/// <summary>
	/// Creates remote MCP broker.
	/// </summary>
	public RemoteHttpMcpToolBroker(
		IHttpClientFactory httpClientFactory,
		McpPolicyGuard policyGuard,
		IResponseGuard responseGuard,
		IOptions<McpOptions> options)
	{
		_httpClientFactory = httpClientFactory;
		_policyGuard = policyGuard;
		_responseGuard = responseGuard;
		_options = options;
	}

	/// <inheritdoc />
	public async Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
	{
		if (!_policyGuard.IsAllowed(request.Route, request.ToolName))
		{
			DeniedCounter.Add(1, CreateTags(request));
			return McpToolCallResult.Failed("tool_not_allowed", "MCP tool is not allowed for this route.", TimeSpan.Zero);
		}

		if (string.IsNullOrWhiteSpace(_options.Value.BaseUrl))
		{
			FailureCounter.Add(1, CreateTags(request));
			return McpToolCallResult.Failed("mcp_misconfigured", "MCP base URL is not configured.", TimeSpan.Zero);
		}

		using var activity = ActivitySource.StartActivity("mcp.invoke", ActivityKind.Client);
		activity?.SetTag("mcp.route", request.Route);
		activity?.SetTag("mcp.tool", request.ToolName);
		activity?.SetTag("session.id", request.SessionId);

		var tags = CreateTags(request);
		var sw = Stopwatch.StartNew();
		var timeout = _policyGuard.GetTimeout(request.ToolName);
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(timeout);

		try
		{
			var client = _httpClientFactory.CreateClient(McpServiceCollectionExtensions.McpClientName);
			var endpoint = BuildEndpoint(_options.Value.BaseUrl);
			var payload = new
			{
				route = request.Route,
				toolName = request.ToolName,
				sessionId = request.SessionId,
				input = request.Input,
				attributes = request.Attributes,
				correlationId = request.CorrelationId
			};

			using var response = await client.PostAsJsonAsync(endpoint, payload, cts.Token).ConfigureAwait(false);
			if (response.StatusCode == HttpStatusCode.Forbidden)
			{
				sw.Stop();
				FailureCounter.Add(1, tags);
				DurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
				return McpToolCallResult.Failed("tool_forbidden", "MCP server denied this tool call.", sw.Elapsed);
			}

			if (!response.IsSuccessStatusCode)
			{
				sw.Stop();
				FailureCounter.Add(1, tags);
				DurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
				return McpToolCallResult.Failed("tool_error", $"MCP server returned {(int)response.StatusCode}.", sw.Elapsed);
			}

			var resultPayload = await response.Content.ReadFromJsonAsync<McpServerResponse>(cancellationToken: cts.Token).ConfigureAwait(false);
			if (resultPayload is null)
			{
				sw.Stop();
				FailureCounter.Add(1, tags);
				DurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
				return McpToolCallResult.Failed("invalid_payload", "MCP server returned an empty payload.", sw.Elapsed);
			}

			sw.Stop();
			InvocationCounter.Add(1, tags);
			DurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);

			if (!resultPayload.Success)
			{
				FailureCounter.Add(1, tags);
				return McpToolCallResult.Failed(
					resultPayload.ErrorCode ?? "tool_error",
					resultPayload.ErrorMessage,
					sw.Elapsed);
			}

			var sanitized = _responseGuard.Sanitize(resultPayload.Content ?? string.Empty);
			return McpToolCallResult.Succeeded(sanitized, sw.Elapsed);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			sw.Stop();
			FailureCounter.Add(1, tags);
			DurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
			return McpToolCallResult.Failed("timeout", "MCP tool call timed out.", sw.Elapsed);
		}
		catch (HttpRequestException ex)
		{
			sw.Stop();
			FailureCounter.Add(1, tags);
			DurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
			return McpToolCallResult.Failed("transport_error", ex.Message, sw.Elapsed);
		}
		catch (JsonException ex)
		{
			sw.Stop();
			FailureCounter.Add(1, tags);
			DurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
			return McpToolCallResult.Failed("invalid_payload", ex.Message, sw.Elapsed);
		}
	}

	private static KeyValuePair<string, object?>[] CreateTags(McpToolCallRequest request)
	{
		return
		[
			new("route", request.Route),
			new("tool", request.ToolName)
		];
	}

	private static string BuildEndpoint(string baseUrl)
	{
		var normalized = baseUrl.EndsWith('/')
			? baseUrl[..^1]
			: baseUrl;
		return $"{normalized}/tools/invoke";
	}

	private sealed record McpServerResponse(bool Success, string? Content, string? ErrorCode, string? ErrorMessage);
}

/// <summary>
/// MCP service registrations.
/// </summary>
public static class McpServiceCollectionExtensions
{
	internal const string McpClientName = "misa-mcp";

	/// <summary>
	/// Adds MCP integration services.
	/// </summary>
	public static IServiceCollection AddMisaMcp(this IServiceCollection services, IConfiguration configuration)
	{
		services.AddOptions<McpOptions>().Bind(configuration.GetSection("Misa:Mcp"));
		services.AddSingleton<McpPolicyGuard>();
		services.AddHttpClient(McpClientName, client =>
		{
			client.Timeout = Timeout.InfiniteTimeSpan;
			client.DefaultRequestHeaders.Add("Accept", "application/json");
		});
		services.AddSingleton<IMcpToolBroker, RemoteHttpMcpToolBroker>();
		return services;
	}
}
