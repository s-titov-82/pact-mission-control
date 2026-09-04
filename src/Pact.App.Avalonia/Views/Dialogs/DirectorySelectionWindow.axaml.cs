using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
		Func<Exception, Task>? reportUserFailureAsync = null)
	{
		ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		_folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
		_eventTasks = eventTasks ?? new ObservedTaskGroup(
			static (_, _) => Task.CompletedTask);
		_reportUserFailureAsync = reportUserFailureAsync
			?? (_ => Task.CompletedTask);

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

	private void OnRecentDirectoryDoubleTapped(object? sender, TappedEventArgs e) =>
		TryAcceptSelection();

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
