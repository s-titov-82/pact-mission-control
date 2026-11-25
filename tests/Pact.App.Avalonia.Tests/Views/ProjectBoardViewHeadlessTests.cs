using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Views;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Core.Boards;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class ProjectBoardViewHeadlessTests
{
	[AvaloniaTest]
	public async Task Pointer_drag_on_card_button_reaches_native_drag_boundary()
	{
		await WithBoard(async (board, view, window) =>
		{
			await board.CreateAsync("Drag me", CancellationToken.None);
			await board.OpenAsync(null, CancellationToken.None);
			window.UpdateLayout();
			var card = CardButtons(view).Single();
			var position = card.TranslatePoint(new Point(20, 20), window)!.Value;
			var started = 0;
			var presses = 0;
			var moves = 0;
			card.AddHandler(global::Avalonia.Input.InputElement.PointerPressedEvent, (_, _) => presses++, RoutingStrategies.Tunnel, handledEventsToo: true);
			card.AddHandler(global::Avalonia.Input.InputElement.PointerMovedEvent, (_, _) => moves++, RoutingStrategies.Tunnel, handledEventsToo: true);
			view.BeginDragAsync = (_, _) => { started++; return Task.FromResult(global::Avalonia.Input.DragDropEffects.None); };
			window.MouseMove(position);
			window.MouseDown(position, global::Avalonia.Input.MouseButton.Left);
			window.MouseMove(new Point(position.X + 20, position.Y + 20), global::Avalonia.Input.RawInputModifiers.LeftMouseButton);
			window.MouseUp(new Point(position.X + 20, position.Y + 20), global::Avalonia.Input.MouseButton.Left);
			started.ShouldBe(1, $"presses={presses}, moves={moves}, bounds={card.Bounds}, point={position}");
			board.Cards.Single().Title.ShouldBe("Drag me");
		});
	}

	[AvaloniaTest]
	public async Task Dropping_in_empty_column_space_appends_to_the_end()
	{
		await WithBoard(async (board, view, window) =>
		{
			foreach (var title in new[] { "First", "Second", "Third" })
			{
				await board.CreateAsync(title, CancellationToken.None);
				await board.UpdateAsync(new() { Status = new(BoardCardStatus.Ready) }, CancellationToken.None);
			}
			await board.OpenAsync(null, CancellationToken.None);

			DropFirstCard(belowLast: true);
			board.Cards.Select(card => card.Title).ShouldBe(["Second", "Third", "First"]);
			DropFirstCard(belowLast: false);
			board.Cards.Select(card => card.Title).ShouldBe(["Third", "First", "Second"]);

			void DropFirstCard(bool belowLast)
			{
				window.UpdateLayout();
				var cards = CardButtons(view).ToArray();
				var start = cards[0].TranslatePoint(new Point(20, 20), window)!.Value;
				var anchor = belowLast ? cards[^1] : cards[0];
				var drop = anchor.TranslatePoint(new Point(40, anchor.Bounds.Height + (belowLast ? 40 : 4)), window)!.Value;
				view.BeginDragAsync = (_, transfer) =>
				{
					var target = window.InputHitTest(drop).ShouldBeAssignableTo<Control>().ShouldNotBeNull();
					var args = new DragEventArgs(DragDrop.DropEvent, transfer, target, window.TranslatePoint(drop, target)!.Value, KeyModifiers.None);
					if (DragDrop.GetAllowDrop(target))
					{
						target.RaiseEvent(args);
					}
					return Task.FromResult(args.DragEffects);
				};
				window.MouseMove(start);
				window.MouseDown(start, MouseButton.Left);
				var moved = new Point(start.X + 20, start.Y + 20);
				window.MouseMove(moved, RawInputModifiers.LeftMouseButton);
				window.MouseUp(moved, MouseButton.Left);
			}
		});
	}

	[AvaloniaTest]
	public async Task One_create_button_opens_backlog_card_and_close_restores_focus()
	{
		await WithBoard(async (board, view, window) =>
		{
			view.GetLogicalDescendants().OfType<Button>().Count(button => Equals(button.Content, "+ Create card")).ShouldBe(1);
			Click(view, "CreateButton");
			board.Cards.Single().Status.ShouldBe(BoardCardStatus.Backlog);
			view.FindControl<Border>("Drawer")!.IsVisible.ShouldBeTrue();
			view.FindControl<TextBox>("TitleBox")!.Text = "A task";
			await board.Workspace.FlushAsync(CancellationToken.None);
			board.Cards.Single().Title.ShouldBe("A task");
			window.UpdateLayout();
			view.FindControl<Button>("AddCommentButton")!.Focus().ShouldBeTrue();
			window.KeyPress(global::Avalonia.Input.Key.Tab, global::Avalonia.Input.RawInputModifiers.None, global::Avalonia.Input.PhysicalKey.Tab, null);
			window.KeyRelease(global::Avalonia.Input.Key.Tab, global::Avalonia.Input.RawInputModifiers.None, global::Avalonia.Input.PhysicalKey.Tab, null);
			var focused = window.FocusManager!.GetFocusedElement().ShouldBeAssignableTo<Control>().ShouldNotBeNull();
			focused.GetVisualAncestors().ShouldContain(view.FindControl<Border>("Drawer")!);
			Click(view, "CloseButton");
			view.FindControl<Border>("Drawer")!.IsVisible.ShouldBeFalse();
			view.FindControl<Button>("CreateButton")!.IsFocused.ShouldBeTrue();
		});
	}

	[AvaloniaTest]
	public async Task Status_comments_archive_and_restore_work_through_controls()
	{
		await WithBoard(async (board, view, window) =>
		{
			Click(view, "CreateButton");
			view.FindControl<TextBox>("JiraBox")!.Text = "https://jira.example/PROJ-1";
			Click(view, "SaveJiraButton");
			string? opened = null;
			view.OpenJiraRequested += (_, card) => opened = card.JiraUrl;
			Click(view, "OpenJiraButton");
			opened.ShouldBe("https://jira.example/PROJ-1");
			view.FindControl<ComboBox>("StatusBox")!.SelectedIndex = (int)BoardCardStatus.Verification;
			board.SelectedCard!.Status.ShouldBe(BoardCardStatus.Verification);
			view.FindControl<TextBox>("CommentBox")!.Text = "Checked";
			Click(view, "AddCommentButton");
			board.SelectedCard.Comments.Single().Text.ShouldBe("Checked");
			view.FindControl<TextBox>("CommentBox")!.Text.ShouldBeEmpty();
			var delete = view.FindControl<StackPanel>("CommentsPanel")!.GetLogicalDescendants().OfType<Button>().Single();
			delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			board.SelectedCard.Comments.ShouldBeEmpty();
			view.FindControl<ComboBox>("StatusBox")!.SelectedIndex = (int)BoardCardStatus.Done;
			var id = board.SelectedCard.Id;
			Click(view, "ArchiveCardButton");
			board.Cards.ShouldBeEmpty();
			Click(view, "ArchiveButton");
			board.Cards.Single().Id.ShouldBe(id);
			await board.OpenAsync(id, CancellationToken.None);
			view.FindControl<TextBox>("TitleBox")!.IsReadOnly.ShouldBeTrue();
			Click(view, "ArchiveCardButton");
			Click(view, "ArchiveButton");
			board.Cards.Single().Status.ShouldBe(BoardCardStatus.Done);
		});
	}

	[AvaloniaTest]
	public async Task Delete_without_description_removes_card_and_closes_editor_without_a_dialog()
	{
		await WithBoard(async (board, view, window) =>
		{
			await board.CreateAsync("Empty brief", CancellationToken.None);
			Click(view, "DeleteCardButton");
			window.OwnedWindows.ShouldBeEmpty();
			board.Cards.ShouldBeEmpty();
			view.FindControl<Border>("Drawer")!.IsVisible.ShouldBeFalse();
		});
	}

	[AvaloniaTest]
	public async Task Delete_with_description_requires_confirmation_and_keeps_a_declined_card()
	{
		await WithBoard(async (board, view, window) =>
		{
			await board.CreateAsync("Has a brief", CancellationToken.None);
			view.FindControl<TextBox>("DescriptionBox")!.Text = "Pending Markdown";
			Click(view, "DeleteCardButton");
			board.Cards.Single().Title.ShouldBe("Has a brief");
			Answer(window, "NoButton");
			board.SelectedCard!.Description.ShouldBe("Pending Markdown");
			Click(view, "DeleteCardButton");
			Answer(window, "YesButton");
			board.Cards.ShouldBeEmpty();
		});
	}

	[AvaloniaTest]
	public async Task Archive_all_requires_confirmation_and_moves_only_done_cards()
	{
		await WithBoard(async (board, view, window) =>
		{
			ArchiveAllButton(view).IsEnabled.ShouldBeFalse();
			await board.CreateAsync("Keep active", CancellationToken.None);
			await board.CreateAsync("First done", CancellationToken.None);
			await board.UpdateAsync(new() { Status = new(BoardCardStatus.Done) }, CancellationToken.None);
			board.CommentText = "Keep this discussion";
			await board.AddCommentAsync(CancellationToken.None);
			await board.CreateAsync("Second done", CancellationToken.None);
			await board.UpdateAsync(new() { Status = new(BoardCardStatus.Done) }, CancellationToken.None);
			await board.OpenAsync(null, CancellationToken.None);

			ArchiveAllButton(view).IsEnabled.ShouldBeTrue();
			ArchiveAllButton(view).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			board.Cards.Count.ShouldBe(3);
			Answer(window, "NoButton");
			board.Cards.Count.ShouldBe(3);
			ArchiveAllButton(view).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Answer(window, "YesButton");
			board.Cards.Select(card => card.Title).ShouldBe(["Keep active"]);
			ArchiveAllButton(view).IsEnabled.ShouldBeFalse();

			Click(view, "ArchiveButton");
			view.GetLogicalDescendants().OfType<Button>().ShouldNotContain(button => Equals(button.Content, "Archive all"));
			board.Cards.Select(card => card.Title).ShouldBe(["First done", "Second done"]);
			board.Cards[0].Comments.Single().Text.ShouldBe("Keep this discussion");
		});
	}

	[AvaloniaTest]
	public async Task Archive_all_rejects_a_card_added_to_done_while_confirming()
	{
		await WithBoard(async (board, view, window) =>
		{
			await board.CreateAsync("Already done", CancellationToken.None);
			await board.UpdateAsync(new() { Status = new(BoardCardStatus.Done) }, CancellationToken.None);
			await board.CreateAsync("Newly done", CancellationToken.None);
			var pending = board.SelectedCard.ShouldNotBeNull();
			await board.OpenAsync(null, CancellationToken.None);

			ArchiveAllButton(view).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			window.OwnedWindows.OfType<MessageDialogWindow>().ShouldHaveSingleItem();
			await board.Workspace.ExecuteAsync(new UpdateCard(pending.Id, BoardRevisions.ForCard(pending), new() { Status = new(BoardCardStatus.Done) }),
				new("agent", "Agent"), CancellationToken.None);
			Answer(window, "YesButton");

			board.Cards.Select(card => card.Title).ShouldBe(["Already done", "Newly done"]);
			view.FindControl<TextBlock>("ErrorText")!.Text.ShouldNotBeNull().ShouldContain("The Done column changed");
		});
	}

	[AvaloniaTest]
	public async Task Clean_archive_requires_confirmation_and_disables_its_button_when_empty()
	{
		await WithBoard(async (board, view, window) =>
		{
			await board.CreateAsync("Keep active", CancellationToken.None);
			await board.CreateAsync("Archive me", CancellationToken.None);
			await board.UpdateAsync(new() { Status = new(BoardCardStatus.Done) }, CancellationToken.None);
			await board.ArchiveAsync(CancellationToken.None);
			var clean = view.FindControl<Button>("CleanArchiveButton").ShouldNotBeNull();
			clean.IsVisible.ShouldBeFalse();
			Click(view, "ArchiveButton");
			clean.IsVisible.ShouldBeTrue();
			clean.IsEnabled.ShouldBeTrue();
			await board.OpenAsync(board.Cards.Single().Id, CancellationToken.None);
			Click(view, "CleanArchiveButton");
			Answer(window, "NoButton");
			board.SelectedCard!.Title.ShouldBe("Archive me");
			Click(view, "CleanArchiveButton");
			Answer(window, "YesButton");
			board.Cards.ShouldBeEmpty();
			view.FindControl<Border>("Drawer")!.IsVisible.ShouldBeFalse();
			clean.IsEnabled.ShouldBeFalse();
			Click(view, "ArchiveButton");
			board.Cards.Single().Title.ShouldBe("Keep active");
		});
	}

	[AvaloniaTest]
	public async Task Delete_rejects_a_comment_added_while_confirming_and_keeps_the_editor()
	{
		await WithBoard(async (board, view, window) =>
		{
			await board.CreateAsync("Discussed card", CancellationToken.None);
			var editor = board.Editor.ShouldNotBeNull();
			editor.Description = "A brief";
			Click(view, "DeleteCardButton");
			window.OwnedWindows.OfType<MessageDialogWindow>().ShouldHaveSingleItem();
			await board.Workspace.ExecuteAsync(new AddComment(editor.CardId, "New comment"), new("agent", "Agent"), CancellationToken.None);
			Answer(window, "YesButton");
			board.SelectedCard!.Comments.Single().Text.ShouldBe("New comment");
			board.Editor.ShouldBeSameAs(editor);
			var drawer = view.FindControl<Border>("Drawer").ShouldNotBeNull();
			drawer.IsVisible.ShouldBeTrue();
			drawer.IsEnabled.ShouldBeTrue();
			view.FindControl<TextBlock>("ErrorText")!.Text.ShouldNotBeNull().ShouldContain("Conflict");
		});
	}

	[AvaloniaTest]
	public async Task Delete_preparation_keeps_its_save_error_visible_after_a_board_refresh()
	{
		await WithBoard(async (board, view, window) =>
		{
			await board.CreateAsync("Edited card", CancellationToken.None);
			var editor = board.Editor.ShouldNotBeNull();
			var card = board.SelectedCard.ShouldNotBeNull();
			editor.Description = "Local brief";
			await board.Workspace.ExecuteAsync(new UpdateCard(card.Id, BoardRevisions.ForCard(card), new() { Description = new("Agent brief") }),
				new("agent", "Agent"), CancellationToken.None);
			Click(view, "DeleteCardButton");
			window.OwnedWindows.ShouldBeEmpty();
			await board.Workspace.LoadAsync(CancellationToken.None);
			view.FindControl<TextBlock>("ErrorText")!.Text.ShouldNotBeNull().ShouldContain("unsaved edits");
			board.Editor.ShouldBeSameAs(editor);
			editor.Description.ShouldBe("Local brief");
			editor.Reload();
		});
	}

	[AvaloniaTest]
	public async Task Clean_archive_rejects_an_archive_change_while_confirming()
	{
		await WithBoard(async (board, view, window) =>
		{
			await board.CreateAsync("Already archived", CancellationToken.None);
			await board.UpdateAsync(new() { Status = new(BoardCardStatus.Done) }, CancellationToken.None);
			await board.ArchiveAsync(CancellationToken.None);
			await board.CreateAsync("New archive entry", CancellationToken.None);
			await board.UpdateAsync(new() { Status = new(BoardCardStatus.Done) }, CancellationToken.None);
			var done = board.SelectedCard.ShouldNotBeNull();
			Click(view, "ArchiveButton");
			Click(view, "CleanArchiveButton");
			window.OwnedWindows.OfType<MessageDialogWindow>().ShouldHaveSingleItem();
			await board.Workspace.ExecuteAsync(new SetArchived(done.Id, true, BoardRevisions.ForCard(done)), new("agent", "Agent"), CancellationToken.None);
			Answer(window, "YesButton");
			board.Cards.Select(card => card.Title).ShouldBe(["Already archived", "New archive entry"]);
			view.FindControl<TextBlock>("ErrorText")!.Text.ShouldNotBeNull().ShouldContain("The archive changed");
		});
	}

	[AvaloniaTest]
	public async Task Delete_from_archive_keeps_other_archived_cards_and_active_order()
	{
		await WithBoard(async (board, view, window) =>
		{
			await board.CreateAsync("First active", CancellationToken.None);
			await board.CreateAsync("Second active", CancellationToken.None);
			foreach (var title in new[] { "Remove archived", "Keep archived" })
			{
				await board.CreateAsync(title, CancellationToken.None);
				await board.UpdateAsync(new() { Status = new(BoardCardStatus.Done) }, CancellationToken.None);
				await board.ArchiveAsync(CancellationToken.None);
			}
			var order = board.Workspace.Snapshot!.OrderRevision;
			Click(view, "ArchiveButton");
			await board.OpenAsync(board.Cards[0].Id, CancellationToken.None);
			Click(view, "DeleteCardButton");
			window.OwnedWindows.ShouldBeEmpty();
			board.Cards.Single().Title.ShouldBe("Keep archived");
			view.FindControl<Border>("Drawer")!.IsVisible.ShouldBeFalse();
			board.Workspace.Snapshot!.OrderRevision.ShouldBe(order);
			Click(view, "ArchiveButton");
			board.Cards.Select(card => card.Title).ShouldBe(["First active", "Second active"]);
		});
	}

	[AvaloniaTest]
	public async Task Keyboard_accessible_order_buttons_change_ready_queue()
	{
		await WithBoard(async (board, view, window) =>
		{
			await board.CreateAsync("First", CancellationToken.None);
			await board.UpdateAsync(new() { Status = new(BoardCardStatus.Ready) }, CancellationToken.None);
			var first = board.SelectedCard!.Id;
			await board.CreateAsync("Second", CancellationToken.None);
			await board.UpdateAsync(new() { Status = new(BoardCardStatus.Ready) }, CancellationToken.None);
			Click(view, "MoveUpButton");
			board.Cards.Where(card => card.Status == BoardCardStatus.Ready).Select(card => card.Title).ShouldBe(["Second", "First"]);
			Click(view, "MoveDownButton");
			board.Cards.First(card => card.Status == BoardCardStatus.Ready).Id.ShouldBe(first);
		});
	}

	private static void Click(ProjectBoardView view, string name) => view.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

	private static IEnumerable<Button> CardButtons(ProjectBoardView view) => view.FindControl<StackPanel>("Columns")!
		.GetLogicalDescendants().OfType<Button>().Where(button => button.Tag is string);

	private static Button ArchiveAllButton(ProjectBoardView view) => view.GetLogicalDescendants().OfType<Button>()
		.Single(button => Equals(button.Content, "Archive all"));

	private static void Answer(Window owner, string buttonName)
	{
		Dispatcher.UIThread.RunJobs();
		var dialog = owner.OwnedWindows.OfType<MessageDialogWindow>().Single();
		dialog.FindControl<Button>(buttonName).ShouldNotBeNull().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Dispatcher.UIThread.RunJobs();
	}

	private static async Task WithBoard(Func<ProjectBoardViewModel, ProjectBoardView, Window, Task> action)
	{
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		using ProjectBoardWorkspace workspace = new(new MemoryStore(), @"D:\project");
		using ProjectBoardViewModel board = new(workspace, action => action(), () => [], TimeProvider.System);
		await board.LoadAsync(CancellationToken.None);
		ProjectBoardView view = new() { DataContext = board };
		Window window = new() { Width = 1000, Height = 800, Content = view };
		window.Show();
		try
		{
			await action(board, view, window);
			await workspace.FlushAsync(CancellationToken.None);
		}
		finally
		{
			view.DataContext = null;
			window.Close();
			Application.Current.Styles.Remove(theme);
		}
	}

	private sealed class MemoryStore : IProjectBoardStore
	{
		private BoardState _state = BoardState.Empty;
		public Task<BoardSnapshot> LoadAsync(string projectRootPath, CancellationToken cancellationToken) =>
			Task.FromResult(new BoardSnapshot(_state, "disk", BoardRevisions.ForOrder(_state)));
		public async Task<BoardMutationResult> ExecuteAsync(string projectRootPath, BoardCommand command, BoardActor actor, CancellationToken cancellationToken)
		{
			var result = BoardRules.Apply(_state, command, actor, DateTimeOffset.UnixEpoch);
			_state = result.State;
			return new(result.Outcome, await LoadAsync(projectRootPath, cancellationToken), result.CardId, result.CommentId);
		}
	}
}
