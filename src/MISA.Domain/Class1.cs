using System.Collections.ObjectModel;

namespace MISA.Domain;

/// <summary>
/// Scenario classification categories for route resolution.
/// </summary>
public enum ScenarioType
{
	Unknown = 0,
	Illustration = 1,
	Clarification = 2,
	Reasoning = 3,
	Knowledge = 4
}

/// <summary>
/// Client smoking status.
/// </summary>
public enum SmokingStatus
{
	Unknown = 0,
	Smoker = 1,
	NonSmoker = 2
}

/// <summary>
/// Snapshot of normalized client profile data.
/// </summary>
public sealed record ClientProfile(
	string ClientId,
	int Age,
	string Gender,
	SmokingStatus SmokingStatus);

/// <summary>
/// Classified scenario metadata and confidence.
/// </summary>
public sealed record ScenarioIntent(
	ScenarioType Type,
	double Confidence,
	string? ClarifyingQuestion = null);

/// <summary>
/// Compact, audit-ready rule and route trace.
/// </summary>
public sealed record DecisionTrace(
	string CaseName,
	string Leaf,
	IReadOnlyDictionary<string, string> Inputs,
	IReadOnlyList<string> Branches)
{
	/// <summary>
	/// Creates an empty trace object to avoid null checks in orchestration.
	/// </summary>
	public static DecisionTrace Empty(string caseName)
		=> new(
			caseName,
			Leaf: "unknown",
			Inputs: new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()),
			Branches: Array.Empty<string>());
}

/// <summary>
/// Stateful session context envelope.
/// </summary>
public sealed record ChatSessionContext(
	string SessionId,
	ClientProfile? Client = null,
	ScenarioIntent? Intent = null,
	DecisionTrace? Trace = null);
