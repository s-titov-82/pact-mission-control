using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Pact.App.Avalonia.Views;
using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class ProjectTreeViewHeadlessTests
{
	[Test]
	public void Drag_threshold_and_drop_half_decisions_are_stable()
	{
		ProjectTreeView.HasExceededDragThreshold(
			new Point(10, 10),
			new Point(13, 13),
			new Size(8, 8)).ShouldBeFalse();
		ProjectTreeView.HasExceededDragThreshold(
			new Point(10, 10),
			new Point(15, 10),
			new Size(8, 8)).ShouldBeTrue();
		ProjectTreeView.IsInsertAfter(9, 20).ShouldBeFalse();
		ProjectTreeView.IsInsertAfter(10, 20).ShouldBeTrue();
	}

	[AvaloniaTest]
	public void Drop_targets_require_same_type_and_owner()
	{
		ProjectTreeView view = new();
		MainWindowViewModel viewModel = new(new EmptyProjectStore(), new EmptyNotesStore());
		var now = DateTimeOffset.UtcNow;
		SessionViewModel first = new(CreateSession("session-1", now));
		SessionViewModel second = new(CreateSession("session-2", now));
		SessionViewModel otherProject = new(CreateSession("session-3", now));
		SessionViewModel root = new(CreateSession("root-session", now), isRootItem: true);
		WorkspaceViewModel firstWorkspace = new(
			new ProjectRecord("project-1", "One", "C:\\one", now, now, null));
		WorkspaceViewModel secondWorkspace = new(
			new ProjectRecord("project-2", "Two", "C:\\two", now, now, null));
		firstWorkspace.Sessions.Add(first);
		firstWorkspace.Sessions.Add(second);
		secondWorkspace.Sessions.Add(otherProject);
		viewModel.Workspaces.Add(firstWorkspace);
		viewModel.Workspaces.Add(secondWorkspace);
		view.DataContext = viewModel;

		view.CanDropTreeItem(first, second).ShouldBeTrue();
		view.CanDropTreeItem(first, otherProject).ShouldBeFalse();
		view.CanDropTreeItem(first, root).ShouldBeFalse();
		view.CanDropTreeItem(first, first).ShouldBeFalse();
		view.CanDropTreeItem(first, new object()).ShouldBeFalse();
	}

	[AvaloniaTest]
	public void Orchestrator_tier_uses_the_terminal_current_style_only_while_selected()
	{
		MainWindowViewModel viewModel = new(new EmptyProjectStore(), new EmptyNotesStore());
		var now = DateTimeOffset.UtcNow;
		SessionViewModel orchestrator = new(CreateSession("orchestrator", now));
		viewModel.OrchestratorSlot.AttachSession(orchestrator);
		ProjectTreeView view = new() { DataContext = viewModel };
		SolidColorBrush surface = new(Color.Parse("#010203"));
		SolidColorBrush border = new(Color.Parse("#040506"));
		SolidColorBrush accentSoft = new(Color.Parse("#070809"));
		SolidColorBrush accent = new(Color.Parse("#0A0B0C"));
		Window window = new()
		{
			RequestedThemeVariant = ThemeVariant.Light,
			Content = view
		};
		window.Resources["AppSurfaceBrush"] = surface;
		window.Resources["AppBorderBrush"] = border;
		window.Resources["AppAccentSoftBrush"] = accentSoft;
		window.Resources["AppAccentBrush"] = accent;
		window.Show();
		var tier = view.FindControl<Border>("OrchestratorTier").ShouldNotBeNull();

		AssertTierStyle(
			tier,
			surface,
			border,
			new Thickness(1));

		viewModel.SelectedSession = orchestrator;

		viewModel.OrchestratorSlot.IsCurrent.ShouldBeTrue();
		tier.Classes.ShouldContain("current");
		AssertTierStyle(
			tier,
			accentSoft,
			accent,
			new Thickness(3, 0, 0, 0));

		viewModel.SelectedSession = null;

		viewModel.OrchestratorSlot.IsCurrent.ShouldBeFalse();
		tier.Classes.ShouldNotContain("current");
		AssertTierStyle(
			tier,
			surface,
			border,
			new Thickness(1));
		window.Close();
	}

	private static void AssertTierStyle(
		Border tier,
		SolidColorBrush expectedBackground,
		SolidColorBrush expectedBorder,
		Thickness borderThickness)
	{
		tier.Background.ShouldBeOfType<SolidColorBrush>().Color
			.ShouldBe(expectedBackground.Color);
		tier.BorderBrush.ShouldBeOfType<SolidColorBrush>().Color
			.ShouldBe(expectedBorder.Color);
		tier.BorderThickness.ShouldBe(borderThickness);
	}

	private static SessionRecord CreateSession(string id, DateTimeOffset now) => new(
		id,
		AgentKind.Codex,
		id,
		"C:\\",
		"codex",
		null,
		SessionStatus.Stopped,
		now,
		now);

	private sealed class EmptyProjectStore : IProjectStore
	{
		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) =>
			Task.FromResult(ProjectsDocument.CreateDefault());

		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task<ProjectsDocument> UpdateAsync(
			Func<ProjectsDocument, ProjectsDocument> update,
			CancellationToken cancellationToken) =>
			Task.FromResult(update(ProjectsDocument.CreateDefault()));
	}

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);

		public Task SaveAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task AppendAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}
}
