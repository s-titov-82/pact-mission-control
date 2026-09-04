using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Dialogs;

internal sealed partial class GitCommitDialog : Window
{
	public GitCommitDialog() : this(new GitCommitDialogViewModel([])) { }

	public GitCommitDialog(GitCommitDialogViewModel viewModel)
	{
		InitializeComponent();
		ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		DataContext = viewModel;
	}

	public GitCommitDialogViewModel ViewModel { get; }
	public GitCommitDialogResult? Result { get; private set; }

	private void OnSelectAllClicked(object? sender, RoutedEventArgs e)
	{
		if (sender is CheckBox checkBox)
		{
			ViewModel.SetAllSelected(checkBox.IsChecked == true);
		}
	}

	private void OnCommitClicked(object? sender, RoutedEventArgs e)
	{
		Result = ViewModel.CreateResult();
		if (Result is not null)
		{
			Close(true);
		}
	}

	private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}