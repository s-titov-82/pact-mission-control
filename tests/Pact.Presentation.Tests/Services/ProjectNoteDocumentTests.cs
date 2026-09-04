using Pact.Core.AgentControl;
using Pact.Core.Projects;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class ProjectNoteDocumentTests
{
	private sealed class FakeNotesStore : IProjectNotesStore
	{
		public string Saved = string.Empty;
		public int SaveCount;
		public string InitialContent = string.Empty;
		public bool FailNextSave;
		public (TaskCompletionSource Started, TaskCompletionSource Release)? BlockingSave;
		public List<string> Appended = [];
		public TaskCompletionSource SaveObserved { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task<string> LoadAsync(string projectRootPath, CancellationToken ct) => Task.FromResult(InitialContent);
		public async Task SaveAsync(string projectRootPath, string text, CancellationToken ct)
		{
			if (FailNextSave)
			{ FailNextSave = false; throw new IOException("disk on fire"); }
			if (BlockingSave is (TaskCompletionSource started, TaskCompletionSource release))
			{
				BlockingSave = null;
				started.SetResult();
				await release.Task;
			}
			Saved = text;
			SaveCount++;
			SaveObserved.TrySetResult();
		}
		public Task AppendAsync(string projectRootPath, string text, CancellationToken ct) { Appended.Add(text); return Task.CompletedTask; }
	}

	private sealed class ReorderingNotesStore : IProjectNotesStore
	{
		private int _saveAttempt;

		public TaskCompletionSource FirstSaveStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource ReleaseFirstSave { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public string Saved { get; private set; } = string.Empty;

		public Task<string> LoadAsync(
			string projectRootPath,
			CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);

		public async Task SaveAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken)
		{
			if (Interlocked.Increment(ref _saveAttempt) == 1)
			{
				FirstSaveStarted.SetResult();
				await ReleaseFirstSave.Task;
			}

			Saved = text;
		}

		public Task AppendAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}

	private static readonly TimeSpan ShortDebounce = TimeSpan.FromMilliseconds(50);

	[Test]
	public async Task LoadAsync_PopulatesTextOnce()
	{
		FakeNotesStore store = new() { InitialContent = "hello" };
		ProjectNoteDocument doc = new(store, @"D:\proj", ShortDebounce);
		await doc.LoadAsync(CancellationToken.None);
		doc.Text.ShouldBe("hello");
		doc.IsLoaded.ShouldBeTrue();
		store.InitialContent = "changed on disk";
		await doc.LoadAsync(CancellationToken.None);
		doc.Text.ShouldBe("hello");
	}

	[Test]
	public async Task SetText_FlushesAfterDebounce()
	{
		ManualTimeProvider time = new();
		FakeNotesStore store = new();
		ProjectNoteDocument doc = new(store, @"D:\proj", ShortDebounce, time);
		await doc.LoadAsync(CancellationToken.None);
		doc.SetText("draft");
		await time.WaitForTimerCountAsync(ShortDebounce, 1).WaitAsync(TimeSpan.FromSeconds(1));
		store.SaveCount.ShouldBe(0);
		time.Advance(ShortDebounce - TimeSpan.FromMilliseconds(1));
		store.SaveCount.ShouldBe(0);
		time.Advance(TimeSpan.FromMilliseconds(1));
		await store.SaveObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
		store.SaveCount.ShouldBe(1);
		store.Saved.ShouldBe("draft");
	}

	[Test]
	public async Task SetText_RepeatedEdits_CoalesceIntoOneSave()
	{
		ManualTimeProvider time = new();
		FakeNotesStore store = new();
		ProjectNoteDocument doc = new(store, @"D:\proj", ShortDebounce, time);
		await doc.LoadAsync(CancellationToken.None);
		doc.SetText("a");
		doc.SetText("ab");
		doc.SetText("abc");
		await time.WaitForTimerCountAsync(ShortDebounce, 3).WaitAsync(TimeSpan.FromSeconds(1));
		time.Advance(ShortDebounce);
		await store.SaveObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
		store.SaveCount.ShouldBe(1);
		store.Saved.ShouldBe("abc");
	}

	[Test]
	public async Task FlushAsync_SavesImmediately_AndOnlyWhenDirty()
	{
		FakeNotesStore store = new();
		ProjectNoteDocument doc = new(store, @"D:\proj", TimeSpan.FromMinutes(5));
		await doc.LoadAsync(CancellationToken.None);
		await doc.FlushAsync(CancellationToken.None);
		store.SaveCount.ShouldBe(0);
		doc.SetText("dirty");
		await doc.FlushAsync(CancellationToken.None);
		store.SaveCount.ShouldBe(1);
		store.Saved.ShouldBe("dirty");
	}

	[Test]
	public async Task Debounced_save_failure_keeps_dirty_and_publishes_failed_status()
	{
		FakeNotesStore store = new() { FailNextSave = true };
		ProjectNoteDocument document = new(store, @"D:\proj", TimeSpan.Zero);
		TaskCompletionSource<DocumentSaveStatus> failed =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		document.SaveStatusChanged += (_, status) =>
		{
			if (status.State == DocumentSaveState.Failed)
			{
				failed.TrySetResult(status);
			}
		};

		document.SetText("unsaved");
		var status = await failed.Task.WaitAsync(TimeSpan.FromSeconds(1));

		status.ErrorMessage.ShouldNotBeNull().ShouldContain("disk on fire");
		status.Exception.ShouldBeOfType<IOException>();
		document.SaveStatus.State.ShouldBe(DocumentSaveState.Failed);
		await document.FlushAsync(CancellationToken.None);
		document.SaveStatus.State.ShouldBe(DocumentSaveState.Clean);
	}

	[Test]
	public async Task FailedSave_KeepsDocumentDirty_SoNextFlushRetries()
	{
		FakeNotesStore store = new() { FailNextSave = true };
		ProjectNoteDocument doc = new(store, @"D:\proj", TimeSpan.FromMinutes(5));
		await doc.LoadAsync(CancellationToken.None);
		doc.SetText("precious");
		await Should.ThrowAsync<IOException>(() => doc.FlushAsync(CancellationToken.None));
		await doc.FlushAsync(CancellationToken.None);
		store.SaveCount.ShouldBe(1);
		store.Saved.ShouldBe("precious");
	}

	[Test]
	public async Task EditDuringSave_KeepsDocumentDirty_AndNextFlushSavesNewestText()
	{
		FakeNotesStore store = new();
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		store.BlockingSave = (started, release);
		ProjectNoteDocument doc = new(store, @"D:\proj", TimeSpan.FromMinutes(5));
		await doc.LoadAsync(CancellationToken.None);
		doc.SetText("v1");
		var flush = doc.FlushAsync(CancellationToken.None);
		await started.Task;
		doc.SetText("v1 edited during save");
		release.SetResult();
		await flush;
		await doc.FlushAsync(CancellationToken.None);
		store.SaveCount.ShouldBe(2);
		store.Saved.ShouldBe("v1 edited during save");
	}

	[Test]
	public async Task Concurrent_flushes_cannot_persist_an_older_snapshot_last()
	{
		ReorderingNotesStore store = new();
		ProjectNoteDocument document = new(
			store,
			@"D:\proj",
			TimeSpan.FromMinutes(5));
		await document.LoadAsync(CancellationToken.None);
		document.SetText("v1");
		var firstFlush = document.FlushAsync(CancellationToken.None);
		await store.FirstSaveStarted.Task;
		document.SetText("v2");

		var secondFlush = document.FlushAsync(CancellationToken.None);
		store.ReleaseFirstSave.SetResult();
		await Task.WhenAll(firstFlush, secondFlush);

		store.Saved.ShouldBe("v2");
		document.SaveStatus.State.ShouldBe(DocumentSaveState.Clean);
	}

	[Test]
	public async Task Append_WhenLoaded_GoesThroughBufferWithSeparation()
	{
		FakeNotesStore store = new() { InitialContent = "existing" };
		ProjectNoteDocument doc = new(store, @"D:\proj", TimeSpan.FromMinutes(5));
		await doc.LoadAsync(CancellationToken.None);
		var raised = false;
		doc.TextReplaced += (_, _) => raised = true;
		doc.Append("appended");
		doc.Text.ShouldBe("existing\n\nappended\n");
		raised.ShouldBeTrue();
		store.Appended.ShouldBeEmpty();
		await doc.FlushAsync(CancellationToken.None);
		store.Saved.ShouldBe("existing\n\nappended\n");
	}

	[Test]
	public async Task ReplaceAsync_refuses_a_stale_revision_without_changing_text()
	{
		FakeNotesStore store = new() { InitialContent = "current" };
		ProjectNoteDocument document = new(store, @"D:\proj", TimeSpan.FromMinutes(5));
		await document.LoadAsync(CancellationToken.None);

		var result = await document.ReplaceAsync(
			"replacement",
			ProjectNotesRevision.Compute("stale"),
			CancellationToken.None);

		result.Status.ShouldBe(ProjectNotesMutationStatus.Conflict);
		result.Snapshot.Text.ShouldBe("current");
		document.Text.ShouldBe("current");
		store.SaveCount.ShouldBe(0);
	}

	[Test]
	public async Task ReplaceAsync_accepts_empty_text_notifies_editor_and_flushes()
	{
		FakeNotesStore store = new() { InitialContent = "delete me" };
		ProjectNoteDocument document = new(store, @"D:\proj", TimeSpan.FromMinutes(5));
		await document.LoadAsync(CancellationToken.None);
		var replaced = 0;
		document.TextReplaced += (_, _) => replaced++;

		var result = await document.ReplaceAsync(
			string.Empty,
			document.GetSnapshot().Revision,
			CancellationToken.None);

		result.Status.ShouldBe(ProjectNotesMutationStatus.Applied);
		result.Snapshot.Text.ShouldBeEmpty();
		store.Saved.ShouldBeEmpty();
		store.SaveCount.ShouldBe(1);
		replaced.ShouldBe(1);
	}

	[Test]
	public async Task ReplaceAsync_retains_the_new_buffer_when_persistence_fails()
	{
		FakeNotesStore store = new() { InitialContent = "old", FailNextSave = true };
		ProjectNoteDocument document = new(store, @"D:\proj", TimeSpan.FromMinutes(5));
		await document.LoadAsync(CancellationToken.None);

		var result = await document.ReplaceAsync(
			"new",
			document.GetSnapshot().Revision,
			CancellationToken.None);

		result.Status.ShouldBe(ProjectNotesMutationStatus.AppliedButNotPersisted);
		result.Snapshot.Text.ShouldBe("new");
		document.Text.ShouldBe("new");
		document.SaveStatus.State.ShouldBe(DocumentSaveState.Failed);
	}

	[Test]
	public async Task AppendAndFlushAsync_updates_the_live_buffer_and_persists_before_returning()
	{
		FakeNotesStore store = new() { InitialContent = "existing" };
		ProjectNoteDocument document = new(store, @"D:\proj", TimeSpan.FromMinutes(5));
		await document.LoadAsync(CancellationToken.None);

		var result = await document.AppendAndFlushAsync("appended", CancellationToken.None);

		result.Status.ShouldBe(ProjectNotesMutationStatus.Applied);
		result.Snapshot.Text.ShouldBe("existing\n\nappended\n");
		store.Saved.ShouldBe(result.Snapshot.Text);
		store.SaveCount.ShouldBe(1);
	}
}
