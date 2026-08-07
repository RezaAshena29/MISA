using MISA.Infrastructure;
using Microsoft.Extensions.Options;

namespace MISA.ArchitectureTests;

public sealed class GovernanceGuardTests
{
	[Fact]
	public void PromptGuardBlocksOverlongPrompt()
	{
		var options = Options.Create(new GuardOptions
		{
			MaxPromptLength = 12,
			BlockedPromptPatterns = Array.Empty<string>()
		});

		var guard = new DefaultPromptGuard(options);

		var safe = guard.IsSafe("this prompt is definitely too long", out var reason);

		Assert.False(safe);
		Assert.Contains("max length", reason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void PromptGuardBlocksInlineSecretLikeContent()
	{
		var options = Options.Create(new GuardOptions
		{
			BlockedPromptPatterns = Array.Empty<string>(),
			BlockPromptWithInlineSecrets = true
		});

		var guard = new DefaultPromptGuard(options);

		var safe = guard.IsSafe("please use api_key=ABCD1234EFGH5678", out var reason);

		Assert.False(safe);
		Assert.Contains("inline secret", reason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void ResponseGuardMasksConfiguredTermsAndPiiPatterns()
	{
		var options = Options.Create(new GuardOptions
		{
			BlockedResponseTerms = ["secret"],
			MaskToken = "[redacted]",
			MaskEmails = true,
			MaskPhoneNumbers = true
		});

		var guard = new DefaultResponseGuard(options);

		var sanitized = guard.Sanitize("secret contact: user@example.com call +1 555 444 3322");

		Assert.Contains("[redacted]", sanitized, StringComparison.Ordinal);
		Assert.Contains("[redacted-email]", sanitized, StringComparison.Ordinal);
		Assert.Contains("[redacted-phone]", sanitized, StringComparison.Ordinal);
	}
}