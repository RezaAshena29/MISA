using MISA.Application;
using MISA.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MISA.Agents;

/// <summary>
/// MAF-aligned route resolver.
///
/// This implementation intentionally stays deterministic at bootstrap stage
/// while MAF workflow parity is built in later phases.
/// </summary>
public sealed partial class MafAgentRouter : IAgentRouter
{
	private static readonly string[] ClarificationHints = ["clarify", "missing", "need more", "what else"];
	private static readonly string[] KnowledgeHints = ["what is", "knowledge", "define", "explain concept", "tell me about"];
	private static readonly string[] ReasoningHints = ["why", "explain why", "how come", "reason"];

	private readonly IChatSessionStore _sessionStore;
	private readonly ILogger<MafAgentRouter> _logger;

	/// <summary>
	/// Creates a route resolver.
	/// </summary>
	public MafAgentRouter(IChatSessionStore sessionStore, ILogger<MafAgentRouter> logger)
	{
		_sessionStore = sessionStore;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<string> ResolveRouteAsync(ChatRequestDto request, CancellationToken cancellationToken)
	{
		var message = request.Message.Trim().ToLowerInvariant();
		var priorState = await _sessionStore.GetAsync(request.SessionId, cancellationToken).ConfigureAwait(false);

		var route = message switch
		{
			_ when ReasoningHints.Any(hint => message.Contains(hint, StringComparison.Ordinal)) => "reasoning",
			_ when KnowledgeHints.Any(hint => message.Contains(hint, StringComparison.Ordinal)) => "knowledge",
			_ when ClarificationHints.Any(hint => message.Contains(hint, StringComparison.Ordinal)) => "clarification",
			_ when message.Length <= 32 && string.Equals(priorState?.LastRoute, "illustration", StringComparison.OrdinalIgnoreCase) => "reasoning",
			_ => "illustration"
		};

		MafAgentRouterLog.RouteResolved(_logger, request.SessionId, route);
		return route;
	}

	private static partial class MafAgentRouterLog
	{
		[LoggerMessage(
			EventId = 2001,
			Level = LogLevel.Information,
			Message = "MAF route resolved. SessionId={SessionId} Route={Route}")]
		public static partial void RouteResolved(ILogger logger, string sessionId, string route);
	}
}

/// <summary>
/// Registers MAF-aligned agent services.
/// </summary>
public static class AgentsServiceCollectionExtensions
{
	/// <summary>
	/// Adds agent services.
	/// </summary>
	public static IServiceCollection AddMisaAgents(this IServiceCollection services)
	{
		services.AddSingleton<IAgentRouter, MafAgentRouter>();
		return services;
	}
}
