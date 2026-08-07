using MISA.Application;
using MISA.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MISA.Decisioning;

/// <summary>
/// Deterministic recommendation engine for illustration scenarios.
/// </summary>
public sealed class RuleBasedDecisioningService : IDecisioningService
{
	private static readonly ActivitySource ActivitySource = new("MISA.Decisioning");
	private static readonly Meter Meter = new("MISA.Decisioning");
	private static readonly Counter<long> RecommendationCounter = Meter.CreateCounter<long>("misa.decisioning.recommendations");

	private static readonly Regex AgePattern = new(
		@"\bage\s*(\d{1,2})\b|\b(\d{1,2})\s*(?:yo|years?|yrs?)\b",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

	private static readonly Regex TaggedBudgetPattern = new(
		@"\b(?:budget|premium)\s*(?:is|=|:)?\s*\$?\s*(\d+(?:\.\d+)?)\s*([kKmM]?)\b",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

	private static readonly Regex CurrencyBudgetPattern = new(
		@"\$\s*(\d+(?:\.\d+)?)\s*([kKmM]?)",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);

	private const decimal DefaultBudget = 100000m;

	/// <inheritdoc />
	public Task<string> BuildRecommendationAsync(ChatRequestDto request, CancellationToken cancellationToken)
	{
		using var activity = ActivitySource.StartActivity("decisioning.build_recommendation", ActivityKind.Internal);
		activity?.SetTag("session.id", request.SessionId);

		var profile = ExtractProfile(request.Message);
		var ranked = ScoreConfigurations(profile)
			.OrderByDescending(item => item.Score)
			.ThenBy(item => item.Configuration, StringComparer.Ordinal)
			.ToList();

		var top = ranked[0];
		var runnerUp = ranked[1];
		var budgetDisplay = profile.Budget.ToString("N0", CultureInfo.InvariantCulture);
		var rationale = BuildRationale(profile, top.Configuration);

		activity?.SetTag("decision.top_configuration", top.Configuration);
		activity?.SetTag("decision.top_score", top.Score);
		RecommendationCounter.Add(1, new KeyValuePair<string, object?>("configuration", top.Configuration));

		var recommendation =
			$"Top pick: {top.Configuration} (score {top.Score:0.0}). " +
			$"Runner-up: {runnerUp.Configuration} (score {runnerUp.Score:0.0}). " +
			$"Profile basis: age {(profile.Age?.ToString(CultureInfo.InvariantCulture) ?? "inferred")}, " +
			$"smoking {(profile.IsNonSmoker.HasValue ? (profile.IsNonSmoker.Value ? "non-smoker" : "smoker") : "inferred")}, " +
			$"budget ${budgetDisplay}. {rationale}";

		return Task.FromResult(recommendation);
	}

	private static DecisionInput ExtractProfile(string message)
	{
		var age = TryExtractAge(message);
		var isNonSmoker = TryExtractSmokingStatus(message);
		var budget = TryExtractBudget(message) ?? DefaultBudget;

		return new DecisionInput(age, isNonSmoker, budget);
	}

	private static int? TryExtractAge(string message)
	{
		var match = AgePattern.Match(message);
		if (!match.Success)
		{
			return null;
		}

		var valueText = match.Groups[1].Success
			? match.Groups[1].Value
			: match.Groups[2].Value;

		return int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var age)
			? age
			: null;
	}

	private static bool? TryExtractSmokingStatus(string message)
	{
		if (message.Contains("non-smoker", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("non smoker", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("nonsmoker", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (message.Contains("smoker", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return null;
	}

	private static decimal? TryExtractBudget(string message)
	{
		var tagged = TaggedBudgetPattern.Match(message);
		if (TryConvertBudgetMatch(tagged, out var taggedBudget))
		{
			return taggedBudget;
		}

		var currency = CurrencyBudgetPattern.Match(message);
		if (TryConvertBudgetMatch(currency, out var currencyBudget))
		{
			return currencyBudget;
		}

		return null;
	}

	private static bool TryConvertBudgetMatch(Match match, out decimal value)
	{
		value = default;
		if (!match.Success || match.Groups.Count < 3)
		{
			return false;
		}

		if (!decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var baseValue))
		{
			return false;
		}

		var multiplier = match.Groups[2].Value.ToLowerInvariant() switch
		{
			"k" => 1000m,
			"m" => 1000000m,
			_ => 1m
		};

		value = baseValue * multiplier;
		return value > 0;
	}

	private static IReadOnlyList<ConfigurationScore> ScoreConfigurations(DecisionInput input)
	{
		var normalizedAge = input.Age ?? 40;
		var smokingAdjustment = input.IsNonSmoker switch
		{
			true => 6d,
			false => -7d,
			null => 0d
		};

		var affordability = Math.Clamp(((double)input.Budget - 50000d) / 10000d, -5d, 8d);

		var pay90 = 68d + (affordability * 1.8d) + ((45d - normalizedAge) * 0.55d) + smokingAdjustment;
		var pay20 = 70d + (affordability * 0.8d) + ((50d - Math.Abs(normalizedAge - 42d)) * 0.15d) + (smokingAdjustment * 0.3d);
		var pay10 = 66d - (affordability * 0.6d) + ((55d - normalizedAge) * 0.25d) + (smokingAdjustment * 0.15d);

		return
		[
			new ConfigurationScore("Pay 90", pay90),
			new ConfigurationScore("Pay 20", pay20),
			new ConfigurationScore("Pay 10", pay10)
		];
	}

	private static string BuildRationale(DecisionInput input, string topConfiguration)
	{
		if (topConfiguration == "Pay 90")
		{
			return "Longer premium duration is favored because affordability and risk profile support stability.";
		}

		if (topConfiguration == "Pay 20")
		{
			return "Balanced premium duration is favored for mixed affordability and medium-term stability.";
		}

		if (input.Budget < 75000m)
		{
			return "Shorter premium duration is favored to preserve affordability under tighter budget constraints.";
		}

		return "Shorter premium duration is favored because available inputs suggest higher risk sensitivity.";
	}

	private sealed record DecisionInput(int? Age, bool? IsNonSmoker, decimal Budget);

	private sealed record ConfigurationScore(string Configuration, double Score);
}

/// <summary>
/// Registers decisioning services.
/// </summary>
public static class DecisioningServiceCollectionExtensions
{
	/// <summary>
	/// Adds decisioning services.
	/// </summary>
	public static IServiceCollection AddMisaDecisioning(this IServiceCollection services)
	{
		services.AddSingleton<IDecisioningService, RuleBasedDecisioningService>();
		return services;
	}
}
