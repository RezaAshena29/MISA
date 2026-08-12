using MISA.Infrastructure;
using Microsoft.Extensions.Options;

namespace MISA.ArchitectureTests;

public sealed class McpPolicyGuardTests
{
	[Fact]
	public void DisabledMcpDeniesAllTools()
	{
		var options = Options.Create(new McpOptions
		{
			Enabled = false,
			AllowedToolsByRoute = new Dictionary<string, string[]>
			{
				["knowledge"] = ["knowledge.answer"]
			}
		});
		var guard = new McpPolicyGuard(options);

		Assert.False(guard.IsAllowed("knowledge", "knowledge.answer"));
	}

	[Fact]
	public void EnabledMcpAllowsConfiguredToolByRoute()
	{
		var options = Options.Create(new McpOptions
		{
			Enabled = true,
			AllowedToolsByRoute = new Dictionary<string, string[]>
			{
				["knowledge"] = ["knowledge.answer"]
			}
		});
		var guard = new McpPolicyGuard(options);

		Assert.True(guard.IsAllowed("knowledge", "knowledge.answer"));
		Assert.False(guard.IsAllowed("knowledge", "other.tool"));
		Assert.False(guard.IsAllowed("illustration", "knowledge.answer"));
	}

	[Fact]
	public void ToolTimeoutOverrideTakesPrecedence()
	{
		var options = Options.Create(new McpOptions
		{
			Enabled = true,
			DefaultTimeoutMs = 2500,
			ToolTimeoutsMs = new Dictionary<string, int>
			{
				["knowledge.answer"] = 1200
			}
		});
		var guard = new McpPolicyGuard(options);

		Assert.Equal(1200, (int)guard.GetTimeout("knowledge.answer").TotalMilliseconds);
		Assert.Equal(2500, (int)guard.GetTimeout("unknown.tool").TotalMilliseconds);
	}
}
