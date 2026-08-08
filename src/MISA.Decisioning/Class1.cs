using MISA.Application;
using MISA.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
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
		@"\bage\s*(\d{1,2})\b|\b(\d{1,2})\s*(?:yo|years?|yrs?)\b|\b(?:client|insured|applicant)\s*(?:is|:)\s*(\d{1,2})\b|\b(\d{1,2})\s*,\s*(?:male|female|man|woman)\b",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

	private static readonly Regex TaggedBudgetPattern = new(
		@"\b(?:budget|premium)\s*(?:is|=|:)?\s*\$?\s*(\d+(?:\.\d+)?)\s*([kKmM]?)\b",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

	private static readonly Regex CurrencyBudgetPattern = new(
		@"\$\s*(\d+(?:\.\d+)?)\s*([kKmM]?)",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);

	private static readonly Regex OffsetYearPattern = new(
		@"\boffset\s*(?:starting\s*)?(?:in\s*)?(?:year\s*)?(\d{1,2})\b",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

	private const decimal DefaultBudget = 100000m;
	private const decimal BaselineBudget = 50000m;

	private static readonly RecommendationTemplate[] Templates =
	[
		new(
			Id: "pay20-25do",
			Label: "Pay 20, 25% DO",
			BaseCoverageAmount: 1090000m,
			BaseAnnualPremium: 38111m,
			DepositOptionPayment: 12500m,
			CashValueYear10: 589770m,
			CashValueYear5: 242450m,
			CashValueYear20: 1550830m,
			CvEfficiencyYear10: 116.5m,
			IrrOnCsvYear10: 8.1m,
			DeathBenefitAtLeCurrent: 9398296m,
			IrrAtLeCurrent: 5.72m,
			DeathBenefitAtLeMinus2: 4892321m,
			IrrAtLeMinus2: 3.86m,
			QuickPayCurrent: 9,
			QuickPayMinus2: 12,
			ExtendedPaymentsForStress: 40),
		new(
			Id: "pay20-lvlmax",
			Label: "Pay 20, Lvl Max DO",
			BaseCoverageAmount: 910000m,
			BaseAnnualPremium: 32126m,
			DepositOptionPayment: 18218m,
			CashValueYear10: 593007m,
			CashValueYear5: 250120m,
			CashValueYear20: 1602520m,
			CvEfficiencyYear10: 117.8m,
			IrrOnCsvYear10: 8.3m,
			DeathBenefitAtLeCurrent: 9770800m,
			IrrAtLeCurrent: 5.74m,
			DeathBenefitAtLeMinus2: 5126139m,
			IrrAtLeMinus2: 3.85m,
			QuickPayCurrent: 7,
			QuickPayMinus2: 10,
			ExtendedPaymentsForStress: 40),
		new(
			Id: "pay90-lvlmax",
			Label: "Pay 90, Lvl Max DO",
			BaseCoverageAmount: 1150000m,
			BaseAnnualPremium: 19311m,
			DepositOptionPayment: 30703m,
			CashValueYear10: 543959m,
			CashValueYear5: 228332m,
			CashValueYear20: 1498820m,
			CvEfficiencyYear10: 108.8m,
			IrrOnCsvYear10: 7.2m,
			DeathBenefitAtLeCurrent: 12447867m,
			IrrAtLeCurrent: 5.92m,
			DeathBenefitAtLeMinus2: 6948928m,
			IrrAtLeMinus2: 4.02m,
			QuickPayCurrent: 5,
			QuickPayMinus2: 6,
			ExtendedPaymentsForStress: 10),
		new(
			Id: "pay100-lvlmax",
			Label: "Pay 100, Lvl Max DO",
			BaseCoverageAmount: 1110000m,
			BaseAnnualPremium: 20037m,
			DepositOptionPayment: 29775m,
			CashValueYear10: 593193m,
			CashValueYear5: 256800m,
			CashValueYear20: 1639040m,
			CvEfficiencyYear10: 119.1m,
			IrrOnCsvYear10: 8.5m,
			DeathBenefitAtLeCurrent: 12049509m,
			IrrAtLeCurrent: 5.83m,
			DeathBenefitAtLeMinus2: 6725961m,
			IrrAtLeMinus2: 3.92m,
			QuickPayCurrent: 5,
			QuickPayMinus2: 7,
			ExtendedPaymentsForStress: null)
	];

	/// <inheritdoc />
	public Task<string> BuildRecommendationAsync(ChatRequestDto request, CancellationToken cancellationToken)
	{
		using var activity = ActivitySource.StartActivity("decisioning.build_recommendation", ActivityKind.Internal);
		activity?.SetTag("session.id", request.SessionId);

		var profile = ExtractProfile(request.Message);
		var table = BuildRecommendationTable(profile);
		var ranked = RankColumns(table.Columns, profile);
		var top = ranked[0];
		var runnerUp = ranked.Count > 1 ? ranked[1] : ranked[0];
		var budgetDisplay = profile.Budget.ToString("N0", CultureInfo.InvariantCulture);

		activity?.SetTag("decision.top_configuration", top.Label);
		activity?.SetTag("decision.top_primary_metric", GetPrimaryMetricDisplay(top, table.ScenarioType));
		RecommendationCounter.Add(1, new KeyValuePair<string, object?>("configuration", top.Label));

		var recommendation =
			$"Top pick: {top.Label} ({GetPrimaryMetricDisplay(top, table.ScenarioType)}). " +
			$"Runner-up: {runnerUp.Label} ({GetPrimaryMetricDisplay(runnerUp, table.ScenarioType)}). " +
			$"Profile basis: age {(profile.Age?.ToString(CultureInfo.InvariantCulture) ?? "inferred")}, " +
			$"smoking {(profile.IsNonSmoker.HasValue ? (profile.IsNonSmoker.Value ? "non-smoker" : "smoker") : "inferred")}, " +
			$"budget ${budgetDisplay}.";

		return Task.FromResult(recommendation);
	}

	/// <inheritdoc />
	public Task<RecommendationTable> BuildRecommendationTableAsync(ChatRequestDto request, CancellationToken cancellationToken)
	{
		using var activity = ActivitySource.StartActivity("decisioning.build_recommendation_table", ActivityKind.Internal);
		activity?.SetTag("session.id", request.SessionId);

		var profile = ExtractProfile(request.Message);
		var table = BuildRecommendationTable(profile);

		activity?.SetTag("decision.scenario", table.ScenarioType);
		activity?.SetTag("decision.column_count", table.Columns.Count);
		RecommendationCounter.Add(1, new KeyValuePair<string, object?>("scenario", table.ScenarioType));

		return Task.FromResult(table);
	}

	private static DecisionInput ExtractProfile(string message)
	{
		var age = TryExtractAge(message);
		var gender = TryExtractGender(message);
		var isNonSmoker = TryExtractSmokingStatus(message);
		var budget = TryExtractBudget(message) ?? DefaultBudget;
		var scenarioType = TryExtractScenarioType(message);
		var offsetYear = TryExtractOffsetYear(message);

		return new DecisionInput(age, gender, isNonSmoker, budget, scenarioType, offsetYear);
	}

	private static int? TryExtractAge(string message)
	{
		var match = AgePattern.Match(message);
		if (!match.Success)
		{
			return null;
		}

		var valueText = string.Empty;
		for (var i = 1; i < match.Groups.Count; i++)
		{
			if (match.Groups[i].Success)
			{
				valueText = match.Groups[i].Value;
				break;
			}
		}

		if (string.IsNullOrWhiteSpace(valueText))
		{
			return null;
		}

		return int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var age)
			? age
			: null;
	}

	private static string TryExtractGender(string message)
	{
		if (message.Contains("female", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("woman", StringComparison.OrdinalIgnoreCase))
		{
			return "Female";
		}

		if (message.Contains("male", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("man", StringComparison.OrdinalIgnoreCase))
		{
			return "Male";
		}

		return "Unknown";
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

	private static string TryExtractScenarioType(string message)
	{
		if (message.Contains("cash surrender", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("cash value", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("csv", StringComparison.OrdinalIgnoreCase))
		{
			return RecommendationScenarios.MaximizeEarlyCsv;
		}

		if (message.Contains("death benefit", StringComparison.OrdinalIgnoreCase))
		{
			return RecommendationScenarios.MaximizeDeathBenefit;
		}

		return RecommendationScenarios.MaximizeIrrAtLe;
	}

	private static int? TryExtractOffsetYear(string message)
	{
		var match = OffsetYearPattern.Match(message);
		if (!match.Success)
		{
			return null;
		}

		return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
			? year
			: null;
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

	private static RecommendationTable BuildRecommendationTable(DecisionInput input)
	{
		var lifeExpectancyAge = EstimateLifeExpectancyAge(input);
		var budgetScale = input.Budget / BaselineBudget;
		var irrAdjustment = BuildIrrAdjustment(input);
		var longevityScale = 1m + ((lifeExpectancyAge - 85m) * 0.015m);
		var cvScale = 1m + (irrAdjustment / 20m);

		var rawColumns = Templates
			.Select(template => BuildColumn(template, input, budgetScale, irrAdjustment, longevityScale, cvScale, lifeExpectancyAge))
			.ToList();

		var ranked = RankColumns(rawColumns, input);
		var recommendedId = ranked[0].Id;

		var finalized = rawColumns
			.Select(column =>
			{
				var isRecommended = string.Equals(column.Id, recommendedId, StringComparison.Ordinal);
				return column with
				{
					Recommended = isRecommended,
					Explain = isRecommended
						? "Apply (recommended)"
						: "Apply to scenario"
				};
			})
			.ToList();

		return new RecommendationTable(
			ScenarioDescription: BuildScenarioDescription(input),
			ScenarioType: input.ScenarioType,
			ClientSummary: BuildClientSummary(input),
			PremiumBudget: input.Budget,
			Columns: finalized);
	}

	private static RecommendationColumn BuildColumn(
		RecommendationTemplate template,
		DecisionInput input,
		decimal budgetScale,
		decimal irrAdjustment,
		decimal longevityScale,
		decimal cvScale,
		int lifeExpectancyAge)
	{
		var baseCoverage = RoundCurrency(template.BaseCoverageAmount * budgetScale * longevityScale);
		var baseAnnualPremium = RoundCurrency(template.BaseAnnualPremium * budgetScale);
		var depositOptionPayment = RoundCurrency(template.DepositOptionPayment * budgetScale);
		var totalAnnualOutlay = RoundCurrency(baseAnnualPremium + depositOptionPayment);
		var cashValueYear10 = RoundCurrency(template.CashValueYear10 * budgetScale * cvScale);
		var cashValueYear5 = RoundCurrency(template.CashValueYear5 * budgetScale * cvScale);
		var cashValueYear20 = RoundCurrency(template.CashValueYear20 * budgetScale * cvScale);

		var cvEfficiencyYear10 = Clamp(template.CvEfficiencyYear10 + (irrAdjustment * 0.6m), 90m, 150m);
		var irrOnCsvYear10 = Clamp(template.IrrOnCsvYear10 + (irrAdjustment * 0.7m), 0.5m, 20m);
		var deathBenefitAtLeCurrent = RoundCurrency(template.DeathBenefitAtLeCurrent * budgetScale * longevityScale);
		var irrAtLeCurrent = Clamp(template.IrrAtLeCurrent + irrAdjustment, 0.5m, 20m);
		var deathBenefitAtLeMinus2 = RoundCurrency(template.DeathBenefitAtLeMinus2 * budgetScale * longevityScale);
		var irrAtLeMinus2 = Clamp(template.IrrAtLeMinus2 + (irrAdjustment * 0.8m), 0.25m, 20m);

		var warnings = new List<string>();
		if (input.Budget > 0)
		{
			var overPct = ((totalAnnualOutlay / input.Budget) - 1m) * 100m;
			if (Math.Abs(overPct) > 5m)
			{
				warnings.Add($"Total annual outlay ${totalAnnualOutlay:N0}/yr ({overPct:+0.0;-0.0}% vs budget)." );
			}
		}

		string? stressNote = null;
		if (template.ExtendedPaymentsForStress is { } extension)
		{
			stressNote = $"Stress scale required {extension} additional payment years ({template.QuickPayCurrent + extension} total) to keep premium offset legal.";
			if (extension >= 5)
			{
				warnings.Add(stressNote);
			}
		}

		return new RecommendationColumn(
			Id: template.Id,
			Label: template.Label,
			BaseCoverageAmount: baseCoverage,
			BaseAnnualPremium: baseAnnualPremium,
			DepositOptionPayment: depositOptionPayment,
			TotalAnnualOutlay: totalAnnualOutlay,
			CashValueYear10: cashValueYear10,
			CashValueYear5: cashValueYear5,
			CashValueYear20: cashValueYear20,
			CvEfficiencyYear10: cvEfficiencyYear10,
			IrrOnCsvYear10: irrOnCsvYear10,
			DeathBenefitAtLeCurrent: deathBenefitAtLeCurrent,
			IrrAtLeCurrent: irrAtLeCurrent,
			DeathBenefitAtLeMinus2: deathBenefitAtLeMinus2,
			IrrAtLeMinus2: irrAtLeMinus2,
			QuickPayCurrent: template.QuickPayCurrent,
			QuickPayMinus2: template.QuickPayMinus2,
			Recommended: false,
			ExtendedPaymentsForStress: template.ExtendedPaymentsForStress,
			StressPaymentExtensionNote: stressNote,
			LifeExpectancyAgeUsed: lifeExpectancyAge,
			Explain: "Apply to scenario",
			Warnings: warnings);
	}

	private static IReadOnlyList<RecommendationColumn> RankColumns(IReadOnlyList<RecommendationColumn> columns, DecisionInput input)
	{
		return input.ScenarioType switch
		{
			RecommendationScenarios.MaximizeEarlyCsv => columns
				.OrderByDescending(x => x.CvEfficiencyYear10)
				.ThenByDescending(x => x.CashValueYear10)
				.ThenByDescending(x => x.IrrOnCsvYear10)
				.ToArray(),
			RecommendationScenarios.MaximizeDeathBenefit => columns
				.OrderByDescending(x => x.DeathBenefitAtLeCurrent)
				.ThenByDescending(x => x.IrrAtLeCurrent)
				.ToArray(),
			_ => RankIrrColumnsByProfile(columns, input)
		};
	}

	private static IReadOnlyList<RecommendationColumn> RankIrrColumnsByProfile(
		IReadOnlyList<RecommendationColumn> columns,
		DecisionInput input)
	{
		var normalizedAge = input.Age ?? 40;
		var smokingAdjustment = input.IsNonSmoker switch
		{
			true => 6d,
			false => -7d,
			null => 0d
		};

		var affordability = Math.Clamp(((double)input.Budget - 50000d) / 10000d, -5d, 8d);

		var pay90Score = 68d + (affordability * 1.8d) + ((45d - normalizedAge) * 0.55d) + smokingAdjustment;
		var pay20Score = 70d + (affordability * 0.8d) + ((50d - Math.Abs(normalizedAge - 42d)) * 0.15d) + (smokingAdjustment * 0.3d);
		var pay10Score = 66d - (affordability * 0.6d) + ((55d - normalizedAge) * 0.25d) + (smokingAdjustment * 0.15d);
		var pay100Score = pay90Score - 12d;

		return columns
			.OrderByDescending(column => GetProfileDurationScore(column.Label, pay20Score, pay90Score, pay100Score, pay10Score))
			.ThenByDescending(column => column.IrrAtLeCurrent)
			.ThenByDescending(column => column.DeathBenefitAtLeCurrent)
			.ToArray();
	}

	private static double GetProfileDurationScore(
		string label,
		double pay20Score,
		double pay90Score,
		double pay100Score,
		double pay10Score)
	{
		if (label.Contains("pay 20", StringComparison.OrdinalIgnoreCase))
		{
			return pay20Score;
		}

		if (label.Contains("pay 100", StringComparison.OrdinalIgnoreCase))
		{
			return pay100Score;
		}

		if (label.Contains("pay 90", StringComparison.OrdinalIgnoreCase))
		{
			return pay90Score;
		}

		return pay10Score;
	}

	private static string BuildScenarioDescription(DecisionInput input)
	{
		var scenarioTitle = input.ScenarioType switch
		{
			RecommendationScenarios.MaximizeEarlyCsv => "Maximize Early Cash Surrender Value",
			RecommendationScenarios.MaximizeDeathBenefit => "Maximize Death Benefit",
			_ => "Maximize IRR at Life Expectancy"
		};

		var description = $"{scenarioTitle}: {BuildClientSummary(input)}, ${input.Budget:N0}/annual";
		if (input.OffsetYear.HasValue)
		{
			description += $", offset starting year {input.OffsetYear.Value}";
		}

		return description;
	}

	private static string BuildClientSummary(DecisionInput input)
	{
		var genderCode = input.Gender switch
		{
			"Male" => "M",
			"Female" => "F",
			_ => "U"
		};

		var ageDisplay = input.Age ?? 40;
		var smoking = input.IsNonSmoker switch
		{
			true => "NonSmoker",
			false => "Smoker",
			null => "UnknownSmoking"
		};

		return $"{genderCode}{ageDisplay} {smoking}, Single Life";
	}

	private static int EstimateLifeExpectancyAge(DecisionInput input)
	{
		var baseLe = input.Gender switch
		{
			"Female" => 86,
			"Male" => 84,
			_ => 85
		};

		if (input.IsNonSmoker == true)
		{
			baseLe += 1;
		}
		else if (input.IsNonSmoker == false)
		{
			baseLe -= 3;
		}

		if (input.Age.HasValue)
		{
			var ageAdjustment = (int)Math.Round((40 - input.Age.Value) / 2.0, MidpointRounding.AwayFromZero);
			baseLe += ageAdjustment;
		}

		return Math.Clamp(baseLe, 70, 95);
	}

	private static decimal BuildIrrAdjustment(DecisionInput input)
	{
		var adjustment = 0m;
		if (input.IsNonSmoker == true)
		{
			adjustment += 0.08m;
		}
		else if (input.IsNonSmoker == false)
		{
			adjustment -= 0.12m;
		}

		if (input.Gender == "Female")
		{
			adjustment += 0.03m;
		}

		if (input.Age.HasValue)
		{
			adjustment += Clamp((45 - input.Age.Value) * 0.015m, -0.15m, 0.15m);
		}

		return adjustment;
	}

	private static decimal RoundCurrency(decimal value)
		=> Math.Round(value, 0, MidpointRounding.AwayFromZero);

	private static decimal Clamp(decimal value, decimal min, decimal max)
		=> value < min ? min : value > max ? max : value;

	private static string GetPrimaryMetricDisplay(RecommendationColumn column, string scenarioType)
	{
		return scenarioType switch
		{
			RecommendationScenarios.MaximizeEarlyCsv => $"CV efficiency {column.CvEfficiencyYear10:0.0}%",
			RecommendationScenarios.MaximizeDeathBenefit => $"DB@LE ${column.DeathBenefitAtLeCurrent:N0}",
			_ => $"IRR@LE {column.IrrAtLeCurrent:0.00}%"
		};
	}

	private sealed record DecisionInput(int? Age, string Gender, bool? IsNonSmoker, decimal Budget, string ScenarioType, int? OffsetYear);

	private sealed record RecommendationTemplate(
		string Id,
		string Label,
		decimal BaseCoverageAmount,
		decimal BaseAnnualPremium,
		decimal DepositOptionPayment,
		decimal CashValueYear10,
		decimal CashValueYear5,
		decimal CashValueYear20,
		decimal CvEfficiencyYear10,
		decimal IrrOnCsvYear10,
		decimal DeathBenefitAtLeCurrent,
		decimal IrrAtLeCurrent,
		decimal DeathBenefitAtLeMinus2,
		decimal IrrAtLeMinus2,
		int QuickPayCurrent,
		int QuickPayMinus2,
		int? ExtendedPaymentsForStress);
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
