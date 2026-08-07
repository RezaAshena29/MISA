using MISA.Contracts;
using MISA.Knowledge;

namespace MISA.ArchitectureTests;

public sealed class KnowledgeServiceTests
{
	[Fact]
	public async Task SmokingQuestionReturnsUnderwritingKnowledge()
	{
		var service = new KnowledgeService();

		var response = await service.AnswerAsync(
			new ChatRequestDto("explain smoker underwriting impact", "knowledge-1"),
			CancellationToken.None);

		Assert.Contains("Smoking status materially affects premium rates", response, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("internal-kb/underwriting/smoking-factors", response, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnknownQuestionFallsBackToGeneralGuidance()
	{
		var service = new KnowledgeService();

		var response = await service.AnswerAsync(
			new ChatRequestDto("hello there", "knowledge-2"),
			CancellationToken.None);

		Assert.Contains("Use client profile factors", response, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("internal-kb/runtime/general-illustration-guidance", response, StringComparison.Ordinal);
	}
}