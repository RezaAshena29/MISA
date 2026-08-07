using MISA.Application;
using MISA.Infrastructure;
using Microsoft.Extensions.Options;

namespace MISA.ArchitectureTests;

public sealed class DurableSessionStoreTests
{
	[Fact]
	public async Task FileBackedStorePersistsSessionAcrossInstances()
	{
		var filePath = Path.Combine(Path.GetTempPath(), $"misa-agentic-session-{Guid.NewGuid():N}.json");
		try
		{
			var options = Options.Create(new SessionStoreOptions
			{
				Mode = "File",
				FilePath = filePath
			});

			var storeOne = new FileBackedChatSessionStore(options);
			await storeOne.SaveAsync(
				new ChatSessionState("durable-1", LastRoute: "illustration", LastRecommendation: "Pay 90 best"),
				CancellationToken.None);

			var storeTwo = new FileBackedChatSessionStore(options);
			var loaded = await storeTwo.GetAsync("durable-1", CancellationToken.None);

			Assert.NotNull(loaded);
			Assert.Equal("illustration", loaded!.LastRoute);
			Assert.Equal("Pay 90 best", loaded.LastRecommendation);
		}
		finally
		{
			if (File.Exists(filePath))
			{
				File.Delete(filePath);
			}
		}
	}

	[Fact]
	public async Task FileBackedStoreClearRemovesPersistedSession()
	{
		var filePath = Path.Combine(Path.GetTempPath(), $"misa-agentic-session-{Guid.NewGuid():N}.json");
		try
		{
			var options = Options.Create(new SessionStoreOptions
			{
				Mode = "File",
				FilePath = filePath
			});

			var storeOne = new FileBackedChatSessionStore(options);
			await storeOne.SaveAsync(new ChatSessionState("durable-2", LastRoute: "reasoning"), CancellationToken.None);
			Assert.True(storeOne.Clear("durable-2"));

			var storeTwo = new FileBackedChatSessionStore(options);
			var loaded = await storeTwo.GetAsync("durable-2", CancellationToken.None);

			Assert.Null(loaded);
		}
		finally
		{
			if (File.Exists(filePath))
			{
				File.Delete(filePath);
			}
		}
	}
}