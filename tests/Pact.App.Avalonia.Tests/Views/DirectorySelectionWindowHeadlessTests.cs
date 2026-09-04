using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Core.Platform;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class DirectorySelectionWindowHeadlessTests
{
	[AvaloniaTest]
	public void Parameterless_constructor_materializes_for_runtime_loader()
	{
		DirectorySelectionWindow window = new();

		window.FindControl<TextBox>("DirectoryTextBox").ShouldNotBeNull();
	}

	[AvaloniaTest]
	public void Window_contains_editable_path_browse_recent_validation_start_and_cancel_controls()
	{
		DirectorySelectionWindow window = new(
			new DirectorySelectionViewModel([Path.GetTempPath()], Path.GetTempPath()),
			new FakeFolderPicker());

		var path = window.FindControl<TextBox>("DirectoryTextBox").ShouldBeOfType<TextBox>();
		var browse = window.FindControl<Button>("BrowseButton").ShouldBeOfType<Button>();
		var recent = window.FindControl<ListBox>("RecentDirectoryList").ShouldBeOfType<ListBox>();
		var validation = window.FindControl<TextBlock>("ValidationText").ShouldBeOfType<TextBlock>();
		var start = window.FindControl<Button>("StartButton").ShouldBeOfType<Button>();
		var cancel = window.FindControl<Button>("CancelButton").ShouldBeOfType<Button>();

		path.IsReadOnly.ShouldBeFalse();
		browse.Content.ShouldBe("Browse...");
		recent.Items.ShouldHaveSingleItem();
		validation.Text.ShouldBe(string.Empty);
		start.Content.ShouldBe("Start");
		cancel.Content.ShouldBe("Cancel");
	}

	[AvaloniaTest]
	public async Task Double_clicking_valid_recent_directory_accepts_it()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var directory = temporaryDirectory.Path;
		try
		{
			Window owner = new() { Width = 300, Height = 200 };
			owner.Show();
			DirectorySelectionWindow window = new(
				new DirectorySelectionViewModel([directory], string.Empty),
				new FakeFolderPicker());
			var dialog =
				window.ShowDialog<DirectorySelectionResult?>(owner);
			Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();

			var recent = window.FindControl<ListBox>("RecentDirectoryList")!;
			recent.SelectedItem = directory;
			Dispatcher.UIThread.RunJobs();
			window.ViewModel.DirectoryText.ShouldBe(directory);

			PointerPressedEventArgs? pointerPressed = null;
			owner.PointerPressed += (_, args) => pointerPressed = args;
			Point clickPoint = new(20, 20);
			owner.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.None);
			owner.MouseUp(clickPoint, MouseButton.Left, RawInputModifiers.None);
			pointerPressed.ShouldNotBeNull();
			recent.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, pointerPressed));
			Dispatcher.UIThread.RunJobs();

			var result = await dialog.WaitAsync(TimeSpan.FromSeconds(2));

			result.ShouldNotBeNull();
			result.Directory.ShouldBe(Path.GetFullPath(directory));
			owner.Close();
		}
		finally
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
	}

	[AvaloniaTest]
	public async Task Browse_failure_is_projected_through_the_owner_reporter()
	{
		TaskCompletionSource<Exception> projected = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		ObservedTaskGroup eventTasks = new(static (_, _) => Task.CompletedTask);
		DirectorySelectionWindow window = new(
			new DirectorySelectionViewModel([], Path.GetTempPath()),
			new ThrowingFolderPicker(),
			eventTasks,
			exception =>
			{
				projected.TrySetResult(exception);
				return Task.CompletedTask;
			});

		window.FindControl<Button>("BrowseButton")!
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await eventTasks.CompleteAndDrainAsync();

		(await projected.Task).Message.ShouldBe("picker failed");
	}

	private sealed class FakeFolderPicker : IFolderPicker
	{
		public Task<string?> PickFolderAsync(string? initialDirectory, string title) =>
			Task.FromResult<string?>(null);
	}

	private sealed class ThrowingFolderPicker : IFolderPicker
	{
		public Task<string?> PickFolderAsync(string? initialDirectory, string title) =>
			Task.FromException<string?>(new IOException("picker failed"));
	}
}
