using Pact.Core.Projects;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class DocsAndNotesWorkspaceViewModelTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _root => _temporaryDirectory.Path;

	[Test]
	public async Task Refresh_builds_common_and_docs_trees()
	{
		Write("AGENTS.md", "agents");
		Write("src/Service/README.md", "service");
		Write("docs/superpowers/specs/design.md", "spec");
		var workspace = CreateWorkspace();

		await workspace.RefreshAsync(CancellationToken.None);

		workspace.SelectedSection.ShouldBe(DocsAndNotesSection.Notes);
		workspace.ShowsDocumentTree.ShouldBeFalse();
		workspace.CommonTree.Select(node => node.Title).ShouldBe(["src", "AGENTS.md"]);
		workspace.DocsTree.Select(node => node.Title).ShouldBe(["superpowers"]);
		workspace.ActiveDocument.ShouldBeOfType<ProjectNoteDocument>();
	}

	[Test]
	public async Task Active_document_exposes_failed_save_and_retry_preserves_buffer()
	{
		FailingNotesStore store = new();
		ProjectNoteDocument notes = new(store, _root, TimeSpan.Zero);
		DocsAndNotesWorkspaceViewModel workspace = new(
			_root,
			notes,
			new ProjectMarkdownFileStore(),
			TimeSpan.FromHours(1));
		await workspace.RefreshAsync(CancellationToken.None);
		TaskCompletionSource failed =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		workspace.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(workspace.HasSaveError)
				&& workspace.HasSaveError)
			{
				failed.TrySetResult();
			}
		};

		workspace.ActiveDocument!.SetText("precious");
		await failed.Task.WaitAsync(TimeSpan.FromSeconds(1));

		workspace.SaveErrorMessage.ShouldNotBeNull().ShouldContain("notes failed");
		await workspace.RetrySaveAsync(CancellationToken.None);
		workspace.HasSaveError.ShouldBeFalse();
		workspace.SaveStatus.State.ShouldBe(DocumentSaveState.Clean);
		store.Saved.ShouldBe("precious");
	}

	[Test]
	public async Task Common_opens_the_root_readme_on_first_activation()
	{
		Write("README.md", "# Readme");
		Write("AGENTS.md", "agents");
		var workspace = CreateWorkspace();
		await workspace.RefreshAsync(CancellationToken.None);

		await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);

		workspace.ShowsDocumentTree.ShouldBeTrue();
		(workspace.ActiveDocument?.Text).ShouldBe("# Readme");
		workspace.SelectedNode.ShouldNotBeNull().Title.ShouldBe("README.md");
	}

	[Test]
	public async Task Common_opens_nothing_when_the_project_has_no_root_readme()
	{
		Write("AGENTS.md", "agents");
		var workspace = CreateWorkspace();
		await workspace.RefreshAsync(CancellationToken.None);

		await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);

		workspace.ActiveDocument.ShouldBeNull();
	}

	[Test]
	public async Task Docs_opens_nothing_on_first_activation()
	{
		Write("docs/manual-tests/checklist.md", "# Checklist");
		var workspace = CreateWorkspace();
		await workspace.RefreshAsync(CancellationToken.None);

		await workspace.SelectSectionAsync(DocsAndNotesSection.Docs, CancellationToken.None);

		workspace.ShowsDocumentTree.ShouldBeTrue();
		workspace.ActiveDocument.ShouldBeNull();
		workspace.DocsTree.Select(node => node.Title).ShouldBe(["manual-tests"]);
	}

	[Test]
	public async Task Selecting_a_document_expands_its_folders_and_is_remembered_per_section()
	{
		Write("README.md", "# Readme");
		Write("docs/manual-tests/checklist.md", "# Checklist");
		var workspace = CreateWorkspace();
		await workspace.RefreshAsync(CancellationToken.None);
		await workspace.SelectSectionAsync(DocsAndNotesSection.Docs, CancellationToken.None);
		var folder = workspace.DocsTree.Single();
		var checklist = folder.Children.Single();

		await workspace.SelectDocumentAsync(checklist, CancellationToken.None);

		(workspace.ActiveDocument?.Text).ShouldBe("# Checklist");
		folder.IsExpanded.ShouldBeTrue();
		workspace.SelectedNode.ShouldBeSameAs(checklist);

		await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
		(workspace.ActiveDocument?.Text).ShouldBe("# Readme");

		await workspace.SelectSectionAsync(DocsAndNotesSection.Docs, CancellationToken.None);
		(workspace.ActiveDocument?.Text).ShouldBe("# Checklist");
	}

	[Test]
	public async Task Selection_moves_before_the_previous_document_finishes_flushing()
	{
		Write("README.md", "# Readme");
		Write("AGENTS.md", "# Agents");
		BlockingMarkdownFileStore store = new();
		ProjectNoteDocument notes = new(new EmptyNotesStore(), _root, TimeSpan.FromHours(1));
		DocsAndNotesWorkspaceViewModel workspace = new(
			_root,
			notes,
			store,
			TimeSpan.FromHours(1));
		await workspace.RefreshAsync(CancellationToken.None);
		await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
		var readmeDocument = workspace.ActiveDocument.ShouldNotBeNull();
		readmeDocument.SetText("# Edited");
		var agents = workspace.CommonTree.Single(node => node.Title == "AGENTS.md");
		TaskCompletionSource gate =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		store.BlockNextSave = gate;

		var selection = workspace.SelectDocumentAsync(agents, CancellationToken.None);

		workspace.SelectedNode.ShouldBeSameAs(agents);
		workspace.ActiveDocument.ShouldBeSameAs(readmeDocument);
		selection.IsCompleted.ShouldBeFalse();

		gate.SetResult();
		await selection;

		workspace.SelectedNode.ShouldBeSameAs(agents);
		workspace.ActiveDocument.ShouldBeSameAs(agents.Document);
	}

	[Test]
	public async Task A_failed_flush_keeps_the_selection_on_the_chosen_node()
	{
		Write("README.md", "# Readme");
		Write("AGENTS.md", "# Agents");
		BlockingMarkdownFileStore store = new();
		ProjectNoteDocument notes = new(new EmptyNotesStore(), _root, TimeSpan.FromHours(1));
		DocsAndNotesWorkspaceViewModel workspace = new(
			_root,
			notes,
			store,
			TimeSpan.FromHours(1));
		await workspace.RefreshAsync(CancellationToken.None);
		await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
		var readmeDocument = workspace.ActiveDocument.ShouldNotBeNull();
		readmeDocument.SetText("# Edited");
		store.FailNextSave = true;
		var agents = workspace.CommonTree.Single(node => node.Title == "AGENTS.md");

		await Should.ThrowAsync<IOException>(
			() => workspace.SelectDocumentAsync(agents, CancellationToken.None));

		workspace.SelectedNode.ShouldBeSameAs(agents);
		workspace.ActiveDocument.ShouldBeSameAs(readmeDocument);
		workspace.HasSaveError.ShouldBeTrue();
		readmeDocument.Text.ShouldBe("# Edited");
	}

	[Test]
	public async Task A_node_outside_the_visible_tree_is_ignored()
	{
		Write("README.md", "# Readme");
		Write("docs/notes.md", "# Notes");
		var workspace = CreateWorkspace();
		await workspace.RefreshAsync(CancellationToken.None);
		await workspace.SelectSectionAsync(DocsAndNotesSection.Docs, CancellationToken.None);
		var docsNode = workspace.DocsTree.Single();
		await workspace.SelectDocumentAsync(docsNode, CancellationToken.None);
		await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);

		await workspace.SelectDocumentAsync(docsNode, CancellationToken.None);

		(workspace.ActiveDocument?.Text).ShouldBe("# Readme");
		workspace.SelectedNode.ShouldNotBeNull().Title.ShouldBe("README.md");
	}

	[Test]
	public async Task A_stale_node_from_a_previous_refresh_is_ignored()
	{
		Write("README.md", "# Readme");
		Write("src/details.md", "# Details");
		var workspace = CreateWorkspace();
		await workspace.RefreshAsync(CancellationToken.None);
		await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
		var staleNode = workspace.CommonTree.Single(node => node.IsFolder).Children.Single();
		await workspace.RefreshAsync(CancellationToken.None);

		await workspace.SelectDocumentAsync(staleNode, CancellationToken.None);

		workspace.SelectedNode.ShouldNotBeNull().Title.ShouldBe("README.md");
	}

	[Test]
	public async Task Selecting_a_folder_keeps_the_active_document_and_the_remembered_path()
	{
		Write("README.md", "# Readme");
		Write("src/Service/details.md", "# Details");
		var workspace = CreateWorkspace();
		await workspace.RefreshAsync(CancellationToken.None);
		await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
		var folder = workspace.CommonTree.Single(node => node.IsFolder);

		await workspace.SelectDocumentAsync(folder, CancellationToken.None);

		workspace.SelectedNode.ShouldBeSameAs(folder);
		folder.IsExpanded.ShouldBeFalse();
		(workspace.ActiveDocument?.Text).ShouldBe("# Readme");

		await workspace.SelectSectionAsync(DocsAndNotesSection.Notes, CancellationToken.None);
		await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);

		(workspace.ActiveDocument?.Text).ShouldBe("# Readme");
	}

	[Test]
	public async Task ToggleFolder_flips_expansion_and_ignores_documents_and_stale_nodes()
	{
		Write("README.md", "# Readme");
		Write("src/Service/details.md", "# Details");
		var workspace = CreateWorkspace();
		await workspace.RefreshAsync(CancellationToken.None);
		await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
		var folder = workspace.CommonTree.Single(node => node.IsFolder);
		var readme = workspace.CommonTree.Single(node => !node.IsFolder);

		workspace.ToggleFolder(folder);
		folder.IsExpanded.ShouldBeTrue();

		workspace.ToggleFolder(folder);
		folder.IsExpanded.ShouldBeFalse();

		workspace.ToggleFolder(readme);
		workspace.SelectedNode.ShouldBeSameAs(readme);
		(workspace.ActiveDocument?.Text).ShouldBe("# Readme");
	}

	[Test]
	public async Task Refresh_keeps_editor_buffers_expansion_and_selection()
	{
		Write("README.md", "# Readme");
		Write("src/Service/details.md", "# Details");
		var workspace = CreateWorkspace();
		await workspace.RefreshAsync(CancellationToken.None);
		await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
		var details = workspace.CommonTree
			.Single(node => node.IsFolder).Children
			.Single(node => node.IsFolder).Children
			.Single();
		await workspace.SelectDocumentAsync(details, CancellationToken.None);
		workspace.ActiveDocument!.SetText("# Edited");

		await workspace.RefreshAsync(CancellationToken.None);

		(workspace.ActiveDocument?.Text).ShouldBe("# Edited");
		workspace.CommonTree.Single(node => node.IsFolder).IsExpanded.ShouldBeTrue();
	}

	[Test]
	public async Task A_remembered_document_that_disappeared_falls_back_to_no_document()
	{
		Write("docs/notes.md", "# Notes");
		var workspace = CreateWorkspace();
		await workspace.RefreshAsync(CancellationToken.None);
		await workspace.SelectSectionAsync(DocsAndNotesSection.Docs, CancellationToken.None);
		await workspace.SelectDocumentAsync(workspace.DocsTree.Single(), CancellationToken.None);
		File.Delete(Path.Combine(_root, "docs", "notes.md"));

		await workspace.RefreshAsync(CancellationToken.None);

		workspace.DocsTree.ShouldBeEmpty();
		workspace.ActiveDocument.ShouldBeNull();
	}

	[Test]
	public async Task FlushAsync_attempts_every_document_before_reporting_failures()
	{
		Write("README.md", "before");
		RecordingNotesStore notesStore = new();
		RecordingMarkdownFileStore markdownStore = new();
		ProjectNoteDocument notes = new(notesStore, _root, TimeSpan.FromHours(1));
		DocsAndNotesWorkspaceViewModel workspace = new(
			_root,
			notes,
			markdownStore,
			TimeSpan.FromHours(1));
		await workspace.RefreshAsync(CancellationToken.None);
		await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
		notes.SetText("one");
		notesStore.FailNextSave = true;
		workspace.ActiveDocument!.SetText("two");

		var error = await Should.ThrowAsync<AggregateException>(
			() => workspace.FlushAsync(CancellationToken.None));

		workspace.NotesDocument.ShouldBeSameAs(notes);
		error.InnerExceptions.Count.ShouldBe(1);
		notesStore.SaveAttempts.ShouldBe(1);
		markdownStore.SavedTexts.ShouldContain("two");
	}

	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}

	private DocsAndNotesWorkspaceViewModel CreateWorkspace()
	{
		ProjectNoteDocument notes = new(
			new EmptyNotesStore(),
			_root,
			TimeSpan.FromHours(1));
		return new DocsAndNotesWorkspaceViewModel(
			_root,
			notes,
			new ProjectMarkdownFileStore(),
			TimeSpan.FromHours(1));
	}

	private void Write(string relativePath, string text)
	{
		var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
	}

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);

		public Task SaveAsync(string projectRootPath, string text, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task AppendAsync(string projectRootPath, string text, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}

	private sealed class FailingNotesStore : IProjectNotesStore
	{
		private bool _failNext = true;
		public string Saved { get; private set; } = string.Empty;

		public Task<string> LoadAsync(
			string projectRootPath,
			CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);

		public Task SaveAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken)
		{
			if (_failNext)
			{
				_failNext = false;
				throw new IOException("notes failed");
			}

			Saved = text;
			return Task.CompletedTask;
		}

		public Task AppendAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}

	private sealed class RecordingNotesStore : IProjectNotesStore
	{
		public bool FailNextSave { get; set; }
		public int SaveAttempts { get; private set; }

		public Task<string> LoadAsync(
			string projectRootPath,
			CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);

		public Task SaveAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken)
		{
			SaveAttempts++;
			if (FailNextSave)
			{
				FailNextSave = false;
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

	private sealed class RecordingMarkdownFileStore : IProjectMarkdownFileStore
	{
		public List<string> SavedTexts { get; } = [];

		public Task<ProjectMarkdownFileSnapshot> LoadAsync(
			string path,
			CancellationToken cancellationToken) =>
			Task.FromResult(new ProjectMarkdownFileSnapshot(true, "before", "r1"));

		public Task<ProjectMarkdownSaveResult> TrySaveAsync(
			string path,
			string text,
			string expectedRevision,
			CancellationToken cancellationToken)
		{
			SavedTexts.Add(text);
			ProjectMarkdownFileSnapshot snapshot = new(true, text, "r2");
			return Task.FromResult(new ProjectMarkdownSaveResult(true, snapshot));
		}

		public Task<ProjectMarkdownFileSnapshot> OverwriteAsync(
			string path,
			string text,
			CancellationToken cancellationToken)
		{
			SavedTexts.Add(text);
			return Task.FromResult(new ProjectMarkdownFileSnapshot(true, text, "r2"));
		}
	}

	private sealed class BlockingMarkdownFileStore : IProjectMarkdownFileStore
	{
		public bool FailNextSave { get; set; }

		public TaskCompletionSource? BlockNextSave { get; set; }

		public async Task<ProjectMarkdownFileSnapshot> LoadAsync(
			string path,
			CancellationToken cancellationToken)
		{
			var text = File.Exists(path)
				? await File.ReadAllTextAsync(path, cancellationToken)
				: string.Empty;
			return new ProjectMarkdownFileSnapshot(File.Exists(path), text, "r1");
		}

		public async Task<ProjectMarkdownSaveResult> TrySaveAsync(
			string path,
			string text,
			string expectedRevision,
			CancellationToken cancellationToken)
		{
			if (BlockNextSave is { } gate)
			{
				BlockNextSave = null;
				await gate.Task;
			}

			if (FailNextSave)
			{
				FailNextSave = false;
				throw new IOException("save failed");
			}

			return new ProjectMarkdownSaveResult(
				true,
				new ProjectMarkdownFileSnapshot(true, text, "r2"));
		}

		public Task<ProjectMarkdownFileSnapshot> OverwriteAsync(
			string path,
			string text,
			CancellationToken cancellationToken) =>
			Task.FromResult(new ProjectMarkdownFileSnapshot(true, text, "r2"));
	}
}
