using System.Text.Json;
using MISA.Application;
using MISA.Contracts;
using MISA.Decisioning;
using Microsoft.Extensions.Options;

namespace MISA.ArchitectureTests;

public sealed class McpDecisioningServiceDecoratorTests
{
	[Fact]
	public async Task DisabledFlagUsesInnerDecisioningWithoutCallingMcp()
	{
		var decorator = new McpDecisioningServiceDecorator(
			new RuleBasedDecisioningService(),
			new ThrowingMcpToolBroker(),
			Options.Create(new DecisioningMcpOptions
			{
				Enabled = false,
				RecommendationTableToolName = "decisioning.mcp"
			}));

		var table = await decorator.BuildRecommendationTableAsync(
			new ChatRequestDto("male age 45 non-smoker budget $100k", "decisioning-mcp-disabled"),
			CancellationToken.None);

		Assert.NotNull(table);
		Assert.NotEmpty(table.Columns);
		Assert.DoesNotContain("MCP", table.ScenarioDescription, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task EnabledFlagUsesMcpTableWhenPayloadIsValid()
	{
		var broker = new StubMcpToolBroker(request =>
		{
			Assert.Equal("illustration", request.Route);
			Assert.Equal("decisioning.mcp", request.ToolName);
			var payload = JsonSerializer.Serialize(BuildMcpTable());
			return Task.FromResult(McpToolCallResult.Succeeded(payload, TimeSpan.FromMilliseconds(7)));
		});

		var decorator = new McpDecisioningServiceDecorator(
			new RuleBasedDecisioningService(),
			broker,
			Options.Create(new DecisioningMcpOptions
			{
				Enabled = true,
				RecommendationTableToolName = "decisioning.mcp"
			}));

		var table = await decorator.BuildRecommendationTableAsync(
			new ChatRequestDto("female age 40 non-smoker budget $80k", "decisioning-mcp-enabled"),
			CancellationToken.None);

		Assert.Equal("MCP decisioning scenario", table.ScenarioDescription);
		Assert.Single(table.Columns);
		Assert.Equal("mcp-plan", table.Columns[0].Id);
	}

	[Fact]
	public async Task EnabledFlagFallsBackWhenMcpFails()
	{
		var decorator = new McpDecisioningServiceDecorator(
			new RuleBasedDecisioningService(),
			new StubMcpToolBroker(_ =>
				Task.FromResult(McpToolCallResult.Failed("timeout", "timed out", TimeSpan.FromMilliseconds(200)))),
			Options.Create(new DecisioningMcpOptions
			{
				Enabled = true,
				RecommendationTableToolName = "decisioning.mcp"
			}));

		var table = await decorator.BuildRecommendationTableAsync(
			new ChatRequestDto("male age 45 non-smoker budget $100k", "decisioning-mcp-failed"),
			CancellationToken.None);

		Assert.NotEmpty(table.Columns);
		Assert.DoesNotContain("MCP decisioning scenario", table.ScenarioDescription, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task EnabledFlagFallsBackWhenMcpPayloadIsMalformed()
	{
		var decorator = new McpDecisioningServiceDecorator(
			new RuleBasedDecisioningService(),
			new StubMcpToolBroker(_ =>
				Task.FromResult(McpToolCallResult.Succeeded("{\"invalid\":", TimeSpan.FromMilliseconds(10)))),
			Options.Create(new DecisioningMcpOptions
			{
				Enabled = true,
				RecommendationTableToolName = "decisioning.mcp"
			}));

		var table = await decorator.BuildRecommendationTableAsync(
			new ChatRequestDto("female age 52 smoker budget $65k", "decisioning-mcp-malformed"),
			CancellationToken.None);

		Assert.NotEmpty(table.Columns);
		Assert.DoesNotContain("MCP decisioning scenario", table.ScenarioDescription, StringComparison.OrdinalIgnoreCase);
	}

	private static RecommendationTable BuildMcpTable()
	{
		return new RecommendationTable(
			ScenarioDescription: "MCP decisioning scenario",
			ScenarioType: RecommendationScenarios.MaximizeIrrAtLe,
			ClientSummary: "MCP generated profile",
			PremiumBudget: 80000m,
			Columns:
			[
				new RecommendationColumn(
					Id: "mcp-plan",
					Label: "MCP Plan",
					BaseCoverageAmount: 1000000m,
					BaseAnnualPremium: 18000m,
					DepositOptionPayment: 0m,
					TotalAnnualOutlay: 18000m,
					CashValueYear10: 250000m,
					CashValueYear5: 100000m,
					CashValueYear20: 550000m,
					CvEfficiencyYear10: 100.0m,
					IrrOnCsvYear10: 6.5m,
					DeathBenefitAtLeCurrent: 2200000m,
					IrrAtLeCurrent: 5.0m,
					DeathBenefitAtLeMinus2: 1800000m,
					IrrAtLeMinus2: 4.0m,
					QuickPayCurrent: 10,
					QuickPayMinus2: 12,
					Recommended: true,
					ExtendedPaymentsForStress: null,
					StressPaymentExtensionNote: null,
					LifeExpectancyAgeUsed: 84,
					Explain: "MCP-selected recommendation",
					Warnings: []),
			]);
	}

	private sealed class StubMcpToolBroker : IMcpToolBroker
	{
		private readonly Func<McpToolCallRequest, Task<McpToolCallResult>> _handler;

		public StubMcpToolBroker(Func<McpToolCallRequest, Task<McpToolCallResult>> handler)
		{
			_handler = handler;
		}

		public Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
		{
			return _handler(request);
		}
	}

	private sealed class ThrowingMcpToolBroker : IMcpToolBroker
	{
		public Task<McpToolCallResult> InvokeAsync(McpToolCallRequest request, CancellationToken cancellationToken)
		{
			throw new InvalidOperationException("MCP broker must not be called when Decisioning MCP is disabled.");
		}
	}
}
