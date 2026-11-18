using Pact.Core.Boards;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.Services;

public sealed class ProjectBoardWorkspaceTests : IDisposable
{
	private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
	private ProjectBoardWorkspace CreateWorkspace() => new(new ProjectBoardStore(new(_directory.Path), TimeProvider.System), @"D:\project");
	public void Dispose() => _directory.Dispose();

	[Test]
	public async Task Conflicting_text_does_not_block_a_pure_reorder()
	{
		using var workspace = CreateWorkspace();
		using ProjectBoardViewModel board = new(workspace, action => action(), () => [], TimeProvider.System);
		await board.CreateAsync("First", CancellationToken.None);
		var first = board.SelectedCard!.Id;
		await board.CreateAsync("Second", CancellationToken.None);
		var second = board.SelectedCard!;
		board.Editor!.Description = "Mine";
		await workspace.ExecuteAsync(new UpdateCard(second.Id, BoardRevisions.ForCard(second), new() { Description = new("Agent") }), new("agent", "Agent"), CancellationToken.None);
		(await board.MoveAsync(second.Id, second.Status, first, CancellationToken.None)).ShouldBeTrue();
		board.Cards.Select(card => card.Title).ShouldBe(["Second", "First"]);
		board.Editor.Description.ShouldBe("Mine");
		board.Editor.SaveStatus.State.ShouldBe(DocumentSaveState.Conflict);
	}

	[Test]
	public async Task Failed_board_does_not_skip_saving_notes()
	{
		AppPaths paths = new(_directory.Path);
		var store = new ControlledStore();
		ProjectNotesStore notes = new(paths);
		MainWindowViewModel main = new(new JsonProjectStore(paths), notes, new ProjectMarkdownFileStore(),
			new TerminalTabStatusCoordinator(action => action()), boardStore: store);
		try
		{
			var workspace = main.GetOrCreateProjectBoard(@"D:\project");
			var created = await workspace.ExecuteAsync(new CreateCard("Task", "", null), new(null, "You"), CancellationToken.None);
			using BoardCardEditorViewModel editor = new(workspace, created.CardId!, action => action(), TimeProvider.System, TimeSpan.FromHours(1));
			editor.Description = "Pending";
			var document = main.GetOrCreateDocsAndNotesWorkspace(@"D:\project").NotesDocument;
			await document.LoadAsync(CancellationToken.None);
			document.SetText("Save these notes");
			store.Fail = true;
			await Should.ThrowAsync<AggregateException>(() => main.FlushAllNoteDocumentsAsync(CancellationToken.None));
			(await notes.LoadAsync(@"D:\project", CancellationToken.None)).ShouldBe("Save these notes");
			editor.Description.ShouldBe("Pending");
		}
		finally
		{
			main.DisposeBoards();
			main.Attention.Dispose();
		}
	}

	[Test]
	public async Task Corrupt_file_surfaces_error_and_retains_bytes_and_editor_text()
	{
		using var workspace = CreateWorkspace();
		using ProjectBoardViewModel board = new(workspace, action => action(), () => [], TimeProvider.System);
		await board.CreateAsync("Task", CancellationToken.None);
		board.Editor!.Description = "Keep my text";
		var path = Path.Combine(new AppPaths(_directory.Path).BoardsDirectory, Pact.Core.Projects.ProjectNoteFileKey.FromRootPath(@"D:\project") + ".json");
		await File.WriteAllTextAsync(path, "{ damaged");
		(await board.LoadAsync(CancellationToken.None)).ShouldBeFalse();
		board.Error.ShouldNotBeNullOrEmpty();
		await board.Editor.FlushAsync(CancellationToken.None);
		board.Editor.SaveStatus.State.ShouldBe(DocumentSaveState.Failed);
		board.Editor.Description.ShouldBe("Keep my text");
		(await File.ReadAllTextAsync(path)).ShouldBe("{ damaged");
	}

