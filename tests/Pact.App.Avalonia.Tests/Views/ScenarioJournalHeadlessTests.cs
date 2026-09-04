using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Pact.App.Avalonia.Views;
using Pact.Core.Sessions;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class ScenarioJournalHeadlessTests
{
	[AvaloniaTest]
	public void JournalSurfaceContainsNoScenarioSetupDialog()
	{
		ScenarioJournalView view = new();

		view.GetLogicalDescendants().OfType<Window>().ShouldBeEmpty();
	}

	[AvaloniaTest]
	public void JournalActionsRaiseEventsWithCurrentRun()
	{
		using var run = CreateRun();
		ScenarioJournalView view = new() { DataContext = run };
		ScenarioRunViewModel? closed = null;
		ScenarioRunViewModel? softStopped = null;
		ScenarioRunViewModel? paused = null;
		ScenarioRunViewModel? aborted = null;
		ScenarioRunViewModel? resumed = null;
		view.CloseRunRequested += (_, value) => closed = value;
		view.SoftStopRequested += (_, value) => softStopped = value;
		view.PauseRequested += (_, value) => paused = value;
		view.AbortRequested += (_, value) => aborted = value;
		view.ResumeRequested += (_, value) => resumed = value;

		Click(view, "CloseRunButton");
		Click(view, "SoftStopButton");
		Click(view, "PauseButton");
		Click(view, "AbortButton");
		Click(view, "ResumeButton");

		closed.ShouldBeSameAs(run);
		softStopped.ShouldBeSameAs(run);
		paused.ShouldBeSameAs(run);
		aborted.ShouldBeSameAs(run);
		resumed.ShouldBeSameAs(run);
		run.Abort();
	}

	[AvaloniaTest]
	public void Journal_commands_use_shared_button_treatment_and_icons()
	{
		ScenarioJournalView view = new();

		foreach (var name in new[] { "CloseRunButton", "SoftStopButton", "PauseButton", "AbortButton", "ResumeButton" })
		{
			var button = view.FindControl<Button>(name)!;
			button.Classes.ShouldContain("scenario-command");
			button.HorizontalContentAlignment.ShouldBe(global::Avalonia.Layout.HorizontalAlignment.Center);
			button.VerticalContentAlignment.ShouldBe(global::Avalonia.Layout.VerticalAlignment.Center);
			button.GetLogicalDescendants().OfType<PathIcon>().ShouldNotBeEmpty();
		}

		var journal = view.FindControl<ToggleButton>("JournalToggleButton")!;
		journal.Classes.ShouldContain("scenario-command");
		journal.GetLogicalDescendants().OfType<PathIcon>().ShouldNotBeEmpty();
		view.FindControl<Control>("ScenarioJournalMarkdownView").ShouldNotBeNull();
		view.FindControl<Control>("ScenarioFinalResultMarkdownView").ShouldNotBeNull();
		view.GetLogicalDescendants().OfType<TextBox>().ShouldBeEmpty();
	}

	private static void Click(ScenarioJournalView view, string name) =>
		view.FindControl<Button>(name)!
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

	private static ScenarioRunViewModel CreateRun()
	{
		ScenarioRunService service = new(new WaitingGateway());
		ScenarioBlueprint blueprint = new(
			"test",
			"Test scenario",
			["reviewer"],
			[new ScenarioStepMetadata("step", "reviewer", null, "Run", ScenarioStepKind.Decision)],
			DefaultMaxIterations: 1,
			DefaultTarget: "target");
		var handle = service.Start(
			blueprint,
			new WaitingProgram(),
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"target",
			maxIterations: 1);
		return new ScenarioRunViewModel(handle, action => action());
	}

	private sealed class WaitingProgram : IScenarioProgram
	{
		public async Task<bool> RunIterationAsync(ScenarioIterationContext context, CancellationToken cancellationToken)
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return false;
		}
	}

	private sealed class WaitingGateway : IScenarioTerminalGateway
	{
		public Task<PromptDeliveryResult> SendPromptAsync(
			string sessionId,
			string prompt,
			bool confirmDelivery,
			CancellationToken cancellationToken) =>
			Task.FromResult(new PromptDeliveryResult(
				PromptDeliveryOutcome.Confirmed,
				string.Empty,
				WriteAttempted: true,
				SubmitAttempted: true));
		public Task SendEscapeAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
		public bool IsSessionAlive(string sessionId) => true;
		public string GetSessionLabel(string sessionId) => sessionId;
	}
}
