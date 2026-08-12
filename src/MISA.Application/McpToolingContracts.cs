namespace MISA.Application;

/// <summary>
/// Transport-agnostic broker abstraction for MCP tool invocation.
/// </summary>
public interface IMcpToolBroker
{
	/// <summary>
	/// Invokes an MCP tool request and returns normalized result output.
	/// </summary>
	Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// MCP tool invocation request details.
/// </summary>
public sealed record McpToolCallRequest(
	string Route,
	string ToolName,
	string SessionId,
	string Input,
	IReadOnlyDictionary<string, string?>? Attributes = null,
	string? CorrelationId = null);

/// <summary>
/// Normalized MCP tool invocation result.
/// </summary>
public sealed record McpToolCallResult(
	bool Success,
	string? Content,
	string? ErrorCode = null,
	string? ErrorMessage = null,
	TimeSpan? Latency = null)
{
	/// <summary>
	/// Creates a successful result.
	/// </summary>
	public static McpToolCallResult Succeeded(string content, TimeSpan latency)
		=> new(true, content, null, null, latency);

	/// <summary>
	/// Creates a failed result.
	/// </summary>
	public static McpToolCallResult Failed(string errorCode, string? errorMessage, TimeSpan latency)
		=> new(false, null, errorCode, errorMessage, latency);
}