	[Test]
	public async Task Saving_and_comments_preserve_trailing_space_while_typing_title()
	{
		using var workspace = CreateWorkspace();
		var created = await workspace.ExecuteAsync(new CreateCard("Task", "", null), new(null, "You"), CancellationToken.None);
		using BoardCardEditorViewModel editor = new(workspace, created.CardId!, action => action(), TimeProvider.System, TimeSpan.FromHours(1));
		editor.Title = "Hello ";
		await editor.FlushAsync(CancellationToken.None);
		editor.Title.ShouldBe("Hello ");
		await workspace.ExecuteAsync(new AddComment(created.CardId!, "Result"), new("agent", "Agent"), CancellationToken.None);
		editor.Title.ShouldBe("Hello ");
		editor.Title += "world";
		await editor.FlushAsync(CancellationToken.None);
		workspace.Snapshot!.State.Cards.Single().Title.ShouldBe("Hello world");
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task Save_rechecks_a_racing_field_change_before_retry(bool sameField)
	{
		var store = new ControlledStore();
		using ProjectBoardWorkspace workspace = new(store, @"D:\project");
		var created = await workspace.ExecuteAsync(new CreateCard("Task", "Base", null), new(null, "You"), CancellationToken.None);
		using BoardCardEditorViewModel editor = new(workspace, created.CardId!, action => action(), TimeProvider.System, TimeSpan.FromHours(1));
		editor.Description = "Mine";
		store.BeforeUpdate = state => state with { Cards = [state.Cards.Single() with { Status = BoardCardStatus.Verification, Description = sameField ? "Agent" : "Base" }] };
		await editor.FlushAsync(CancellationToken.None);
		editor.SaveStatus.State.ShouldBe(sameField ? DocumentSaveState.Conflict : DocumentSaveState.Clean);
		workspace.Snapshot!.State.Cards.Single().Description.ShouldBe(sameField ? "Agent" : "Mine");
		workspace.Snapshot.State.Cards.Single().Status.ShouldBe(BoardCardStatus.Verification);
	}

	[Test]
	public async Task Autosave_waits_for_injected_debounce_and_saves_latest_text()
	{
		ManualTimeProvider time = new();
		using ProjectBoardWorkspace workspace = new(new ControlledStore(), @"D:\project");
		var created = await workspace.ExecuteAsync(new CreateCard("Task", "Base", null), new(null, "You"), CancellationToken.None);
		var debounce = TimeSpan.FromSeconds(5);
		using BoardCardEditorViewModel editor = new(workspace, created.CardId!, action => action(), time, debounce);
		TaskCompletionSource saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
		editor.PropertyChanged += (_, _) =>
		{
			if (editor.SaveStatus.State == DocumentSaveState.Clean)
			{
				saved.TrySetResult();
			}
		};
		editor.Description = "First";
		editor.Description = "Latest";
		await time.WaitForTimerCountAsync(debounce, 2).WaitAsync(TimeSpan.FromSeconds(1));
		time.Advance(TimeSpan.FromSeconds(4));
		workspace.Snapshot!.State.Cards.Single().Description.ShouldBe("Base");
		time.Advance(TimeSpan.FromSeconds(1));
		await saved.Task.WaitAsync(TimeSpan.FromSeconds(1));
		workspace.Snapshot.State.Cards.Single().Description.ShouldBe("Latest");
	}

	[Test]
	public async Task Deleted_assignee_preserves_card_and_disables_navigation()
	{
		var now = DateTimeOffset.UtcNow;
		System.Collections.ObjectModel.ObservableCollection<SessionViewModel> sessions =
		[
			new(new Pact.Core.Sessions.SessionRecord("agent", Pact.Core.Agents.AgentKind.Codex,
				"Agent", @"D:\project", "codex", null, Pact.Core.Sessions.SessionStatus.Stopped, now, now))
		];
		using var workspace = CreateWorkspace();
		using ProjectBoardViewModel board = new(workspace, action => action(), () => sessions.ToArray(), TimeProvider.System, sessions);
		await board.CreateAsync("Task", CancellationToken.None);
		await board.UpdateAsync(new() { AssignedSessionId = new("agent"), AssignedSessionName = new("Agent") }, CancellationToken.None);
		var notified = false;
		board.PropertyChanged += (_, args) => notified |= args.PropertyName == nameof(board.Sessions);
		board.CanNavigateToAssignee(board.SelectedCard!).ShouldBeTrue();
		sessions.Clear();
		notified.ShouldBeTrue();
		board.CanNavigateToAssignee(board.SelectedCard!).ShouldBeFalse();
		board.AssigneeName(board.SelectedCard!).ShouldBe("Agent");
		board.SelectedCard!.Status.ShouldBe(BoardCardStatus.Backlog);
		board.SelectedCard.AssignedSessionId.ShouldBe("agent");
	}

	[Test]
	public async Task Normalized_root_uses_one_owner_before_a_board_view_opens()
	{
		AppPaths paths = new(_directory.Path);
		MainWindowViewModel main = new(new JsonProjectStore(paths), new ProjectNotesStore(paths),
			new ProjectMarkdownFileStore(), new TerminalTabStatusCoordinator(action => action()),
			boardStore: new ProjectBoardStore(paths, TimeProvider.System));
		try
		{
			var owner = main.GetOrCreateProjectBoard(@"D:\Project\");
			await owner.ExecuteAsync(new CreateCard("Before UI", "", null), new("agent", "Agent"), CancellationToken.None);
			var same = main.GetOrCreateProjectBoard("d:/project");
			same.ShouldBeSameAs(owner);
			same.Snapshot!.State.Cards.Single().Title.ShouldBe("Before UI");
		}
		finally
		{
			main.DisposeBoards();
			main.Attention.Dispose();
		}
	}

	[Test]
	public async Task Drag_started_before_a_reorder_is_rejected_without_overwriting_order()
	{
		using var workspace = CreateWorkspace();
		using ProjectBoardViewModel board = new(workspace, action => action(), () => [], TimeProvider.System);
		await board.CreateAsync("First", CancellationToken.None);
		var first = board.SelectedCard!.Id;
		await board.CreateAsync("Second", CancellationToken.None);
		var second = board.SelectedCard!.Id;
		var dragToken = workspace.Snapshot!.OrderRevision;
		await board.MoveAsync(second, BoardCardStatus.Backlog, first, CancellationToken.None);
		(await board.MoveAsync(first, BoardCardStatus.Backlog, second, CancellationToken.None, dragToken)).ShouldBeFalse();
		board.Error.ShouldNotBeNull().ShouldContain("Conflict");
		board.Cards.Select(card => card.Title).ShouldBe(["Second", "First"]);
	}

	[Test]
	public async Task Failed_save_keeps_text_and_can_be_retried()
	{
		var store = new ControlledStore();
		using var workspace = new ProjectBoardWorkspace(store, @"D:\project");
		var first = await workspace.ExecuteAsync(new CreateCard("Task", "Base", null), new(null, "You"), CancellationToken.None);
		using var editor = new BoardCardEditorViewModel(workspace, first.CardId!, action => action(), TimeProvider.System, TimeSpan.FromHours(1));
		editor.Description = "Mine";
		store.Fail = true;
		await editor.FlushAsync(CancellationToken.None);
		editor.SaveStatus.State.ShouldBe(DocumentSaveState.Failed);
		editor.Description.ShouldBe("Mine");
		store.Fail = false;
		await workspace.FlushAsync(CancellationToken.None);
		workspace.Snapshot!.State.Cards.Single().Description.ShouldBe("Mine");
	}

	[Test]
	public async Task Edit_during_save_survives_older_completion()
	{
		var store = new ControlledStore();
		using var workspace = new ProjectBoardWorkspace(store, @"D:\project");
		var first = await workspace.ExecuteAsync(new CreateCard("Task", "Base", null), new(null, "You"), CancellationToken.None);
		using var editor = new BoardCardEditorViewModel(workspace, first.CardId!, action => action(), TimeProvider.System, TimeSpan.FromHours(1));
		editor.Description = "First";
		store.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var save = editor.FlushAsync(CancellationToken.None);
		await store.Started.Task;
		editor.Description = "Second";
		store.Release.SetResult();
		await save;
		editor.Description.ShouldBe("Second");
		editor.SaveStatus.State.ShouldBe(DocumentSaveState.Dirty);
		await workspace.FlushAsync(CancellationToken.None);
		workspace.Snapshot!.State.Cards.Single().Description.ShouldBe("Second");
	}

	private sealed class ControlledStore : IProjectBoardStore
	{
		private BoardState _state = BoardState.Empty;
		public bool Fail { get; set; }
		public Func<BoardState, BoardState>? BeforeUpdate { get; set; }
		public TaskCompletionSource? Release { get; set; }
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task<BoardSnapshot> LoadAsync(string projectRootPath, CancellationToken cancellationToken) =>
			Task.FromResult(new BoardSnapshot(_state, "disk", BoardRevisions.ForOrder(_state)));
		public async Task<BoardMutationResult> ExecuteAsync(string projectRootPath, BoardCommand command, BoardActor actor, CancellationToken cancellationToken)
		{
			if (Fail)
			{
				throw new IOException("Disk is unavailable.");
			}
			if (Release is { } release)
			{
				Started.TrySetResult();
				await release.Task;
			}
			if (command is UpdateCard && BeforeUpdate is { } before)
			{
				BeforeUpdate = null;
				_state = before(_state);
			}
			var transition = BoardRules.Apply(_state, command, actor, DateTimeOffset.UtcNow);
			_state = transition.State;
			return new(transition.Outcome, await LoadAsync(projectRootPath, cancellationToken), transition.CardId, transition.CommentId);
		}
	}

	[Test]
	public async Task Commands_publish_saved_snapshot_before_returning()
	{
		using var workspace = CreateWorkspace();
		BoardSnapshot? published = null;
		workspace.SnapshotChanged += (_, snapshot) => published = snapshot;
		await workspace.ExecuteAsync(new CreateCard("Task", "", null), new(null, "You"), CancellationToken.None);
		published.ShouldNotBeNull();
		published.State.Cards.Single().Title.ShouldBe("Task");
		using var reopened = CreateWorkspace();
		(await reopened.LoadAsync(CancellationToken.None)).State.Cards.Single().Title.ShouldBe("Task");
	}

	[Test]
	public async Task Other_comment_does_not_conflict_with_dirty_description()
	{
		using var workspace = CreateWorkspace();
		var first = await workspace.ExecuteAsync(new CreateCard("Task", "Base", null), new(null, "You"), CancellationToken.None);
		using var editor = new BoardCardEditorViewModel(workspace, first.CardId!, action => action(), TimeProvider.System, TimeSpan.FromHours(1));
		editor.Description = "Mine";
		await workspace.ExecuteAsync(new AddComment(first.CardId!, "Agent result"), new("agent", "Agent"), CancellationToken.None);
		await editor.FlushAsync(CancellationToken.None);
		editor.SaveStatus.State.ShouldBe(DocumentSaveState.Clean);
		workspace.Snapshot!.State.Cards.Single().Description.ShouldBe("Mine");
		workspace.Snapshot.State.Cards.Single().Comments.Single().Text.ShouldBe("Agent result");
	}

	[Test]
	public async Task Same_description_edit_preserves_buffer_and_conflicts()
	{
		using var workspace = CreateWorkspace();
		var first = await workspace.ExecuteAsync(new CreateCard("Task", "Base", null), new(null, "You"), CancellationToken.None);
		using var editor = new BoardCardEditorViewModel(workspace, first.CardId!, action => action(), TimeProvider.System, TimeSpan.FromHours(1));
		editor.Description = "Mine";
		var card = first.Snapshot.State.Cards.Single();
		await workspace.ExecuteAsync(new UpdateCard(card.Id, BoardRevisions.ForCard(card), new() { Description = new("Agent") }), new("agent", "Agent"), CancellationToken.None);
		await editor.FlushAsync(CancellationToken.None);
		editor.Description.ShouldBe("Mine");
		editor.SaveStatus.State.ShouldBe(DocumentSaveState.Conflict);
		workspace.Snapshot!.State.Cards.Single().Description.ShouldBe("Agent");
		await editor.SaveMineAsync(CancellationToken.None);
		workspace.Snapshot.State.Cards.Single().Description.ShouldBe("Mine");
	}

	[Test]
	public async Task Unrelated_field_edit_rebases_only_user_fields()
	{
		using var workspace = CreateWorkspace();
		var first = await workspace.ExecuteAsync(new CreateCard("Title", "Base", null), new(null, "You"), CancellationToken.None);
		using var editor = new BoardCardEditorViewModel(workspace, first.CardId!, action => action(), TimeProvider.System, TimeSpan.FromHours(1));
		editor.Description = "Mine";
		var card = first.Snapshot.State.Cards.Single();
		await workspace.ExecuteAsync(new UpdateCard(card.Id, BoardRevisions.ForCard(card), new() { Title = new("Renamed") }), new("agent", "Agent"), CancellationToken.None);
		await editor.FlushAsync(CancellationToken.None);
		workspace.Snapshot!.State.Cards.Single().Title.ShouldBe("Renamed");
		workspace.Snapshot.State.Cards.Single().Description.ShouldBe("Mine");
		editor.SaveStatus.State.ShouldBe(DocumentSaveState.Clean);
	}
}
