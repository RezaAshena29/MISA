using System.Text.Json;
using System.Text.Json.Serialization;

namespace MISA.Contracts;

/// <summary>
/// Contract constants preserved for backward compatibility.
/// </summary>
public static class ChatContracts
{
	/// <summary>
	/// Existing gateway and UI chat endpoint route.
	/// </summary>
	public const string ChatRoute = "irt/chat";

	/// <summary>
	/// Existing endpoint for clearing server-side chat context.
	/// </summary>
	public const string ChatSessionRoute = "irt/chat/session/{sessionId}";

	/// <summary>
	/// Existing gateway and UI health endpoint route.
	/// </summary>
	public const string HealthRoute = "irt/health";
}

/// <summary>
/// Event types emitted through SSE.
/// </summary>
public enum ChatEventType
{
	Thinking = 0,
	Progress = 1,
	Assumptions = 2,
	Prevalidation = 3,
	Clarification = 4,
	Question = 5,
	Result = 6,
	Columns = 7,
	Error = 8
}

/// <summary>
/// Chat request payload accepted by /api/irt/chat.
/// </summary>
public sealed record ChatRequestDto(
	string Message,
	string SessionId,
	string? Product = null,
	string? Language = null,
	bool ContextConsent = false,
	JsonElement? UdmContext = null);

/// <summary>
/// Generic SSE data frame payload.
/// </summary>
public sealed record ChatEventEnvelope(ChatEventType Type, object Content)
{
	/// <summary>
	/// Wire-level event name expected by existing UI and tooling.
	/// </summary>
	[JsonIgnore]
	public string WireType => Type switch
	{
		ChatEventType.Thinking => "thinking",
		ChatEventType.Progress => "progress",
		ChatEventType.Assumptions => "assumptions",
		ChatEventType.Prevalidation => "prevalidation",
		ChatEventType.Clarification => "clarification",
		ChatEventType.Question => "question",
		ChatEventType.Result => "result",
		ChatEventType.Columns => "columns",
		ChatEventType.Error => "error",
		_ => "message"
	};

	/// <summary>
	/// Creates an event containing text content.
	/// </summary>
	public static ChatEventEnvelope Text(ChatEventType type, string content) => new(type, content);
}

/// <summary>
/// Contract payload for per-column UDM patch events.
/// </summary>
public sealed record ColumnsEventPayload(
	[property: JsonPropertyName("version")] string Version,
	[property: JsonPropertyName("session_id")] string SessionId,
	[property: JsonPropertyName("columns")] IReadOnlyList<UdmPatchColumnDto> Columns,
	[property: JsonPropertyName("irt_udm")] object? IrtUdm,
	[property: JsonPropertyName("irt_calc_response")] object? IrtCalcResponse,
	[property: JsonPropertyName("irt_metrics")] object? IrtMetrics);

/// <summary>
/// DTO for a single recommendation column patch set.
/// </summary>
public sealed record UdmPatchColumnDto(
	[property: JsonPropertyName("label")] string Label,
	[property: JsonPropertyName("operations")] IReadOnlyList<UdmPatchOperationDto> Operations);

/// <summary>
/// DTO for one UDM patch operation.
/// </summary>
public sealed record UdmPatchOperationDto(
	[property: JsonPropertyName("op")] string Op,
	[property: JsonPropertyName("path")] string Path,
	[property: JsonPropertyName("value")] object? Value);
