using System.Collections.ObjectModel;
using Pact.Core.Agents;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class SubscriptionUsageRefreshServiceTests
{
	[Test]
	public async Task RefreshAsync_reads_usage_outside_the_callers_synchronization_context()
	{
		CapturingReader reader = new();
		SubscriptionUsageRefreshService service = new(reader);
		AgentProfileRecord profile = new("codex", AgentKind.Codex, "Codex", "codex", null, "pwsh");
		ObservableCollection<SubscriptionUsageRow> rows = [];
		var previous = SynchronizationContext.Current;
		SynchronizationContext callerContext = new();
		SynchronizationContext.SetSynchronizationContext(callerContext);
		try
		{
			await service.RefreshAsync([profile], rows, CancellationToken.None);
		}
		finally
		{
			SynchronizationContext.SetSynchronizationContext(previous);
		}

		reader.ReadCount.ShouldBe(1);
		reader.ContextDuringRead.ShouldNotBeSameAs(callerContext);
		rows.Single().ProfileId.ShouldBe("codex");
	}

	private sealed class CapturingReader : ISubscriptionUsageReader
	{
		public int ReadCount { get; private set; }

		public SynchronizationContext? ContextDuringRead { get; private set; }

		public Task<SubscriptionUsageSnapshot> ReadAsync(
			AgentProfileRecord profile,
			CancellationToken cancellationToken)
		{
			ReadCount++;
			ContextDuringRead = SynchronizationContext.Current;
			return Task.FromResult(new SubscriptionUsageSnapshot(
				profile.Id,
				profile.DisplayName,
				profile.Kind,
				SubscriptionUsageState.Unavailable,
				null,
				null,
				"No data",
				null,
				null,
				DateTimeOffset.UnixEpoch));
		}
	}
}
