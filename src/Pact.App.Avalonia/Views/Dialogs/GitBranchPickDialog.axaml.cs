using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Dialogs;

internal sealed partial class GitBranchPickDialog : Window
{
	public GitBranchPickDialog()
		: this(new GitBranchPickDialogViewModel([], false), "Choose branch", string.Empty, "Choose") { }

	public GitBranchPickDialog(
		GitBranchPickDialogViewModel viewModel,
		string title,
		string helpText,
		string acceptText)
	{
		InitializeComponent();
		ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		Title = title;
		HelpTextBlock.Text = helpText;
		AcceptButton.Content = acceptText;
		DataContext = viewModel;
	}

	public GitBranchPickDialogViewModel ViewModel { get; }
	public GitBranchPickDialogResult? Result { get; private set; }

	private void OnAcceptClicked(object? sender, RoutedEventArgs e)
	{
		Result = ViewModel.CreateResult();
		if (Result is not null)
		{
			Close(true);
		}
	}

	private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}