using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Lifecycle;
using Pact.Core.Platform;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Dialogs;

internal sealed partial class DirectorySelectionWindow : Window
{
	private readonly IFolderPicker _folderPicker;
	private readonly ObservedTaskGroup _eventTasks;
	private readonly Func<Exception, Task> _reportUserFailureAsync;
	private bool _closing;
	private readonly Func<string, Task>? _forgetDirectoryAsync;

	public DirectorySelectionWindow()
		: this(
			new DirectorySelectionViewModel([], string.Empty),
			new NullFolderPicker(),
			new ObservedTaskGroup(static (_, _) => Task.CompletedTask))
	{
	}

	public DirectorySelectionWindow(
		DirectorySelectionViewModel viewModel,
		IFolderPicker folderPicker,
		ObservedTaskGroup? eventTasks = null,
		Func<Exception, Task>? reportUserFailureAsync = null,
		Func<string, Task>? forgetDirectoryAsync = null)
	{
		ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		_folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
		_eventTasks = eventTasks ?? new ObservedTaskGroup(
			static (_, _) => Task.CompletedTask);
		_reportUserFailureAsync = reportUserFailureAsync
			?? (_ => Task.CompletedTask);

		_forgetDirectoryAsync = forgetDirectoryAsync;
		InitializeComponent();
		DataContext = ViewModel;
		Opened += (_, _) =>
		{
			DirectoryTextBox.SelectAll();
			DirectoryTextBox.Focus();
		};
		Closed += (_, _) => _closing = true;
	}

	public DirectorySelectionViewModel ViewModel { get; }

	private void OnBrowseClicked(object? sender, RoutedEventArgs e)
	{
		if (!_closing)
		{
			_eventTasks.TryRun(
				"directory-browse",
				BrowseAsync,
				_reportUserFailureAsync);
		}
	}

	private void OnForgetClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Button { DataContext: string path } && _forgetDirectoryAsync is { } forget)
		{
			_eventTasks.TryRun("forget-directory", async () =>
			{
				await forget(path);
				ViewModel.RecentDirectories.Remove(path);
			}, _reportUserFailureAsync);
		}
	}

	private async Task BrowseAsync()
	{
		var initialDirectory = Directory.Exists(ViewModel.DirectoryText)
			? ViewModel.DirectoryText
			: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var folder = await _folderPicker.PickFolderAsync(
			initialDirectory,
			"Select working directory");
		if (string.IsNullOrWhiteSpace(folder))
		{
			return;
		}

		ViewModel.DirectoryText = folder;
		DirectoryTextBox.SelectAll();
		DirectoryTextBox.Focus();
	}

	private void OnRecentDirectoryDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (e.Source is not Control source || source is not Button && source.FindAncestorOfType<Button>() is null)
		{
			TryAcceptSelection();
		}
	}

	private void OnStartClicked(object? sender, RoutedEventArgs e) =>
		TryAcceptSelection();

	private void OnCancelClicked(object? sender, RoutedEventArgs e) =>
		Close(null);

	private void TryAcceptSelection()
	{
		if (ViewModel.CreateResult() is { } result)
		{
			Close(result);
		}
	}

	private sealed class NullFolderPicker : IFolderPicker
	{
		public Task<string?> PickFolderAsync(string? initialDirectory, string title) =>
			Task.FromResult<string?>(null);
	}
}
