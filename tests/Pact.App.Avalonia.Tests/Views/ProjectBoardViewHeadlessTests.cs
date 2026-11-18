using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Views;
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
			var card = view.FindControl<StackPanel>("Columns")!.GetLogicalDescendants().OfType<Button>().Single();
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
