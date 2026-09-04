using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Views;
using Pact.Core.Projects;
using Pact.Core.Prompting;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class NotesAndActionsHeadlessTests : IDisposable
{
	private readonly List<TemporaryDirectory> _temporaryDirectories = [];

	[AvaloniaTest]
	public async Task Save_failure_is_projected_through_the_notes_owner_reporter()
	{
		ProjectNoteDocument document = new(
			new ThrowingNotesStore(),
			"C:\\repo",
			TimeSpan.FromHours(1));
		await document.LoadAsync(CancellationToken.None);
		document.SetText("changed notes");
		TaskCompletionSource<Exception> projected = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		ObservedTaskGroup eventTasks = new(static (_, _) => Task.CompletedTask);
		NotesPaneView notes = new() { Document = document };
		notes.ConfigureLifecycle(
			eventTasks,
			exception =>
			{
				projected.TrySetResult(exception);
				return Task.CompletedTask;
			});

		notes.GetLogicalDescendants()
			.OfType<Button>()
			.Single(button => Equals(button.Content, "Save mine"))
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await eventTasks.CompleteAndDrainAsync();

		(await projected.Task).Message.ShouldBe("notes save failed");
	}

	[AvaloniaTest]
	public async Task Reopening_unchanged_notes_preserves_caret_position()
	{
		var document = await CreateDocumentAsync("first\nsecond\nthird");
		NotesPaneView notes = new();
		var editor = notes.FindControl<TextBox>("Editor").ShouldBeOfType<TextBox>();
		notes.Document = document;
		notes.FocusEditor();
		editor.CaretIndex = 3;

		notes.Document = null;
		notes.Document = document;
		notes.FocusEditor();

		editor.CaretIndex.ShouldBe(3);
	}

	[AvaloniaTest]
	public async Task ShiftDelete_CutsSelectedEditorText()
	{
		var document = await CreateDocumentAsync("abcdef");
		NotesPaneView notes = new() { Document = document };
		Window window = new() { Content = notes };
		window.Show();
		try
		{
			var editor = notes.FindControl<TextBox>("Editor").ShouldBeOfType<TextBox>();
			editor.SelectionStart = 1;
			editor.SelectionEnd = 4;
			KeyEventArgs key = new()
			{
				RoutedEvent = InputElement.KeyDownEvent,
				Key = Key.Delete,
				KeyModifiers = KeyModifiers.Shift
			};

			editor.RaiseEvent(key);
			Dispatcher.UIThread.RunJobs();

			key.Handled.ShouldBeTrue();
			editor.Text.ShouldBe("aef");
			var clipboard = TopLevel.GetTopLevel(notes).ShouldNotBeNull().Clipboard.ShouldNotBeNull();
			(await clipboard.TryGetTextAsync()).ShouldBe("bcd");
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Delete_without_shift_stays_an_ordinary_editor_delete()
	{
		var document = await CreateDocumentAsync("abcdef");
		NotesPaneView notes = new() { Document = document };
		Window window = new() { Content = notes };
		window.Show();
		try
		{
			var editor = notes.FindControl<TextBox>("Editor").ShouldBeOfType<TextBox>();
			var clipboard = TopLevel.GetTopLevel(notes).ShouldNotBeNull().Clipboard.ShouldNotBeNull();
			await clipboard.SetTextAsync("untouched");
			editor.SelectionStart = 1;
			editor.SelectionEnd = 4;
			KeyEventArgs key = new()
			{
				RoutedEvent = InputElement.KeyDownEvent,
				Key = Key.Delete,
				KeyModifiers = KeyModifiers.None
			};

			editor.RaiseEvent(key);
			Dispatcher.UIThread.RunJobs();

			(await clipboard.TryGetTextAsync()).ShouldBe("untouched");
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Pointer_release_publishes_selected_text_at_the_editor_point()
	{
		var document = await CreateDocumentAsync("before selected after");
		NotesPaneView notes = new() { Document = document };
		Window window = new() { Width = 520, Height = 320, Content = notes };
		NotesSelectionCompletion? received = null;
		notes.SelectionCompleted += (_, completion) => received = completion;
		window.Show();
		window.UpdateLayout();
		var editor = notes.FindControl<TextBox>("Editor").ShouldBeOfType<TextBox>();
		Point editorPoint = new(40, 30);
		var windowPoint = editor.TranslatePoint(editorPoint, window).ShouldNotBeNull();
		editor.SelectionStart = 7;
		editor.SelectionEnd = 15;
		using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
		PointerReleasedEventArgs released = new(
			editor,
			pointer,
			window,
			windowPoint,
			timestamp: 1,
			new PointerPointProperties(
				RawInputModifiers.None,
				PointerUpdateKind.LeftButtonReleased),
			KeyModifiers.None,
			MouseButton.Left);

		try
		{
			editor.RaiseEvent(released);

			received.ShouldNotBeNull();
			received.Text.ShouldBe("selected");
			received.HasAnchor.ShouldBeTrue();
			received.X.ShouldBe(editorPoint.X, tolerance: 0.01);
			received.Y.ShouldBe(editorPoint.Y, tolerance: 0.01);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Keyboard_selection_completion_has_no_visual_anchor()
	{
		var document = await CreateDocumentAsync("before selected after");
		NotesPaneView notes = new() { Document = document };
		var editor = notes.FindControl<TextBox>("Editor").ShouldBeOfType<TextBox>();
		NotesSelectionCompletion? received = null;
		notes.SelectionCompleted += (_, completion) => received = completion;
		editor.SelectionStart = 7;
		editor.SelectionEnd = 15;

		editor.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyUpEvent,
			Key = Key.Right,
			KeyModifiers = KeyModifiers.Shift
		});

		received.ShouldNotBeNull();
		received.Text.ShouldBe("selected");
		received.HasAnchor.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task Left_pointer_completion_upgrades_keyboard_anchor_for_same_selection()
	{
		var document = await CreateDocumentAsync("before selected after");
		NotesPaneView notes = new() { Document = document };
		Window window = new() { Width = 520, Height = 320, Content = notes };
		List<NotesSelectionCompletion> received = [];
		notes.SelectionCompleted += (_, completion) => received.Add(completion);
		window.Show();
		window.UpdateLayout();
		var editor = notes.FindControl<TextBox>("Editor").ShouldBeOfType<TextBox>();
		Point editorPoint = new(40, 30);
		var windowPoint = editor.TranslatePoint(editorPoint, window).ShouldNotBeNull();
		editor.SelectionStart = 7;
		editor.SelectionEnd = 15;
		editor.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyUpEvent,
			Key = Key.Right,
			KeyModifiers = KeyModifiers.Shift
		});
		using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

		try
		{
			editor.RaiseEvent(new PointerReleasedEventArgs(
				editor,
				pointer,
				window,
				windowPoint,
				timestamp: 1,
				new PointerPointProperties(
					RawInputModifiers.None,
					PointerUpdateKind.LeftButtonReleased),
				KeyModifiers.None,
				MouseButton.Left));

			received.Count.ShouldBe(2);
			received[0].HasAnchor.ShouldBeFalse();
			received[1].HasAnchor.ShouldBeTrue();
			received[1].X.ShouldBe(editorPoint.X, tolerance: 0.01);
			received[1].Y.ShouldBe(editorPoint.Y, tolerance: 0.01);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Unchanged_selection_ignores_copy_modifiers_and_right_click()
	{
		var document = await CreateDocumentAsync("before selected after");
		NotesPaneView notes = new() { Document = document };
		Window window = new() { Width = 520, Height = 320, Content = notes };
		List<NotesSelectionCompletion> received = [];
		notes.SelectionCompleted += (_, completion) => received.Add(completion);
		window.Show();
		window.UpdateLayout();
		var editor = notes.FindControl<TextBox>("Editor").ShouldBeOfType<TextBox>();
		editor.SelectionStart = 7;
		editor.SelectionEnd = 15;
		editor.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyUpEvent,
			Key = Key.Right,
			KeyModifiers = KeyModifiers.Shift
		});

		editor.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyUpEvent,
			Key = Key.C,
			KeyModifiers = KeyModifiers.Control
		});
		editor.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyUpEvent,
			Key = Key.LeftCtrl
		});
		using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
		editor.RaiseEvent(new PointerReleasedEventArgs(
			editor,
			pointer,
			window,
			new Point(40, 30),
			timestamp: 1,
			new PointerPointProperties(
				RawInputModifiers.None,
				PointerUpdateKind.RightButtonReleased),
			KeyModifiers.None,
			MouseButton.Right));

		try
		{
			received.ShouldHaveSingleItem();
			received[0].Text.ShouldBe("selected");
			received[0].HasAnchor.ShouldBeFalse();
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Collapsing_non_empty_keyboard_selection_publishes_empty_completion_for_close()
	{
		var document = await CreateDocumentAsync("text");
		NotesPaneView notes = new() { Document = document };
		var editor = notes.FindControl<TextBox>("Editor").ShouldBeOfType<TextBox>();
		NotesSelectionCompletion? received = null;
		notes.SelectionCompleted += (_, completion) => received = completion;
		editor.SelectionStart = 0;
		editor.SelectionEnd = 2;
		editor.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyUpEvent,
			Key = Key.Right,
			KeyModifiers = KeyModifiers.Shift
		});
		received.ShouldNotBeNull().Text.ShouldBe("te");

		received = null;
		editor.SelectionStart = 2;
		editor.SelectionEnd = 2;

		editor.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyUpEvent,
			Key = Key.Right
		});

		received.ShouldNotBeNull();
		received.Text.ShouldBeEmpty();
		received.HasAnchor.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task Reopening_notes_after_append_moves_caret_to_end()
	{
		var document = await CreateDocumentAsync("first");
		NotesPaneView notes = new();
		var editor = notes.FindControl<TextBox>("Editor").ShouldBeOfType<TextBox>();
		notes.Document = document;
		notes.FocusEditor();
		editor.CaretIndex = 2;
		notes.Document = null;
		document.Append("second");

		notes.Document = document;
		notes.FocusEditor();

		editor.CaretIndex.ShouldBe(editor.Text!.Length);
	}

	[AvaloniaTest]
	public async Task Switching_equal_text_notes_restores_each_document_selection()
	{
		var first = await CreateDocumentAsync("same text");
		var second = await CreateDocumentAsync("same text");
		NotesPaneView notes = new();
		var editor = notes.FindControl<TextBox>("Editor").ShouldBeOfType<TextBox>();
		notes.Document = first;
		editor.CaretIndex = 2;
		editor.SelectionStart = 1;
		editor.SelectionEnd = 4;

		notes.Document = second;
		editor.CaretIndex = 7;
		editor.SelectionStart = 5;
		editor.SelectionEnd = 8;

		notes.Document = first;

		editor.CaretIndex.ShouldBe(2);
		editor.SelectionStart.ShouldBe(1);
		editor.SelectionEnd.ShouldBe(4);
	}

	[AvaloniaTest]
	public async Task Same_restored_selection_publishes_after_document_generation_changes()
	{
		var first = await CreateDocumentAsync("same text");
		var second = await CreateDocumentAsync("same text");
		NotesPaneView notes = new();
		var editor = notes.FindControl<TextBox>("Editor").ShouldBeOfType<TextBox>();
		List<NotesSelectionCompletion> received = [];
		notes.SelectionCompleted += (_, completion) => received.Add(completion);
		notes.Document = second;
		editor.SelectionStart = 1;
		editor.SelectionEnd = 4;
		notes.Document = first;
		editor.SelectionStart = 1;
		editor.SelectionEnd = 4;
		editor.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyUpEvent,
			Key = Key.Right,
			KeyModifiers = KeyModifiers.Shift
		});

		notes.Document = second;
		editor.SelectionStart.ShouldBe(1);
		editor.SelectionEnd.ShouldBe(4);
		editor.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyUpEvent,
			Key = Key.Right,
			KeyModifiers = KeyModifiers.Shift
		});

		received.Count.ShouldBe(2);
		received.All(completion => completion.Text == "ame").ShouldBeTrue();
	}

	[AvaloniaTest]
	public void Empty_document_pane_hides_surfaces_and_disables_mode_controls()
	{
		NotesPaneView notes = new();

		notes.FindControl<TextBox>("Editor")!.IsVisible.ShouldBeFalse();
		notes.FindControl<Control>("Preview")!.IsVisible.ShouldBeFalse();
		notes.FindControl<ToggleButton>("PreviewModeButton")!.IsEnabled.ShouldBeFalse();
		notes.FindControl<ToggleButton>("EditorModeButton")!.IsEnabled.ShouldBeFalse();
	}

	[AvaloniaTest]
	public void Rendered_markdown_preview_enables_text_selection()
	{
		NotesPaneView notes = new();

		notes.FindControl<Markdown.Avalonia.Full.MarkdownScrollViewer>("Preview")!
			.SelectionEnabled.ShouldBeTrue();
	}

	[AvaloniaTest]
	public async Task Primary_tab_selection_focuses_only_editor_mode()
	{
		var root = CreateRoot();
		try
		{
			Write(root, "README.md", "# Readme");
			var workspace = CreateWorkspace(root);
			await workspace.RefreshAsync(CancellationToken.None);
			NotesPaneView notes = new() { Workspace = workspace };
			Window window = new() { Content = notes };
			window.Show();
			var editor = notes.FindControl<TextBox>("Editor")!;
			var notesTab = notes.FindControl<ToggleButton>("NotesTab")!;
			var commonTab = notes.FindControl<ToggleButton>("CommonTab")!;

			await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
			notesTab.Focus();
			Click(notesTab);

			editor.IsFocused.ShouldBeTrue();

			commonTab.Focus();
			Click(commonTab);

			editor.IsVisible.ShouldBeFalse();
			editor.IsFocused.ShouldBeFalse();

			Click(notes.FindControl<ToggleButton>("EditorModeButton")!);
			notesTab.Focus();
			Click(notesTab);
			commonTab.Focus();
			Click(commonTab);

			editor.IsVisible.ShouldBeTrue();
			editor.IsFocused.ShouldBeTrue();

			window.Close();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[AvaloniaTest]
	public void NotesAndActionsExposeOnlyInShellControls()
	{
		NotesPaneView notes = new();
		RightActionsPanel actions = new();

		notes.FindControl<TextBox>("Editor").ShouldNotBeNull();
		actions.ShouldNotBeNull();
	}

	[AvaloniaTest]
	public async Task DocsAndNotesPaneShowsThreePrimaryTabsAndNoNestedStrip()
	{
		var root = CreateRoot();
		try
		{
			Write(root, "AGENTS.md", "# Agents");
			Write(root, "docs/superpowers/specs/design.md", "# Design");
			var workspace = CreateWorkspace(root);
			await workspace.RefreshAsync(CancellationToken.None);
			NotesPaneView notes = new() { Workspace = workspace };

			(notes.FindControl<ToggleButton>("NotesTab")?.Content).ShouldBe("Notes");
			(notes.FindControl<ToggleButton>("CommonTab")?.Content).ShouldBe("Common MD's");
			(notes.FindControl<ToggleButton>("DocsTab")?.Content).ShouldBe("Docs");
			notes.FindControl<ToggleButton>("ReadmeTab").ShouldBeNull();
			notes.FindControl<ToggleButton>("SuperpowersTab").ShouldBeNull();
			notes.FindControl<ItemsControl>("NestedTabs").ShouldBeNull();
			notes.FindControl<ToggleButton>("PreviewModeButton")!.Content.ShouldNotBeNull();
			notes.FindControl<ToggleButton>("EditorModeButton")!.Content.ShouldNotBeNull();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[AvaloniaTest]
	public async Task Notes_defaults_to_editor_and_project_documents_default_to_preview()
	{
		var root = CreateRoot();
		try
		{
			Write(root, "README.md", "# Readme");
			var workspace = CreateWorkspace(root);
			await workspace.RefreshAsync(CancellationToken.None);
			NotesPaneView notes = new() { Workspace = workspace };

			notes.FindControl<TextBox>("Editor")!.IsVisible.ShouldBeTrue();
			notes.FindControl<Control>("Preview")!.IsVisible.ShouldBeFalse();
			(notes.FindControl<ToggleButton>("EditorModeButton")!.IsChecked == true).ShouldBeTrue();

			await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);

			notes.FindControl<TextBox>("Editor")!.IsVisible.ShouldBeFalse();
			notes.FindControl<Control>("Preview")!.IsVisible.ShouldBeTrue();
			(notes.FindControl<ToggleButton>("PreviewModeButton")!.IsChecked == true).ShouldBeTrue();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[AvaloniaTest]
	public async Task Mode_selection_is_remembered_independently_per_document()
	{
		var root = CreateRoot();
		try
		{
			Write(root, "README.md", "# Readme");
			Write(root, "AGENTS.md", "# Agents");
			var workspace = CreateWorkspace(root);
			await workspace.RefreshAsync(CancellationToken.None);
			NotesPaneView notes = new() { Workspace = workspace };

			await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
			Click(notes.FindControl<ToggleButton>("EditorModeButton")!);
			var agents = workspace.CommonTree.Single(node => node.Title == "AGENTS.md");
			await workspace.SelectDocumentAsync(agents, CancellationToken.None);

			(notes.FindControl<ToggleButton>("PreviewModeButton")!.IsChecked == true).ShouldBeTrue();

			var readme = workspace.CommonTree.Single(node => node.Title == "README.md");
			await workspace.SelectDocumentAsync(readme, CancellationToken.None);

			(notes.FindControl<ToggleButton>("EditorModeButton")!.IsChecked == true).ShouldBeTrue();
			notes.FindControl<TextBox>("Editor")!.IsVisible.ShouldBeTrue();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[AvaloniaTest]
	public async Task Failed_autosave_shows_retry_and_retry_keeps_local_text()
	{
		FailOnceNotesStore store = new();
		ProjectNoteDocument document = new(store, "C:\\repo", TimeSpan.Zero);
		await document.LoadAsync(CancellationToken.None);
		TaskCompletionSource<Exception> reported =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		ObservedTaskGroup eventTasks = new(static (_, _) => Task.CompletedTask);
		NotesPaneView notes = new();
		notes.ConfigureLifecycle(
			eventTasks,
			reportSaveFailureAsync: exception =>
			{
				reported.TrySetResult(exception);
				return Task.CompletedTask;
			});
		notes.Document = document;

		document.SetText("precious");
		await reported.Task.WaitAsync(TimeSpan.FromSeconds(1));

		notes.FindControl<Control>("SaveFailureBanner")!.IsVisible.ShouldBeTrue();
		notes.FindControl<TextBlock>("SaveFailureMessage")!
			.Text.ShouldNotBeNull().ShouldContain("save failed");
		notes.FindControl<Button>("RetrySaveButton")!
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await eventTasks.CompleteAndDrainAsync();

		document.SaveStatus.State.ShouldBe(DocumentSaveState.Clean);
		store.Saved.ShouldBe("precious");
		notes.FindControl<Control>("SaveFailureBanner")!.IsVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task ExternalWriteShowsConflictBannerWithoutOverwritingDisk()
	{
		var root = CreateRoot();
		try
		{
			var readme = Write(root, "README.md", "initial");
			var workspace = CreateWorkspace(root);
			await workspace.RefreshAsync(CancellationToken.None);
			await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
			NotesPaneView notes = new() { Workspace = workspace };
			var editor = notes.FindControl<TextBox>("Editor")!;
			editor.Text = "mine";
			await File.WriteAllTextAsync(readme, "external");

			await workspace.ActiveDocument!.FlushAsync(CancellationToken.None);

			notes.FindControl<Control>("ConflictBanner")!.IsVisible.ShouldBeTrue();
			(await File.ReadAllTextAsync(readme)).ShouldBe("external");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[AvaloniaTest]
	public void DefaultActionsPanelRemainsVisibleWithoutSelectionMode()
	{
		RightActionsPanel actions = new();
		var defaults = actions.FindControl<Control>("DefaultActionsPanel")!;

		defaults.IsVisible.ShouldBeTrue();
	}

	[AvaloniaTest]
	public void SelectedTabDetailsAndTransientStatusHaveIndependentVisibility()
	{
		RightActionsPanel actions = new();
		SelectedTabDetailsViewModel details = new(
			"Selected terminal",
			"Author",
			[new SelectedTabDetailRowViewModel("Viewport", "215 × 37")]);
		var detailsSection = actions.FindControl<Control>("SelectedTabDetailsSection")!;
		var status = actions.FindControl<TextBlock>("StatusText")!;

		actions.SetSelectedTabDetails(details, visible: true);
		detailsSection.IsVisible.ShouldBeTrue();
		detailsSection.DataContext.ShouldBeSameAs(details);
		status.IsVisible.ShouldBeFalse();

		actions.SetStatusText("Clipboard failed");
		status.Text.ShouldBe("Clipboard failed");
		status.IsVisible.ShouldBeTrue();
		detailsSection.IsVisible.ShouldBeTrue();

		actions.SetSelectedTabDetails(details, visible: false);
		detailsSection.IsVisible.ShouldBeFalse();
		status.IsVisible.ShouldBeTrue();
	}

	[AvaloniaTest]
	public void SelectedTabDetailsUseAvailableHeightBeforeScrolling()
	{
		RightActionsPanel actions = new();
		SelectedTabDetailsViewModel details = new(
			"Selected terminal",
			"Author",
			Enumerable.Range(1, 24)
				.Select(index => new SelectedTabDetailRowViewModel($"Fact {index}", "Value"))
				.ToArray());
		actions.SetSelectedTabDetails(details, visible: true);
		Window window = new() { Width = 380, Height = 1000, Content = actions };

		try
		{
			window.Show();
			window.UpdateLayout();
			Dispatcher.UIThread.RunJobs();
			var scroller = actions.FindControl<ScrollViewer>("SelectedTabDetailsScroller")
				.ShouldNotBeNull();

			scroller.Extent.Height.ShouldBeLessThanOrEqualTo(scroller.Viewport.Height);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public void SelectedTabDetailsRefreshPreservesSelectedTextForCopying()
	{
		RightActionsPanel actions = new();
		SelectedTabDetailsViewModel details = new(
			"Selected terminal",
			"Author",
			[
				new SelectedTabDetailRowViewModel("External metrics", "Unavailable — method missing"),
				new SelectedTabDetailRowViewModel("Observed", "10:00:00")
			]);
		actions.SetSelectedTabDetails(details, visible: true);
		var rows = actions.FindControl<ItemsControl>("SelectedTabDetailsRows").ShouldNotBeNull();
		var template = rows.ItemTemplate.ShouldBeAssignableTo<IDataTemplate>()!;
		var row = template.Build(details.Rows[0]).ShouldBeAssignableTo<Control>()!;
		row.DataContext = details.Rows[0];
		var value = row.GetSelfAndVisualDescendants()
			.OfType<SelectableTextBlock>()
			.Single(block => block.Text == "Unavailable — method missing");
		value.SelectionStart = 14;
		value.SelectionEnd = value.Text!.Length;
		value.SelectedText.ShouldBe("method missing");

		details.UpdateFrom(new SelectedTabDetailsViewModel(
			"Selected terminal",
			"Author",
			[
				new SelectedTabDetailRowViewModel("External metrics", "Unavailable — method missing"),
				new SelectedTabDetailRowViewModel("Observed", "10:00:02")
			]));

		details.Rows[0].ShouldBeSameAs(row.DataContext);
		value.SelectedText.ShouldBe("method missing");
	}

	[AvaloniaTest]
	public void QuickActionButtonRaisesTemplateEvent()
	{
		RightActionsPanel actions = new();
		PromptTemplateRecord template = new("quick", "Quick", "hello", false);
		PromptTemplateRecord? received = null;
		actions.QuickActionRequested += (_, value) => received = value;
		var list = actions.FindControl<ItemsControl>("QuickActionsList")!;
		var itemTemplate = list.ItemTemplate.ShouldBeAssignableTo<IDataTemplate>()!;
		var item = itemTemplate.Build(template)!;
		item.DataContext = template;

		item.GetSelfAndVisualDescendants().OfType<Button>().Single()
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		received.ShouldBeSameAs(template);
	}

	[AvaloniaTest]
	public void SettingsButtonIsEnabledAndRaisesSettingsRequested()
	{
		RightActionsPanel actions = new();
		var raised = false;
		actions.SettingsRequested += (_, _) => raised = true;
		var settings = actions.FindControl<Button>("SettingsButton").ShouldBeOfType<Button>();

		settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		settings.IsEnabled.ShouldBeTrue();
		raised.ShouldBeTrue();
	}

	[AvaloniaTest]
	public void ScenarioButtonIsEnabledAndRaisesSelectedDefinition()
	{
		RightActionsPanel actions = new();
		ScenarioDefinition definition = new(
			"review", ScenarioKind.ReviewLoop, "Review", 2, "DONE", "target",
			"start", "first", "return", "feedback", [], string.Empty);
		ScenarioDefinition? received = null;
		actions.ScenarioRequested += (_, value) => received = value;
		var list = actions.FindControl<ItemsControl>("ScenarioActionsList")!;
		var template = list.ItemTemplate.ShouldBeAssignableTo<IDataTemplate>()!;
		var item = template.Build(definition).ShouldBeAssignableTo<Control>()!;
		item.DataContext = definition;
		var button = item.GetSelfAndVisualDescendants().OfType<Button>().Single();

		button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		button.IsEnabled.ShouldBeTrue();
		received.ShouldBeSameAs(definition);
	}

	[AvaloniaTest]
	public async Task Documents_tree_is_hidden_for_notes_and_shown_for_document_sections()
	{
		var theme = new FluentTheme();
		Application.Current!.Styles.Add(theme);
		var root = CreateRoot();
		try
		{
			Write(root, "README.md", "# Readme");
			var workspace = CreateWorkspace(root);
			await workspace.RefreshAsync(CancellationToken.None);
			RightActionsPanel actions = new() { Workspace = workspace };
			Window window = new() { Content = actions };
			window.Show();
			window.UpdateLayout();
			var panel = actions.FindControl<Grid>("DefaultActionsPanel")!;
			var documentsSection = actions.FindControl<Control>("DocumentsSection")!;

			documentsSection.IsVisible.ShouldBeFalse();
			panel.RowDefinitions[1].Height.ShouldBe(new GridLength(0));
			documentsSection.Bounds.Height.ShouldBe(0);

			await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
			window.UpdateLayout();

			actions.FindControl<Control>("DocumentsSection")!.IsVisible.ShouldBeTrue();
			panel.RowDefinitions[1].Height.ShouldBe(new GridLength(1, GridUnitType.Star));
			var tree = actions.FindControl<TreeView>("DocumentTree")!;
			tree.ItemsSource.ShouldNotBeNull().Cast<MarkdownTreeNodeViewModel>()
				.Select(node => node.Title).ShouldBe(["README.md"]);

			window.Close();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
			Application.Current!.Styles.Remove(theme);
		}
	}

	[AvaloniaTest]
	public async Task Restored_selection_becomes_the_actual_tree_selection()
	{
		var theme = new FluentTheme();
		Application.Current!.Styles.Add(theme);
		var root = CreateRoot();
		try
		{
			Write(root, "README.md", "# Readme");
			Write(root, "docs/notes.md", "# Notes");
			var workspace = CreateWorkspace(root);
			await workspace.RefreshAsync(CancellationToken.None);
			RightActionsPanel actions = new() { Workspace = workspace };
			Window window = new() { Width = 400, Height = 600, Content = actions };
			window.Show();
			var tree = actions.FindControl<TreeView>("DocumentTree")!;

			await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
			window.UpdateLayout();
			Dispatcher.UIThread.RunJobs();

			tree.SelectedItem.ShouldBeSameAs(workspace.SelectedNode);
			((MarkdownTreeNodeViewModel)tree.SelectedItem!).Title.ShouldBe("README.md");

			await workspace.SelectSectionAsync(DocsAndNotesSection.Docs, CancellationToken.None);
			window.UpdateLayout();
			Dispatcher.UIThread.RunJobs();

			tree.SelectedItem.ShouldBeNull();

			await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
			window.UpdateLayout();
			Dispatcher.UIThread.RunJobs();

			((MarkdownTreeNodeViewModel?)tree.SelectedItem)?.Title.ShouldBe("README.md");

			window.Close();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
			Application.Current!.Styles.Remove(theme);
		}
	}

	[AvaloniaTest]
	public async Task Clicking_a_folder_row_twice_expands_then_collapses_it_and_keeps_selection_coherent()
	{
		var theme = new FluentTheme();
		Application.Current!.Styles.Add(theme);
		var root = CreateRoot();
		try
		{
			Write(root, "README.md", "# Readme");
			Write(root, "src/details.md", "# Details");
			var workspace = CreateWorkspace(root);
			await workspace.RefreshAsync(CancellationToken.None);
			await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
			RightActionsPanel actions = new() { Workspace = workspace };
			Window window = new()
			{
				Width = 400,
				Height = 600,
				Content = actions
			};
			window.Show();
			window.UpdateLayout();
			Dispatcher.UIThread.RunJobs();
			actions.TreeNodeSelected += (_, node) =>
				workspace.SelectDocumentAsync(node, CancellationToken.None).GetAwaiter().GetResult();
			actions.FolderToggleRequested += (_, node) => workspace.ToggleFolder(node);
			var tree = actions.FindControl<TreeView>("DocumentTree")!;
			var readme = workspace.CommonTree.Single(node => !node.IsFolder);
			var folder = workspace.CommonTree.Single(node => node.IsFolder);
			var openedDocument = workspace.ActiveDocument;
			openedDocument.ShouldBeSameAs(readme.Document);

			ClickTreeRow(window, tree, folder);

			folder.IsExpanded.ShouldBeTrue();
			tree.SelectedItem.ShouldBeSameAs(folder);
			workspace.SelectedNode.ShouldBeSameAs(folder);
			workspace.ActiveDocument.ShouldBeSameAs(openedDocument);

			ClickTreeRow(window, tree, folder);

			folder.IsExpanded.ShouldBeFalse();
			tree.SelectedItem.ShouldBeSameAs(folder);
			workspace.SelectedNode.ShouldBeSameAs(folder);
			workspace.ActiveDocument.ShouldBeSameAs(openedDocument);

			window.Close();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
			Application.Current!.Styles.Remove(theme);
		}
	}

	[AvaloniaTest]
	public async Task Clicking_a_document_row_selects_that_document()
	{
		var theme = new FluentTheme();
		Application.Current!.Styles.Add(theme);
		var root = CreateRoot();
		try
		{
			Write(root, "README.md", "# Readme");
			Write(root, "AGENTS.md", "# Agents");
			var workspace = CreateWorkspace(root);
			await workspace.RefreshAsync(CancellationToken.None);
			await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
			RightActionsPanel actions = new() { Workspace = workspace };
			Window window = new() { Width = 400, Height = 600, Content = actions };
			window.Show();
			window.UpdateLayout();
			Dispatcher.UIThread.RunJobs();
			List<MarkdownTreeNodeViewModel> selected = [];
			actions.TreeNodeSelected += (_, node) => selected.Add(node);
			actions.FolderToggleRequested += (_, node) => selected.Add(node);
			var tree = actions.FindControl<TreeView>("DocumentTree")!;
			var agents = workspace.CommonTree.Single(node => node.Title == "AGENTS.md");

			ClickTreeRow(window, tree, agents);

			selected.ShouldHaveSingleItem().ShouldBeSameAs(agents);

			window.Close();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
			Application.Current!.Styles.Remove(theme);
		}
	}

	[AvaloniaTest]
	public async Task Folder_rows_render_a_folder_glyph_and_document_rows_do_not()
	{
		var theme = new FluentTheme();
		Application.Current!.Styles.Add(theme);
		var root = CreateRoot();
		try
		{
			Write(root, "README.md", "# Readme");
			Write(root, "src/details.md", "# Details");
			var workspace = CreateWorkspace(root);
			await workspace.RefreshAsync(CancellationToken.None);
			await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
			RightActionsPanel actions = new() { Workspace = workspace };
			Window window = new() { Width = 400, Height = 600, Content = actions };
			window.Show();
			window.UpdateLayout();
			Dispatcher.UIThread.RunJobs();
			var tree = actions.FindControl<TreeView>("DocumentTree")!;

			FindTreeRow(tree, workspace.CommonTree.Single(node => node.IsFolder))
				.GetVisualDescendants().OfType<PathIcon>()
				.Any(icon => icon.IsVisible).ShouldBeTrue();
			FindTreeRow(tree, workspace.CommonTree.Single(node => !node.IsFolder))
				.GetVisualDescendants().OfType<PathIcon>()
				.Any(icon => icon.IsVisible).ShouldBeFalse();

			window.Close();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
			Application.Current!.Styles.Remove(theme);
		}
	}

	private static TreeViewItem FindTreeRow(TreeView tree, MarkdownTreeNodeViewModel node)
		=> tree.GetVisualDescendants().OfType<TreeViewItem>()
			.Single(item => ReferenceEquals(item.DataContext, node));

	private static void ClickTreeRow(
		Window window,
		TreeView tree,
		MarkdownTreeNodeViewModel node)
	{
		var row = FindTreeRow(tree, node);
		PointerPressedEventArgs? pointerPressed = null;
		void Capture(object? sender, PointerPressedEventArgs args)
		{
			pointerPressed = args;
		}
		window.PointerPressed += Capture;
		Point clickPoint = new(4, 4);
		window.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.None);
		window.MouseUp(clickPoint, MouseButton.Left, RawInputModifiers.None);
		window.PointerPressed -= Capture;
		pointerPressed.ShouldNotBeNull();

		tree.SelectedItem = node;
		var content = row.GetVisualDescendants().OfType<StackPanel>()
			.Single(panel => ReferenceEquals(panel.DataContext, node)
				&& panel.Children.OfType<PathIcon>().Any());
		content.RaiseEvent(new TappedEventArgs(InputElement.TappedEvent, pointerPressed));
		Dispatcher.UIThread.RunJobs();
	}

	private static void Click(ToggleButton button) =>
		button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

	private static async Task<ProjectNoteDocument> CreateDocumentAsync(string text)
	{
		ProjectNoteDocument document = new(
			new FakeNotesStore(text),
			"C:\\repo",
			TimeSpan.FromHours(1));
		await document.LoadAsync(CancellationToken.None);
		return document;
	}

	private static DocsAndNotesWorkspaceViewModel CreateWorkspace(string root) => new(
		root,
		new ProjectNoteDocument(new FakeNotesStore(string.Empty), root, TimeSpan.FromHours(1)),
		new ProjectMarkdownFileStore(),
		TimeSpan.FromHours(1));

	private string CreateRoot()
	{
		var directory = TemporaryDirectory.Create();
		_temporaryDirectories.Add(directory);
		return directory.Path;
	}

	public void Dispose() => _temporaryDirectories.ForEach(static directory => directory.Dispose());

	private static string Write(string root, string relativePath, string text)
	{
		var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
		return path;
	}

	private sealed class FakeNotesStore(string text) : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) =>
			Task.FromResult(text);
		public Task SaveAsync(string projectRootPath, string value, CancellationToken cancellationToken) =>
			Task.CompletedTask;
		public Task AppendAsync(string projectRootPath, string value, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}

	[AvaloniaTest]
	public async Task Conflict_banner_actions_reload_disk_and_save_local_version()
	{
		var root = CreateRoot();
		try
		{
			var readme = Write(root, "README.md", "initial");
			var workspace = CreateWorkspace(root);
			await workspace.RefreshAsync(CancellationToken.None);
			await workspace.SelectSectionAsync(DocsAndNotesSection.Common, CancellationToken.None);
			NotesPaneView notes = new()
			{
				Workspace = workspace,
				ConfirmDiscardAsync = static () => Task.FromResult(false)
			};
			var editor = notes.FindControl<TextBox>("Editor")!;

			editor.Text = "mine";
			await File.WriteAllTextAsync(readme, "external");
			await workspace.ActiveDocument!.FlushAsync(CancellationToken.None);
			ObservedTaskGroup canceledReloadTasks = new(static (_, _) => Task.CompletedTask);
			notes.ConfigureLifecycle(canceledReloadTasks);
			notes.FindControl<Button>("ReloadFromDiskButton")!
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await canceledReloadTasks.CompleteAndDrainAsync();

			workspace.ActiveDocument.Text.ShouldBe("mine");
			notes.FindControl<Control>("ConflictBanner")!.IsVisible.ShouldBeTrue();

			notes.ConfirmDiscardAsync = static () => Task.FromResult(true);
			ObservedTaskGroup reloadTasks = new(static (_, _) => Task.CompletedTask);
			notes.ConfigureLifecycle(reloadTasks);
			notes.FindControl<Button>("ReloadFromDiskButton")!
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await reloadTasks.CompleteAndDrainAsync();

			workspace.ActiveDocument.Text.ShouldBe("external");
			notes.FindControl<Control>("ConflictBanner")!.IsVisible.ShouldBeFalse();

			editor.Text = "mine again";
			await File.WriteAllTextAsync(readme, "external again");
			await workspace.ActiveDocument.FlushAsync(CancellationToken.None);
			ObservedTaskGroup saveTasks = new(static (_, _) => Task.CompletedTask);
			notes.ConfigureLifecycle(saveTasks);
			notes.FindControl<Button>("SaveMineButton")!
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await saveTasks.CompleteAndDrainAsync();

			(await File.ReadAllTextAsync(readme)).ShouldBe("mine again");
			workspace.ActiveDocument.SaveStatus.State.ShouldBe(DocumentSaveState.Clean);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private sealed class ThrowingNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) =>
			Task.FromResult("notes");

		public Task SaveAsync(string projectRootPath, string value, CancellationToken cancellationToken) =>
			Task.FromException(new IOException("notes save failed"));

		public Task AppendAsync(string projectRootPath, string value, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}

	private sealed class FailOnceNotesStore : IProjectNotesStore
	{
		private bool _failNext = true;
		public string Saved { get; private set; } = string.Empty;

		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);

		public Task SaveAsync(string projectRootPath, string value, CancellationToken cancellationToken)
		{
			if (_failNext)
			{
				_failNext = false;
				throw new IOException("save failed");
			}

			Saved = value;
			return Task.CompletedTask;
		}

		public Task AppendAsync(string projectRootPath, string value, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}
}
