using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Styling;
using Pact.App.Avalonia.Views.Settings;
using Pact.Presentation.Settings;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class SettingsHelpWindowHeadlessTests
{
	[AvaloniaTest]
	public void Help_is_section_specific_and_read_only()
	{
		SettingsHelpWindow window = new(SettingsSection.Scenarios);

		window.RequestedThemeVariant.ShouldBe(ThemeVariant.Default);
		var title = window.FindControl<TextBlock>("HelpTitleText")!;
		var body = window.FindControl<TextBox>("HelpBodyTextBox")!;

		window.Title.ShouldBe("PACT:> Mission Control — Scenarios");
		title.Text.ShouldBe("Scenarios");
		body.Text!.Contains("review loop", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
		body.IsReadOnly.ShouldBeTrue();
		(body.TextWrapping == global::Avalonia.Media.TextWrapping.Wrap).ShouldBeTrue();
	}

	[AvaloniaTest]
	public void Parameterless_constructor_materializes_for_runtime_loader()
	{
		SettingsHelpWindow window = new();

		window.FindControl<TextBox>("HelpBodyTextBox").ShouldNotBeNull();
	}
}
