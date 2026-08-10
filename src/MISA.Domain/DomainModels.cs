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

/// <summary>
/// Canonical agent names used by the MISA agentic orchestration lifecycle.
/// </summary>
public static class AgentNames
{
	public const string OrchestratorAgent = "Orchestrator Agent";
	public const string IntentAnalyzerAgent = "Intent Analyzer Agent";
	public const string ContextMemoryAgent = "Context Memory Agent";
	public const string ClarifierAgent = "Clarifier Agent";
	public const string IllustrationPlannerAgent = "Illustration Planner Agent";
	public const string ValidationGuardAgent = "Validation Guard Agent";
	public const string FanoutDispatcherAgent = "Fanout Dispatcher Agent";
	public const string CalcWorkerPoolAgent = "Calc Worker Pool Agent";
	public const string FaninAggregatorAgent = "Fanin Aggregator Agent";
	public const string DecisionRankerAgent = "Decision Ranker Agent";
	public const string ResponseComposerAgent = "Response Composer Agent";
}
