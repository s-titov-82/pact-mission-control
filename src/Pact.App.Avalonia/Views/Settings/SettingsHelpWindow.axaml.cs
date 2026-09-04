using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.Presentation.Settings;

namespace Pact.App.Avalonia.Views.Settings;

internal sealed partial class SettingsHelpWindow : Window
{
	public SettingsHelpWindow()
		: this(SettingsSection.Projects)
	{
	}

	public SettingsHelpWindow(SettingsSection section)
	{
		InitializeComponent();
		(var title, var body) = SettingsHelpContent.Get(section);
		Title = $"{AppProfileDefaults.ProductTitle} — {title}";
		HelpTitleText.Text = title;
		HelpBodyTextBox.Text = body;
	}

	private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
