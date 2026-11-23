using System.Collections.ObjectModel;
using Pact.Core.Agents;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class SubscriptionUsageRefreshServiceTests
{
	[Test]
	public async Task Changed_home_clears_the_previous_accounts_numbers_when_the_new_account_is_unavailable()
	{
		AgentProfileRecord original = new("work", AgentKind.Codex, "Work", "codex", null, "pwsh");
		var oldRow = new SubscriptionUsageRow(original).Apply(new SubscriptionUsageSnapshot(original.Id,
			original.DisplayName, original.Kind, SubscriptionUsageState.Ready, new SubscriptionLimitSnapshot(95, null),
			null, "Ready", null, null, DateTimeOffset.UtcNow));
		ObservableCollection<SubscriptionUsageRow> rows = [oldRow];
		var updated = original with { NativeConfigDirectory = Path.Combine(Path.GetTempPath(), "another-account") };
		await new SubscriptionUsageRefreshService(new CapturingReader()).RefreshAsync([updated], rows, CancellationToken.None);
		var row = rows.Single();
		row.ProfileId.ShouldBe(original.Id);
		row.State.ShouldBe(SubscriptionUsageState.Unavailable);
		row.FiveHour.ShouldBeNull();
		row.IsNearLimit.ShouldBeFalse();
	}

	[Test]
	public async Task Renaming_updates_the_bound_row_without_blank_usage_while_the_reader_is_pending()
	{
		AgentProfileRecord profile = new("codex", AgentKind.Codex, "Work", "codex", null, "pwsh");
		SubscriptionLimitSnapshot limit = new(68, null);
		SubscriptionUsageSnapshot snapshot = new(profile.Id, profile.DisplayName, profile.Kind,
			SubscriptionUsageState.Ready, limit, null, "Ready", null, null, DateTimeOffset.UtcNow);
		var row = new SubscriptionUsageRow(profile).Apply(snapshot);
		ObservableCollection<SubscriptionUsageRow> rows = [row];
		DeferredReader reader = new();
		var renamed = profile with { DisplayName = "Renamed" };
		var refresh = new SubscriptionUsageRefreshService(reader).RefreshAsync([renamed], rows, CancellationToken.None);
		try
		{
			(await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(renamed);
			rows.ShouldHaveSingleItem().ShouldBeSameAs(row);
			row.ProfileName.ShouldBe("Renamed");
			row.State.ShouldBe(SubscriptionUsageState.Ready);
			row.FiveHour.ShouldBe(limit);
		}
		finally
		{
			reader.Result.TrySetResult(snapshot with { ProfileName = "Renamed" });
			await refresh;
		}
	}

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

	private sealed class DeferredReader : ISubscriptionUsageReader
	{
		public TaskCompletionSource<AgentProfileRecord> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<SubscriptionUsageSnapshot> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<SubscriptionUsageSnapshot> ReadAsync(AgentProfileRecord profile, CancellationToken cancellationToken)
		{
			Started.SetResult(profile);
			return Result.Task;
		}
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
