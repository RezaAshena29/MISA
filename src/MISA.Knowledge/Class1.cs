using MISA.Application;
using MISA.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MISA.Knowledge;

/// <summary>
/// Deterministic knowledge service for explanation and grounding responses.
/// </summary>
public sealed class KnowledgeService : IKnowledgeService
{
	private static readonly ActivitySource ActivitySource = new("MISA.Knowledge");
	private static readonly Meter Meter = new("MISA.Knowledge");
	private static readonly Counter<long> LookupCounter = Meter.CreateCounter<long>("misa.knowledge.lookups");

	private static readonly KnowledgeEntry[] Entries =
	[
		new(
			"par-participating",
			["par", "participating", "dividend", "whole life"],
			"Participating (Par) plans may receive dividends based on company surplus. Illustrations should explain that dividends are not guaranteed and scenario assumptions drive projected values.",
			"internal-kb/insurance/par-basics"),
		new(
			"smoking-underwriting",
			["smoker", "non-smoker", "nonsmoker", "underwriting"],
			"Smoking status materially affects premium rates and qualification. Recommendation comparisons should keep smoking status constant across all configurations for fair ranking.",
			"internal-kb/underwriting/smoking-factors"),
		new(
			"budget-guidance",
			["budget", "premium", "afford", "affordability"],
			"Budget should be evaluated against sustainable monthly or annual premium commitment. If premium stress is likely, prefer shorter premium duration or reduced sum assured.",
			"internal-kb/advice/budget-discipline"),
		new(
			"reasoning-explainability",
			["why", "reason", "explain", "how come"],
			"Reasoning responses should include: selected configuration, key eligibility assumptions, and constraints that ruled out alternatives.",
			"internal-kb/governance/recommendation-explainability")
	];

	private static readonly KnowledgeEntry DefaultEntry = new(
		"general-guidance",
		Array.Empty<string>(),
		"Use client profile factors (age, smoking status, and premium budget) to compare configurations consistently. Clarify missing inputs before returning final recommendations.",
		"internal-kb/runtime/general-illustration-guidance");

	/// <inheritdoc />
	public Task<string> AnswerAsync(ChatRequestDto request, CancellationToken cancellationToken)
	{
		using var activity = ActivitySource.StartActivity("knowledge.answer", ActivityKind.Internal);
		activity?.SetTag("session.id", request.SessionId);

		var message = request.Message.ToLowerInvariant();
		var selected = SelectBestEntry(message);

		activity?.SetTag("knowledge.topic", selected.Topic);
		LookupCounter.Add(1, new KeyValuePair<string, object?>("topic", selected.Topic));

		var response =
			$"{selected.Body}\n\n" +
			$"Source: {selected.Source}";

		return Task.FromResult(response);
	}

	private static KnowledgeEntry SelectBestEntry(string message)
	{
		var scored = Entries
			.Select(entry => new
			{
				Entry = entry,
				Score = entry.Keywords.Count(keyword => message.Contains(keyword, StringComparison.Ordinal))
			})
			.OrderByDescending(item => item.Score)
			.ThenBy(item => item.Entry.Topic, StringComparer.Ordinal)
			.First();

		return scored.Score > 0 ? scored.Entry : DefaultEntry;
	}

	private sealed record KnowledgeEntry(
		string Topic,
		IReadOnlyList<string> Keywords,
		string Body,
		string Source);
}

/// <summary>
/// Registers knowledge services.
/// </summary>
public static class KnowledgeServiceCollectionExtensions
{
	/// <summary>
	/// Adds knowledge module services.
	/// </summary>
	public static IServiceCollection AddMisaKnowledge(this IServiceCollection services)
	{
		services.AddSingleton<IKnowledgeService, KnowledgeService>();
		return services;
	}
}
