using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class ScenarioSetupWindowHeadlessTests
{
	[AvaloniaTest]
	public void Window_materializes_complete_setup_form_and_bindings()
	{
		var viewModel = CreateViewModel();
		ScenarioSetupWindow window = new(viewModel);

		window.RequestedThemeVariant.ShouldBe(ThemeVariant.Default);
		window.FindControl<ItemsControl>("StepsList")!.ItemsSource.ShouldBeSameAs(viewModel.StepRows);
		window.FindControl<ItemsControl>("RoleBindingsList")!.ItemsSource.ShouldBeSameAs(viewModel.RoleBindings);
		window.FindControl<TextBox>("TargetTextBox")!.Text.ShouldBe(viewModel.Target);
		window.FindControl<TextBox>("MaxIterationsTextBox")!.Text.ShouldBe(viewModel.MaxIterations.ToString());
		window.FindControl<ComboBox>("ReviewerInstructionCombo")!.ItemsSource.ShouldBeSameAs(viewModel.ReviewerInstructionOptions);
		window.FindControl<TextBox>("ReviewerInstructionTextBox")!.Text.ShouldBe(viewModel.ReviewerInstructionText);
		(window.FindControl<TextBox>("TargetTextBox")!.TextWrapping == global::Avalonia.Media.TextWrapping.Wrap).ShouldBeTrue();
		(window.FindControl<TextBox>("ReviewerInstructionTextBox")!.TextWrapping == global::Avalonia.Media.TextWrapping.Wrap).ShouldBeTrue();
		AssertScenarioCommand(window.FindControl<Button>("RunButton")!);
		AssertScenarioCommand(window.FindControl<Button>("CancelButton")!);
	}

	[AvaloniaTest]
	public void Run_accepts_only_valid_setup_and_cancel_or_escape_rejects()
	{
		var valid = CreateViewModel();
		ScenarioSetupWindow accepted = new(valid);
		var run = accepted.FindControl<Button>("RunButton")!;
		run.IsEnabled.ShouldBeTrue();
		run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		accepted.Accepted.ShouldBeTrue();

		var invalid = CreateViewModel(oneSessionOnly: true);
		ScenarioSetupWindow rejected = new(invalid);
		rejected.FindControl<Button>("RunButton")!.IsEnabled.ShouldBeFalse();
		rejected.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		rejected.Accepted.ShouldBeFalse();

		ScenarioSetupWindow escaped = new(valid);
		escaped.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyDownEvent,
			Key = Key.Escape
		});
		escaped.Accepted.ShouldBeFalse();
	}

	private static void AssertScenarioCommand(Button button)
	{
		button.Classes.ShouldContain("scenario-command");
		button.HorizontalContentAlignment.ShouldBe(global::Avalonia.Layout.HorizontalAlignment.Center);
		button.VerticalContentAlignment.ShouldBe(global::Avalonia.Layout.VerticalAlignment.Center);
		button.GetLogicalDescendants().OfType<PathIcon>().ShouldNotBeEmpty();
	}

	[AvaloniaTest]
	public void Validation_text_and_save_default_state_follow_view_model()
	{
		var viewModel = CreateViewModel(oneSessionOnly: true);
		ScenarioSetupWindow window = new(viewModel);

		window.FindControl<TextBlock>("ValidationText")!.Text.ShouldBe(viewModel.ValidationMessage);
		var save = window.FindControl<CheckBox>("SaveTargetCheckBox")!;
		save.IsChecked = true;
		viewModel.SaveTargetAsDefault.ShouldBeTrue();
	}

	private static ScenarioSetupViewModel CreateViewModel(bool oneSessionOnly = false)
	{
		var author = Session("author", "Author");
		var reviewer = Session("reviewer", "Reviewer");
		ScenarioDefinition definition = new(
			"review-loop", ScenarioKind.ReviewLoop, "Review loop", 3, "DONE", "Target",
			"start", "feedback", "return", "follow-up",
			[new("strict", "Strict", "Review strictly")], "strict");
		ScenarioBlueprint blueprint = new(
			"review-loop", "Review loop", ["author", "reviewer"],
			[new("send", "author", "reviewer", "Send", ScenarioStepKind.Send)],
			3, "Target");
		return new ScenarioSetupViewModel(
			blueprint,
			definition,
			oneSessionOnly ? [author] : [author, reviewer]);
	}

	private static SessionViewModel Session(string id, string title) => new(new SessionRecord(
		id, AgentKind.Codex, title, @"D:\repo", "codex", null, SessionStatus.Running,
		DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
}
