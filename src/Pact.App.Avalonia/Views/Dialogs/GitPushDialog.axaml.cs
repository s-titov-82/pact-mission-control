using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Dialogs;

internal sealed partial class GitPushDialog : Window
{
	public GitPushDialog() : this(new GitPushDialogViewModel(string.Empty, hasUpstream: false)) { }

	public GitPushDialog(GitPushDialogViewModel viewModel)
	{
		InitializeComponent();
		ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		DataContext = viewModel;
	}

	public GitPushDialogViewModel ViewModel { get; }
	public GitPushDialogResult? Result { get; private set; }

	private void OnPushClicked(object? sender, RoutedEventArgs e)
	{
		Result = ViewModel.CreateResult();
		Close(true);
	}

	private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}