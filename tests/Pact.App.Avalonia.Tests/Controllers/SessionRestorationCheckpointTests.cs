using Pact.App.Avalonia.Controllers;
using Pact.Core.Updates;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class SessionRestorationCheckpointTests
{
	[Test]
	public async Task Shutdown_freezes_the_final_projection_and_waits_for_earlier_writes()
	{
		SessionRestorationState current = new(["first"], [], [], new(null, "first"), false);
		List<SessionRestorationState> saved = [];
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		SessionRestorationCheckpoint checkpoint = new(() => current, async state =>
		{
			if (saved.Count == 0)
			{
				entered.SetResult();
				await release.Task;
			}
			saved.Add(state);
		}, exception => throw new AssertionException(exception.Message));
		checkpoint.Publish();
		saved.ShouldBeEmpty(); // Startup is held until restoration completes.
		checkpoint.Start();
		await entered.Task;
		current = current with { LiveTerminalIds = ["first", "last"], UnreadTerminalIds = ["last"] };
		var flush = checkpoint.FreezeAsync();
		flush.IsCompleted.ShouldBeFalse();
		current = current with { LiveTerminalIds = [], UnreadTerminalIds = [], Selection = null };
		checkpoint.Publish(); // Shutdown changes cannot replace the frozen state.
		release.SetResult();
		await flush;
		saved.Count.ShouldBe(2);
		saved[1].LiveTerminalIds.ShouldBe(["first", "last"]);
		saved[1].UnreadTerminalIds.ShouldBe(["last"]);
		saved[1].Selection.ShouldBe(new SoftRestartSelection(null, "first"));
	}

	[Test]
	public async Task Equal_projections_are_not_written_on_every_classifier_event()
	{
		var count = 0;
		SessionRestorationCheckpoint checkpoint = new(() => new(["s"], [], [], null, false),
			_ => { count++; return Task.CompletedTask; }, exception => throw new AssertionException(exception.Message));
		checkpoint.Start();
		checkpoint.Publish();
		checkpoint.Publish();
		await checkpoint.FreezeAsync();
		count.ShouldBe(1);
	}
}
