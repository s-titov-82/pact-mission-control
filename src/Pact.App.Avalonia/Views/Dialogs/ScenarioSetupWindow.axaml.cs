using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Dialogs;

internal sealed partial class ScenarioSetupWindow : Window
{
	public ScenarioSetupWindow()
	{
		InitializeComponent();
	}

	public ScenarioSetupWindow(ScenarioSetupViewModel viewModel)
		: this()
	{
		ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		DataContext = viewModel;
	}

	public ScenarioSetupViewModel? ViewModel { get; }

	public bool Accepted { get; private set; }

	private void OnRunClicked(object? sender, RoutedEventArgs e)
	{
		if (ViewModel?.CanRun != true)
		{
			return;
		}

		Accepted = true;
		Close(true);
	}

	private void OnCancelClicked(object? sender, RoutedEventArgs e) => Reject();

	private void OnWindowKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Escape)
		{
			return;
		}

		e.Handled = true;
		Reject();
	}

	private void Reject()
	{
		Accepted = false;
		Close(false);
	}
}