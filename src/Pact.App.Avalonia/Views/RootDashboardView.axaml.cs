using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

/// <summary>Project overview whose actions carry existing owner/session identities.</summary>
public sealed partial class RootDashboardView : UserControl
{
	/// <summary>Creates the global overview control.</summary>
	public RootDashboardView()
	{
		InitializeComponent();
	}
	/// <summary>Opens a card's project overview.</summary>
	public event EventHandler<WorkspaceViewModel>? ProjectRequested;
	/// <summary>Activates one existing live agent.</summary>
	public event EventHandler<SessionViewModel>? AgentRequested;
	/// <summary>Opens the original project's Changes tab.</summary>
	public event EventHandler<WorkspaceViewModel>? ChangesRequested;
	/// <summary>Uses the application's existing project picker.</summary>
	public event EventHandler? AddProjectRequested;
	/// <summary>Restores the original paused project through the existing resume flow.</summary>
	public event EventHandler<WorkspaceViewModel>? ResumeProjectRequested;
	/// <summary>Opens one recent directory through the existing project-creation flow.</summary>
	public event EventHandler<string>? RecentDirectoryRequested;

	private void OnDashboardSizeChanged(object? sender, global::Avalonia.Controls.SizeChangedEventArgs args)
	{
		if (ProjectCards.GetVisualDescendants().OfType<WrapPanel>().FirstOrDefault() is { } panel)
		{
			panel.ItemWidth = Math.Min(342, Math.Max(120, Bounds.Width - 32));
		}
	}

	private void OnProjectClicked(object? sender, RoutedEventArgs args)
	{
		args.Handled = true;
		if (sender is Control { DataContext: ProjectDashboardCardViewModel card })
		{ ProjectRequested?.Invoke(this, card.Workspace); }
	}
	private void OnChangesClicked(object? sender, RoutedEventArgs args)
	{
		args.Handled = true;
		if (sender is Control { DataContext: ProjectDashboardCardViewModel card })
		{ ChangesRequested?.Invoke(this, card.Workspace); }
	}
	private void OnAgentClicked(object? sender, RoutedEventArgs args)
	{
		args.Handled = true;
		if (sender is Control { DataContext: SessionViewModel session })
		{ AgentRequested?.Invoke(this, session); }
	}
	private void OnAddProjectClicked(object? sender, RoutedEventArgs args) => AddProjectRequested?.Invoke(this, EventArgs.Empty);
	private void OnResumeProjectClicked(object? sender, RoutedEventArgs args)
	{
		args.Handled = true;
		if (sender is Control { DataContext: WorkspaceViewModel workspace })
		{ ResumeProjectRequested?.Invoke(this, workspace); }
	}
	private void OnRecentDirectoryClicked(object? sender, RoutedEventArgs args)
	{
		args.Handled = true;
		if (sender is Control { DataContext: string directory })
		{ RecentDirectoryRequested?.Invoke(this, directory); }
	}
}