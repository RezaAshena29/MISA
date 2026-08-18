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
				["knowledge"] = ["knowledge.mcp"]
			}
		});
		var guard = new McpPolicyGuard(options);

		Assert.False(guard.IsAllowed("knowledge", "knowledge.mcp"));
	}

	[Fact]
	public void EnabledMcpAllowsConfiguredToolByRoute()
	{
		var options = Options.Create(new McpOptions
		{
			Enabled = true,
			AllowedToolsByRoute = new Dictionary<string, string[]>
			{
				["knowledge"] = ["knowledge.mcp"],
				["reasoning"] = ["reasoning.mcp"],
				["clarification"] = ["clarification.mcp"]
			}
		});
		var guard = new McpPolicyGuard(options);

		Assert.True(guard.IsAllowed("knowledge", "knowledge.mcp"));
		Assert.True(guard.IsAllowed("reasoning", "reasoning.mcp"));
		Assert.True(guard.IsAllowed("clarification", "clarification.mcp"));
		Assert.False(guard.IsAllowed("knowledge", "other.tool"));
		Assert.False(guard.IsAllowed("illustration", "knowledge.mcp"));
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
				["knowledge.mcp"] = 1200
			}
		});
		var guard = new McpPolicyGuard(options);

		Assert.Equal(1200, (int)guard.GetTimeout("knowledge.mcp").TotalMilliseconds);
		Assert.Equal(2500, (int)guard.GetTimeout("unknown.tool").TotalMilliseconds);
	}
}
