using MISA.Contracts;
using MISA.Decisioning;

namespace MISA.ArchitectureTests;

public sealed class DecisioningServiceTests
{
	[Fact]
	public async Task HighBudgetNonSmokerProfilePrefersPay90()
	{
		var service = new RuleBasedDecisioningService();

		var recommendation = await service.BuildRecommendationAsync(
			new ChatRequestDto("male age 32 non-smoker budget $160k", "decisioning-1"),
			CancellationToken.None);

		Assert.Contains("Top pick: Pay 90", recommendation, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Runner-up: Pay 20", recommendation, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task OlderSmokerProfilePrefersBalancedDuration()
	{
		var service = new RuleBasedDecisioningService();

		var recommendation = await service.BuildRecommendationAsync(
			new ChatRequestDto("female age 58 smoker budget $45k", "decisioning-2"),
			CancellationToken.None);

		Assert.Contains("Top pick: Pay 20", recommendation, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Top pick: Pay 90", recommendation, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task MissingStructuredInputsFallsBackToInferredProfile()
	{
		var service = new RuleBasedDecisioningService();

		var recommendation = await service.BuildRecommendationAsync(
			new ChatRequestDto("recommend a plan", "decisioning-3"),
			CancellationToken.None);

		Assert.Contains("age inferred", recommendation, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("budget $100,000", recommendation, StringComparison.OrdinalIgnoreCase);
	}
}