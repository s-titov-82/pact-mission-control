using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Core.Boards;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

/// <summary>Native project board and an in-surface card editor.</summary>
internal sealed partial class ProjectBoardView : UserControl
{
	private ProjectBoardViewModel? _board;
	private BoardCardEditorViewModel? _editor;
	private bool _refreshing;
	private bool _busy;
	private int _commentLimit = 10;
	private string? _returnCardId;
	private string? _draggedId;
	private string? _dragCandidate;
	private string? _dragOrder;
	private PointerPressedEventArgs? _pressed;
	private Point _dragStart;
	private BoardSnapshot? _columnsSnapshot;
	private bool _columnsArchive;
	private string? _jiraCardId;
	private string? _savedJira;
	public event EventHandler<SessionViewModel>? OpenSessionRequested;
	public event EventHandler<BoardCard>? OpenJiraRequested;
	internal Func<PointerPressedEventArgs, DataTransfer, Task<DragDropEffects>> BeginDragAsync { get; set; } =
		(args, transfer) => DragDrop.DoDragDropAsync(args, transfer, DragDropEffects.Move);

	public ProjectBoardView()
	{
		InitializeComponent();
		DataContextChanged += (_, _) => Attach(DataContext as ProjectBoardViewModel);
		SizeChanged += (_, _) => Drawer.Width = Math.Min(520, Math.Max(0, Bounds.Width - 24));
		KeyDown += (_, e) =>
		{
			if (e.Key == Key.Escape && Drawer.IsVisible)
			{
				OnClose(this, new());
				e.Handled = true;
			}
		};
	}

