using Pact.Core.Projects;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class ProjectMarkdownDocumentTests
{
	[Test]
	public async Task Debounce_uses_injected_time_provider()
	{
		var debounce = TimeSpan.FromSeconds(5);
		ManualTimeProvider time = new();
		FakeMarkdownFileStore store = new("initial");
		ProjectMarkdownDocument document = new(
			store,
			@"C:\repo\README.md",
			debounce,
			time);
		await document.LoadAsync(CancellationToken.None);

		document.SetText("draft");
		await time.WaitForTimerCountAsync(debounce, 1).WaitAsync(TimeSpan.FromSeconds(1));
		time.Advance(TimeSpan.FromSeconds(4));
		store.SaveCount.ShouldBe(0);
		time.Advance(TimeSpan.FromSeconds(1));
		await store.SaveObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

		store.SaveCount.ShouldBe(1);
		store.Text.ShouldBe("draft");
	}

	[Test]
	public async Task Flush_sets_conflict_instead_of_overwriting_external_content()
	{
		FakeMarkdownFileStore store = new("initial");
		ProjectMarkdownDocument document = new(store, @"C:\repo\README.md", TimeSpan.FromHours(1));
		await document.LoadAsync(CancellationToken.None);
		document.SetText("mine");
		store.ReplaceExternally("external");

		await document.FlushAsync(CancellationToken.None);

		document.SaveStatus.State.ShouldBe(DocumentSaveState.Conflict);
		document.Text.ShouldBe("mine");
		store.Text.ShouldBe("external");
	}

	[Test]
	public async Task Clean_document_reloads_external_change()
	{
		FakeMarkdownFileStore store = new("initial");
		ProjectMarkdownDocument document = new(store, @"C:\repo\README.md", TimeSpan.FromHours(1));
		await document.LoadAsync(CancellationToken.None);
		store.ReplaceExternally("external");

		await document.CheckForExternalChangeAsync(CancellationToken.None);

		document.SaveStatus.State.ShouldBe(DocumentSaveState.Clean);
		document.Text.ShouldBe("external");
	}

	[Test]
	public async Task Conflict_can_reload_disk_or_save_local_version()
	{
		FakeMarkdownFileStore store = new("initial");
		ProjectMarkdownDocument document = new(store, @"C:\repo\README.md", TimeSpan.FromHours(1));
		await document.LoadAsync(CancellationToken.None);
		document.SetText("mine");
		store.ReplaceExternally("external");
		await document.FlushAsync(CancellationToken.None);

		await document.ReloadFromDiskAsync(CancellationToken.None);
		document.Text.ShouldBe("external");
		document.SaveStatus.State.ShouldBe(DocumentSaveState.Clean);

		document.SetText("mine again");
		store.ReplaceExternally("external again");
		await document.FlushAsync(CancellationToken.None);
		await document.SaveMineAsync(CancellationToken.None);

		store.Text.ShouldBe("mine again");
		document.SaveStatus.State.ShouldBe(DocumentSaveState.Clean);
	}

	[Test]
	public async Task Debounced_save_failure_is_visible_and_retry_preserves_local_text()
	{
		FakeMarkdownFileStore store = new("initial") { FailNextSave = true };
		ProjectMarkdownDocument document = new(
			store,
			@"C:\repo\README.md",
			TimeSpan.Zero);
		await document.LoadAsync(CancellationToken.None);
		TaskCompletionSource<DocumentSaveStatus> failed =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		document.SaveStatusChanged += (_, status) =>
		{
			if (status.State == DocumentSaveState.Failed)
			{
				failed.TrySetResult(status);
			}
		};

		document.SetText("mine");
		var status = await failed.Task.WaitAsync(TimeSpan.FromSeconds(1));

		status.Exception.ShouldBeOfType<IOException>();
		document.Text.ShouldBe("mine");
		await document.FlushAsync(CancellationToken.None);
		document.SaveStatus.State.ShouldBe(DocumentSaveState.Clean);
		store.Text.ShouldBe("mine");
	}

	private sealed class FakeMarkdownFileStore(string initialText) : IProjectMarkdownFileStore
	{
		private int _revision = 1;
		public string Text { get; private set; } = initialText;
		public bool FailNextSave { get; set; }
		public int SaveCount { get; private set; }
		public TaskCompletionSource SaveObserved { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<ProjectMarkdownFileSnapshot> LoadAsync(string path, CancellationToken cancellationToken) =>
			Task.FromResult(Snapshot());

		public Task<ProjectMarkdownSaveResult> TrySaveAsync(
			string path,
			string text,
			string expectedRevision,
			CancellationToken cancellationToken)
		{
			if (FailNextSave)
			{
				FailNextSave = false;
				throw new IOException("markdown disk on fire");
			}

			if (!string.Equals(expectedRevision, Revision, StringComparison.Ordinal))
			{
				return Task.FromResult(new ProjectMarkdownSaveResult(false, Snapshot()));
			}

			Text = text;
			_revision++;
			SaveCount++;
			SaveObserved.TrySetResult();
			return Task.FromResult(new ProjectMarkdownSaveResult(true, Snapshot()));
		}

		public Task<ProjectMarkdownFileSnapshot> OverwriteAsync(
			string path,
			string text,
			CancellationToken cancellationToken)
		{
			Text = text;
			_revision++;
			return Task.FromResult(Snapshot());
		}

		public void ReplaceExternally(string text)
		{
			Text = text;
			_revision++;
		}

		private string Revision => _revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
		private ProjectMarkdownFileSnapshot Snapshot() => new(true, Text, Revision);
	}
}