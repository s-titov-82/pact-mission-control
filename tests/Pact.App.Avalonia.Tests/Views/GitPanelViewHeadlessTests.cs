using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Pact.App.Avalonia.Views;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class GitPanelViewHeadlessTests
{
	[AvaloniaTest]
	public void PanelContainsDataDrivenPopupButtonsList()
	{
		GitPanelView view = new();

		view.GetLogicalDescendants().OfType<ItemsControl>().ShouldContain(items => items.Name == "PopupButtonsList");
	}

	[AvaloniaTest]
	public async Task Branch_row_spins_while_the_panel_waits_for_git()
	{
		RunningGitRunner runner = new();
		GitPanelViewModel panel = new(
			@"D:\repo",
			runner,
			helperActions: [],
			launchHelperAction: (_, _, _) => { },
			directoryExists: _ => false);
		GitPanelView view = new() { DataContext = panel };
		Window window = new() { Content = view };
		window.Show();
		try
		{
			window.UpdateLayout();
			var indicator = view.FindControl<TextBlock>("GitActivityIndicator").ShouldNotBeNull();
			indicator.IsVisible.ShouldBeFalse();

			var command = panel.RunCommandAsync("Pull", ["pull"]);
			await runner.Started.Task;
			window.UpdateLayout();

			indicator.IsVisible.ShouldBeTrue();
			var first = view.AdvanceActivitySpinnerFrame();
			var second = view.AdvanceActivitySpinnerFrame();
			first.ShouldNotBe(second);
			indicator.Text.ShouldBe(second);

			runner.Release.SetResult();
			await command;
			window.UpdateLayout();

			indicator.IsVisible.ShouldBeFalse();
		}
		finally
		{
			window.Close();
		}
	}

	private sealed class RunningGitRunner : IGitCliRunner
	{
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<GitCommandResult> RunAsync(
			string workingDirectory,
			IReadOnlyList<string> arguments,
			IProgress<string>? outputLine,
			CancellationToken cancellationToken)
		{
			if (arguments.SequenceEqual(["pull"]))
			{
				Started.TrySetResult();
				await Release.Task;
			}

			return new GitCommandResult(0, string.Empty, string.Empty);
		}
	}

	[AvaloniaTest]
	public void PanelDoesNotExposeRedundantManualRefreshButton()
	{
		GitPanelView view = new();

		view.GetLogicalDescendants()
			.OfType<Button>()
			.Any(button =>
				string.Equals(
					button.Content?.ToString(),
					"Refresh",
					StringComparison.Ordinal))
			.ShouldBeFalse();
	}
}