	private void Attach(ProjectBoardViewModel? board)
	{
		_board?.PropertyChanged -= OnBoardChanged;
		_editor?.PropertyChanged -= OnEditorChanged;
		_board = board;
		_editor = null;
		_columnsSnapshot = null;
		_board?.PropertyChanged += OnBoardChanged;
		Refresh();
	}
	private void OnBoardChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ProjectBoardViewModel.Sessions))
		{
			_columnsSnapshot = null;
		}
		Refresh();
	}
	private void OnEditorChanged(object? sender, PropertyChangedEventArgs e) => RefreshEditor();

	private void Refresh()
	{
		_refreshing = true;
		try
		{
			ErrorText.Text = _board?.Error;
			ErrorText.IsVisible = !string.IsNullOrEmpty(ErrorText.Text);
			ArchiveButton.Content = _board?.ShowArchive == true ? "Active board" : "Archive";
			CleanArchiveButton.IsVisible = _board?.ShowArchive == true;
			CleanArchiveButton.IsEnabled = _board?.ShowArchive == true && _board.Cards.Count > 0;
			if (!ReferenceEquals(_columnsSnapshot, _board?.Workspace.Snapshot) || _columnsArchive != (_board?.ShowArchive == true))
			{
				_columnsSnapshot = _board?.Workspace.Snapshot;
				_columnsArchive = _board?.ShowArchive == true;
				RenderColumns();
			}
			if (!ReferenceEquals(_editor, _board?.Editor))
			{
				_editor?.PropertyChanged -= OnEditorChanged;
				_editor = _board?.Editor;
				_editor?.PropertyChanged += OnEditorChanged;
				TitleBox.DataContext = _editor;
				DescriptionBox.DataContext = _editor;
				_commentLimit = 10;
			}
			Drawer.IsVisible = _editor is not null;
			if (_board?.SelectedCard is not { } card)
			{
				return;
			}
			CardHeading.Text = $"#{card.Number}";
			TitleBox.IsReadOnly = DescriptionBox.IsReadOnly = card.IsArchived;
			StatusBox.ItemsSource = Enum.GetValues<BoardCardStatus>().Select(status => new StatusChoice(status)).ToArray();
			StatusBox.SelectedIndex = (int)card.Status;
			StatusBox.IsEnabled = SessionBox.IsEnabled = SaveJiraButton.IsEnabled = !card.IsArchived;
			var sessions = new List<SessionChoice> { new(null, "Unassigned") };
			sessions.AddRange(_board.Sessions.Select(session => new SessionChoice(session.Record.Id, session.Title)));
			if (card.AssignedSessionId is { } missing && !sessions.Any(session => session.Id == missing))
			{
				sessions.Add(new(missing, (_board.AssigneeName(card) ?? missing) + " (unavailable)"));
			}
			SessionBox.ItemsSource = sessions;
			SessionBox.SelectedItem = sessions.FirstOrDefault(session => session.Id == card.AssignedSessionId);
			MissingSessionText.IsVisible = card.AssignedSessionId is not null && !_board.CanNavigateToAssignee(card);
			OpenSessionButton.IsEnabled = _board.CanNavigateToAssignee(card);
			if (_jiraCardId != card.Id || JiraBox.Text == _savedJira)
			{
				JiraBox.Text = card.JiraUrl;
			}
			_jiraCardId = card.Id;
			_savedJira = card.JiraUrl;
			JiraBox.IsReadOnly = card.IsArchived;
			OpenJiraButton.IsEnabled = card.JiraUrl is not null;
			ArchiveCardButton.IsEnabled = card.Status == BoardCardStatus.Done;
			ArchiveCardButton.Content = card.IsArchived ? "Restore card" : "Archive card";
			var column = _board.Cards.Where(item => item.Status == card.Status).ToArray();
			var index = Array.FindIndex(column, item => item.Id == card.Id);
			MoveUpButton.IsEnabled = !card.IsArchived && index > 0;
			MoveDownButton.IsEnabled = !card.IsArchived && index >= 0 && index < column.Length - 1;
			CommentsPanel.Children.Clear();
			foreach (var comment in card.Comments.TakeLast(_commentLimit))
			{
				StackPanel item = new() { Spacing = 4 };
				var author = _board.Sessions.FirstOrDefault(session => session.Record.Id == comment.AuthorSessionId)?.Title ?? comment.AuthorName;
				item.Children.Add(new TextBlock { Text = $"{author} · {comment.CreatedAt.LocalDateTime:g}", FontSize = 12 });
				item.Children.Add(new SelectableTextBlock { Text = comment.Text, TextWrapping = TextWrapping.Wrap });
				Button delete = new() { Content = "Delete comment", Tag = comment.Id, IsEnabled = !card.IsArchived };
				delete.Click += (_, _) => Run(async board => await board.DeleteCommentAsync(comment.Id, CancellationToken.None));
				item.Children.Add(delete);
				CommentsPanel.Children.Add(item);
			}
			MoreCommentsButton.IsVisible = card.Comments.Count > _commentLimit;
			CommentBox.Text = _board.CommentText;
			CommentBox.IsEnabled = AddCommentButton.IsEnabled = !card.IsArchived;
			RefreshEditor();
		}
		finally
		{
			_refreshing = false;
		}
	}

	private void RefreshEditor()
	{
		var status = _editor?.SaveStatus;
		SaveText.Text = status?.ErrorMessage ?? status?.State.ToString();
		RetryButton.IsVisible = status?.State == DocumentSaveState.Failed;
		ReloadButton.IsVisible = SaveMineButton.IsVisible = status?.State == DocumentSaveState.Conflict;
		DescriptionPreview.Markdown = _editor?.Description ?? string.Empty;
	}

	private void RenderColumns()
	{
		Columns.Children.Clear();
		if (_board is null)
		{
			return;
		}
		foreach (var status in _board.ShowArchive ? [BoardCardStatus.Done] : Enum.GetValues<BoardCardStatus>())
		{
			var cards = _board.Cards.Where(card => card.Status == status).ToArray();
			StackPanel column = new() { Width = 240, Spacing = 8, MinHeight = 180, Tag = status };
			Grid header = new() { ColumnDefinitions = new("*,Auto"), MinHeight = 36, Margin = new(4, 0) };
			header.Children.Add(new TextBlock
			{
				Text = $"{(_board.ShowArchive ? "Archive" : StatusLabel(status))}  {cards.Length}",
				FontWeight = FontWeight.SemiBold,
				VerticalAlignment = VerticalAlignment.Center
			});
			if (!_board.ShowArchive && status == BoardCardStatus.Done)
			{
				Button archiveAll = new() { Content = "Archive all", IsEnabled = cards.Length > 0, VerticalAlignment = VerticalAlignment.Center };
				archiveAll.Click += OnArchiveAllDone;
				Grid.SetColumn(archiveAll, 1);
				header.Children.Add(archiveAll);
			}
			column.Children.Add(header);
			if (cards.Length == 0)
			{
				column.Children.Add(new TextBlock { Text = status == BoardCardStatus.Ready ? "No Ready cards" : "No cards", Margin = new(4, 8) });
			}
			foreach (var card in cards)
			{
				StackPanel text = new() { Spacing = 6 };
				text.Children.Add(new TextBlock { Text = $"#{card.Number}" + (status == BoardCardStatus.Ready && card == cards[0] ? " · Next" : ""), FontSize = 12 });
				text.Children.Add(new TextBlock { Text = card.Title, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
				text.Children.Add(new TextBlock { Text = $"{_board.AssigneeName(card) ?? "Unassigned"} · {card.Comments.Count} comments", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
				Button button = new() { Content = text, Tag = card.Id, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(12) };
				button.Click += (_, _) => Run(async board =>
				{
					_returnCardId = card.Id;
					if (await board.OpenAsync(card.Id, CancellationToken.None))
					{
						TitleBox.Focus();
					}
				});
				button.AddHandler(PointerPressedEvent, (_, e) =>
				{
					if (e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
					{
						_pressed = e;
						_dragCandidate = card.Id;
						_dragStart = e.GetPosition(this);
						_dragOrder = _board?.Workspace.Snapshot?.OrderRevision;
					}
				}, RoutingStrategies.Tunnel, handledEventsToo: true);
				button.AddHandler(PointerMovedEvent, OnDragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
				button.AddHandler(PointerReleasedEvent, (_, _) => { _pressed = null; _dragCandidate = null; }, RoutingStrategies.Tunnel, handledEventsToo: true);
				AttachDrop(button, status, card.Id);
				column.Children.Add(button);
			}
			ScrollViewer viewport = new()
			{
				Content = column,
				Background = Brushes.Transparent,
				VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
			};
			if (!_board.ShowArchive)
			{
				// Leave a reachable drop area even when the last card fills the viewport.
				column.Margin = new Thickness(0, 0, 0, 24);
				AttachDrop(viewport, status, null);
			}
			Columns.Children.Add(viewport);
		}
	}

	private void AttachDrop(Control target, BoardCardStatus status, string? before)
	{
		DragDrop.SetAllowDrop(target, true);
		target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
		{
			e.DragEffects = _draggedId is not null && _board?.ShowArchive == false && _draggedId != before ? DragDropEffects.Move : DragDropEffects.None;
			e.Handled = true;
		});
		target.AddHandler(DragDrop.DropEvent, (_, e) =>
		{
			if (_draggedId is { } id && _board?.ShowArchive == false && id != before)
			{
				var order = _dragOrder;
				Run(async board => await board.MoveAsync(id, status, before, CancellationToken.None, order));
			}
			e.Handled = true;
		});
	}
	private async void OnDragMoved(object? sender, PointerEventArgs e)
	{
		if (_pressed is not { } pressed || _board?.ShowArchive != false || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
			|| (Math.Abs(e.GetPosition(this).X - _dragStart.X) < 8 && Math.Abs(e.GetPosition(this).Y - _dragStart.Y) < 8))
		{
			return;
		}
		_pressed = null;
		_draggedId = _dragCandidate;
		_dragCandidate = null;
		using DataTransfer transfer = new();
		DataTransferItem item = new();
		item.SetText("Pact board card");
		transfer.Add(item);
		try
		{
			await BeginDragAsync(pressed, transfer);
		}
		finally
		{
			_draggedId = null;
		}
	}

	private async void Run(Func<ProjectBoardViewModel, Task> operation)
	{
		if (_board is not { } board || _busy)
		{
			return;
		}
		_busy = true;
		try
		{
			await operation(board);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
		{
			ErrorText.Text = exception.Message;
			ErrorText.IsVisible = true;
		}
		finally
		{
			_busy = false;
		}
	}
	private void OnRefresh(object? sender, RoutedEventArgs e) => Run(async board => await board.LoadAsync(CancellationToken.None));
	private void OnCreate(object? sender, RoutedEventArgs e) => Run(async board =>
	{
		if (await board.CreateAsync("New card", CancellationToken.None))
		{
			TitleBox.Focus();
			TitleBox.SelectAll();
		}
	});
	private void OnArchiveView(object? sender, RoutedEventArgs e) => Run(async board => await board.SetArchiveAsync(!board.ShowArchive, CancellationToken.None));
	private void OnClose(object? sender, RoutedEventArgs e) => Run(async board =>
	{
		if (await board.OpenAsync(null, CancellationToken.None))
		{
			var target = Columns.Children.OfType<ScrollViewer>().Select(view => view.Content).OfType<StackPanel>()
				.SelectMany(column => column.Children).OfType<Button>().FirstOrDefault(button => (string?)button.Tag == _returnCardId);
			(target ?? CreateButton).Focus();
		}
	});
	private void OnStatusChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (!_refreshing && StatusBox.SelectedItem is StatusChoice choice)
		{
			Run(async board => await board.UpdateAsync(new() { Status = new(choice.Status) }, CancellationToken.None));
		}
	}
	private void OnSessionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (!_refreshing && SessionBox.SelectedItem is SessionChoice choice)
		{
			Run(async board => await board.UpdateAsync(new() { AssignedSessionId = new(choice.Id), AssignedSessionName = new(choice.Id is null ? null : choice.Label) }, CancellationToken.None));
		}
	}
	private void OnSaveJira(object? sender, RoutedEventArgs e) => Run(async board => await board.UpdateAsync(new() { JiraUrl = new(JiraBox.Text) }, CancellationToken.None));
	private void OnOpenJira(object? sender, RoutedEventArgs e)
	{
		if (_board?.SelectedCard is { JiraUrl: not null } card)
		{
			OpenJiraRequested?.Invoke(this, card);
		}
	}
	private void OnOpenSession(object? sender, RoutedEventArgs e)
	{
		if (_board?.Sessions.FirstOrDefault(session => session.Record.Id == _board.SelectedCard?.AssignedSessionId) is { } session)
		{
			OpenSessionRequested?.Invoke(this, session);
		}
	}
	private void OnArchiveCard(object? sender, RoutedEventArgs e) => Run(async board => await board.ArchiveAsync(CancellationToken.None));
	private void OnDeleteCard(object? sender, RoutedEventArgs e) => Run(async board =>
	{
		if (board.Editor is not { } editor)
		{
			return;
		}

		Drawer.IsEnabled = false;
		try
		{
			if (await board.PrepareDeleteAsync(CancellationToken.None) is not { } deletion
				|| !ReferenceEquals(_board, board) || !ReferenceEquals(board.Editor, editor))
			{
				return;
			}

			if (deletion.RequiresConfirmation
				&& !await ConfirmAsync("Delete card", $"Permanently delete card #{deletion.Card.Number} and its comments?"))
			{
				return;
			}

			if (ReferenceEquals(_board, board) && ReferenceEquals(board.Editor, editor)
				&& await board.DeleteAsync(deletion.Card, CancellationToken.None))
			{
				(board.ShowArchive ? ArchiveButton : CreateButton).Focus();
			}
		}
		finally
		{
			Drawer.IsEnabled = true;
		}
	});

	private void OnArchiveAllDone(object? sender, RoutedEventArgs e) => Run(async board =>
	{
		if (board.ShowArchive || _columnsSnapshot is not { } snapshot
			|| !snapshot.State.Cards.Any(card => card is { Status: BoardCardStatus.Done, IsArchived: false })
			|| !await ConfirmAsync("Archive all", "Move all cards in the Done column to the archive?")
			|| !ReferenceEquals(_board, board) || board.ShowArchive)
		{
			return;
		}

		Drawer.IsEnabled = false;
		try
		{
			if (await board.ArchiveAllDoneAsync(snapshot.OrderRevision, CancellationToken.None)
				&& ReferenceEquals(_board, board))
			{
				ArchiveButton.Focus();
			}
		}
		finally
		{
			Drawer.IsEnabled = true;
		}
	});

	private void OnCleanArchive(object? sender, RoutedEventArgs e) => Run(async board =>
	{
		if (!board.ShowArchive || board.Workspace.Snapshot is not { } snapshot || board.Cards.Count == 0)
		{
			return;
		}

		var revision = BoardRevisions.ForArchive(snapshot.State);
		if (await ConfirmAsync("Clean archive", "Permanently delete all archived cards and their comments?")
			&& ReferenceEquals(_board, board) && board.ShowArchive
			&& await board.CleanArchiveAsync(revision, CancellationToken.None))
		{
			ArchiveButton.Focus();
		}
	});

	private async Task<bool> ConfirmAsync(string title, string message)
	{
		return TopLevel.GetTopLevel(this) is Window owner
			&& await MessageDialogWindow.ShowOwnedAsync(owner,
				new(title, message, MessageDialogButtons.YesNo, MessageDialogResult.No)) == MessageDialogResult.Yes;
	}

	private void OnMoveUp(object? sender, RoutedEventArgs e) => MoveSelected(-1);
	private void OnMoveDown(object? sender, RoutedEventArgs e) => MoveSelected(1);
	private void MoveSelected(int direction) => Run(async board =>
	{
		if (board.SelectedCard is not { } card)
		{
			return;
		}
		var column = board.Cards.Where(candidate => candidate.Status == card.Status).ToArray();
		var index = Array.FindIndex(column, candidate => candidate.Id == card.Id);
		var target = direction < 0 ? index - 1 : index + 2;
		await board.MoveAsync(card.Id, card.Status, target >= 0 && target < column.Length ? column[target].Id : null, CancellationToken.None);
	});
	private void OnCommentTextChanged(object? sender, TextChangedEventArgs e)
	{
		if (!_refreshing && _board is { } board)
		{
			board.CommentText = CommentBox.Text ?? string.Empty;
		}
	}
	private void OnAddComment(object? sender, RoutedEventArgs e) => Run(async board =>
	{
		board.CommentText = CommentBox.Text ?? string.Empty;
		await board.AddCommentAsync(CancellationToken.None);
	});
	private void OnMoreComments(object? sender, RoutedEventArgs e) { _commentLimit += 10; Refresh(); }
	private void OnRetry(object? sender, RoutedEventArgs e) => Run(async board => { if (board.Editor is { } editor) { await editor.FlushAsync(CancellationToken.None); } });
	private void OnSaveMine(object? sender, RoutedEventArgs e) => Run(async board => { if (board.Editor is { } editor) { await editor.SaveMineAsync(CancellationToken.None); } });
	private void OnReload(object? sender, RoutedEventArgs e) => _editor?.Reload();
	private static string StatusLabel(BoardCardStatus status) => status == BoardCardStatus.InProgress ? "In Progress" : status.ToString();
	private sealed record StatusChoice(BoardCardStatus Status) { public override string ToString() => StatusLabel(Status); }
	private sealed record SessionChoice(string? Id, string Label) { public override string ToString() => Label; }
}
