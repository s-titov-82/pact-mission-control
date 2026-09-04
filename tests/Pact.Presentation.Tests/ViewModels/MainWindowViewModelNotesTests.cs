using Pact.Core.Agents;
using Pact.Core.AgentControl;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Infrastructure.Storage;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class MainWindowViewModelNotesTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _root => _temporaryDirectory.Path;
	public void Dispose() => _temporaryDirectory.Dispose();

	[Test]
	public async Task ShowNotesTabAsync_CreatesRecordAndSelects()
	{
		(var vm, var store) = await CreateAsync();
		var note = await vm.ShowNotesTabAsync("p1", CancellationToken.None);
		vm.SelectedProjectNote.ShouldBeSameAs(note);
		vm.Workspaces.Single().IsNotesTabOpen.ShouldBeTrue();
		var doc = await store.LoadAsync(CancellationToken.None);
		doc.Projects.Single().NotesTab.ShouldNotBeNull();
		doc.Projects.Single().ActiveItemId.ShouldBe(note.Record.Id);
	}

	[Test]
	public async Task ShowNotesTabAsync_SecondCall_SelectsExisting_NoSecondRecord()
	{
		(var vm, _) = await CreateAsync();
		var first = await vm.ShowNotesTabAsync("p1", CancellationToken.None);
		var second = await vm.ShowNotesTabAsync("p1", CancellationToken.None);
		second.ShouldBeSameAs(first);
		vm.Workspaces.Single().Notes.ShouldHaveSingleItem();
	}

	[Test]
	public async Task HideNotesTabAsync_RemovesRecordAndViewModel()
	{
		(var vm, var store) = await CreateAsync();
		await vm.ShowNotesTabAsync("p1", CancellationToken.None);
		await vm.HideNotesTabAsync("p1", CancellationToken.None);
		vm.SelectedProjectNote.ShouldBeNull();
		vm.Workspaces.Single().IsNotesTabOpen.ShouldBeFalse();
		(await store.LoadAsync(CancellationToken.None)).Projects.Single().NotesTab.ShouldBeNull();
	}

	[Test]
	public async Task SelectedProjectNote_ClearsOtherSelections_AndViceVersa()
	{
		(var vm, _) = await CreateAsync(withSession: true);
		await vm.ShowNotesTabAsync("p1", CancellationToken.None);
		vm.SelectedSession.ShouldBeNull();
		vm.SelectedSession = vm.Sessions.First();
		vm.SelectedProjectNote.ShouldBeNull();
	}

	[Test]
	public async Task Selecting_non_note_item_clears_the_note_current_indicator()
	{
		(var vm, _) = await CreateAsync(withSession: true);
		var note = await vm.ShowNotesTabAsync("p1", CancellationToken.None);
		note.IsCurrentNote.ShouldBeTrue();

		vm.SelectedSession = vm.Sessions.Single();
		note.IsCurrentNote.ShouldBeFalse();

		vm.SelectedProjectNote = note;
		WebPageViewModel page = new(new WebPageRecord(
			"web-1", "Web", "https://example.test", "https://example.test",
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
		vm.WebPages.Add(page);
		vm.SelectedWebPage = page;

		note.IsCurrentNote.ShouldBeFalse();
	}

	[Test]
	public async Task SelectStartupItem_RestoresNotesTabFromActiveItemId()
	{
		(var vm, var store) = await CreateAsync();
		await vm.ShowNotesTabAsync("p1", CancellationToken.None);
		MainWindowViewModel reloaded = new(store, new ProjectNotesStore(new AppPaths(_root)));
		await reloaded.LoadAsync(CancellationToken.None);
		reloaded.SelectedProjectNote.ShouldNotBeNull();
	}

	[Test]
	public async Task AppendToProjectNotes_HiddenTab_DoesNotCreateStructuralTab()
	{
		(var vm, var store) = await CreateAsync();

		var result = await vm.AppendToProjectNotesAsync(
			"p1",
			"captured text",
			CancellationToken.None);

		result.Status.ShouldBe(ProjectNotesMutationStatus.Applied);
		result.Snapshot.Text.ShouldContain("captured text");
		vm.Workspaces.Single().Notes.ShouldBeEmpty();
		(await store.LoadAsync(CancellationToken.None))
			.Projects.Single().NotesTab.ShouldBeNull();
	}

	[Test]
	public async Task AppendToProjectNotes_LoadedDocument_AppendsToBuffer()
	{
		(var vm, _) = await CreateAsync();
		var note = await vm.ShowNotesTabAsync("p1", CancellationToken.None);
		var document = vm.GetOrCreateNoteDocument(note);
		await document.LoadAsync(CancellationToken.None);
		await vm.AppendToProjectNotesAsync("p1", "live append", CancellationToken.None);
		document.Text.Contains("live append", StringComparison.Ordinal).ShouldBeTrue();
	}

	[Test]
	public async Task Note_document_identity_is_owned_by_the_retained_project_workspace()
	{
		(var vm, _) = await CreateAsync();
		var note = await vm.ShowNotesTabAsync("p1", CancellationToken.None);

		var workspace =
			vm.GetOrCreateDocsAndNotesWorkspace(note);
		var document = vm.GetOrCreateNoteDocument(note);

		document.ShouldBeSameAs(workspace.NotesDocument);
		vm.GetOrCreateDocsAndNotesWorkspace(note).ShouldBeSameAs(workspace);
		vm.GetOrCreateNoteDocument(note).ShouldBeSameAs(document);
	}

	[Test]
	public async Task FlushAllNoteDocumentsAsync_attempts_every_workspace_before_reporting_failures()
	{
		var firstRoot = Path.Combine(_root, "first-project");
		var secondRoot = Path.Combine(_root, "second-project");
		JsonProjectStore store = new(_root);
		var now = DateTimeOffset.UtcNow;
		await store.SaveAsync(
			new ProjectsDocument(
				1,
				[
					new ProjectRecord("p1", "First", firstRoot, now, now, null),
					new ProjectRecord("p2", "Second", secondRoot, now, now, null)
				]),
			CancellationToken.None);
		RecordingNotesStore notesStore = new(firstRoot);
		MainWindowViewModel vm = new(store, notesStore);
		await vm.LoadAsync(CancellationToken.None);
		var firstNote = await vm.ShowNotesTabAsync("p1", CancellationToken.None);
		var secondNote = await vm.ShowNotesTabAsync("p2", CancellationToken.None);
		var first = vm.GetOrCreateNoteDocument(firstNote);
		var second = vm.GetOrCreateNoteDocument(secondNote);
		await first.LoadAsync(CancellationToken.None);
		await second.LoadAsync(CancellationToken.None);
		first.SetText("first");
		second.SetText("second");

		var error = await Should.ThrowAsync<AggregateException>(
			() => vm.FlushAllNoteDocumentsAsync(CancellationToken.None));

		error.InnerExceptions.Count.ShouldBe(1);
		notesStore.SaveAttempts.ShouldBe([firstRoot, secondRoot], ignoreOrder: true);
	}

	private async Task<(MainWindowViewModel Vm, JsonProjectStore Store)> CreateAsync(bool withSession = false)
	{
		JsonProjectStore store = new(_root);
		var now = DateTimeOffset.UtcNow;
		SessionRecord[] sessions = withSession ? [new("s1", AgentKind.Codex, "Codex", @"D:\proj", "codex", null, SessionStatus.Stopped, now, now)] : [];
		ProjectRecord project = new("p1", "Project", @"D:\proj", now, now, null) { Sessions = sessions };
		await store.SaveAsync(new ProjectsDocument(1, [project]), CancellationToken.None);
		MainWindowViewModel vm = new(store, new ProjectNotesStore(new AppPaths(_root)));
		await vm.LoadAsync(CancellationToken.None);
		return (vm, store);
	}

	private sealed class RecordingNotesStore(string failingRoot) : IProjectNotesStore
	{
		public List<string> SaveAttempts { get; } = [];

		public Task<string> LoadAsync(
			string projectRootPath,
			CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);

		public Task SaveAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken)
		{
			SaveAttempts.Add(projectRootPath);
			if (string.Equals(
					projectRootPath,
					failingRoot,
					StringComparison.OrdinalIgnoreCase))
			{
				throw new IOException("notes failed");
			}

			return Task.CompletedTask;
		}

		public Task AppendAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}
}
