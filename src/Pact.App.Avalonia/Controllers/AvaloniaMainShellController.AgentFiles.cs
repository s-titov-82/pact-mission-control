using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

internal sealed partial class AvaloniaMainShellController
{
	// Bumped by every open, close and change of the shown item: a load that finishes under an
	// older value was overtaken and is dropped.
	private int _agentFileGeneration;

	/// <summary>Files linked by the agent of the shown session; <see langword="null"/> when no session is shown.</summary>
	public AgentFilesViewModel? CurrentAgentFiles { get; private set; }

	/// <summary>The linked file shown over the terminal, or <see langword="null"/> when the panel is closed.</summary>
	public AgentFileDocument? ShownAgentFile { get; private set; }

	/// <summary>Project of the session <see cref="ShownAgentFile"/> was opened from; <see langword="null"/> for ROOT.</summary>
	public string? ShownAgentFileProjectId { get; private set; }

	/// <summary>Reads a linked file for the panel: path, project root, cancellation.</summary>
	internal Func<string, string?, CancellationToken, Task<AgentFileDocument>> AgentFileLoader { get; set; } =
		AgentFileDocument.LoadAsync;

	/// <summary>Reads <paramref name="item"/> and shows it, unless something else was opened or shown meanwhile.</summary>
	public async Task OpenAgentFileAsync(AgentFileItem item, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(item);
		var generation = ++_agentFileGeneration;
		var workspace = CurrentAgentFiles is { } files
			? FindWorkspace(candidate => candidate.Sessions.Contains(files.Session))
			: null;
		var document = await AgentFileLoader(item.Path, workspace?.RootPath, cancellationToken);
		if (generation != _agentFileGeneration || _disposed)
		{
			return;
		}

		ShownAgentFileProjectId = workspace?.Id;
		ShownAgentFile = document;
		OnPropertyChanged(nameof(ShownAgentFile));
	}

	/// <summary>Closes the panel and drops any load still running.</summary>
	public void CloseAgentFile()
	{
		_agentFileGeneration++;
		if (ShownAgentFile is null)
		{
			return;
		}

		ShownAgentFile = null;
		ShownAgentFileProjectId = null;
		OnPropertyChanged(nameof(ShownAgentFile));
	}

	// Called after every presentation: the panel stays only while the same session stays shown the
	// same way (a terminal finishing loading is the same), and the list follows the shown session.
	private void UpdateCurrentAgentFiles(PresentedSurface previous, PresentedSurface next)
	{
		var same = (previous, next) switch
		{
			(PresentedSurface.Terminal before, PresentedSurface.Terminal after) => ReferenceEquals(before.Session, after.Session),
			(PresentedSurface.PausedSession before, PresentedSurface.PausedSession after) => ReferenceEquals(before.Session, after.Session),
			_ => false
		};
		if (!same)
		{
			CloseAgentFile();
		}

		var session = next switch
		{
			PresentedSurface.Terminal terminal => terminal.Session,
			PresentedSurface.PausedSession paused => paused.Session,
			_ => null
		};
		if (ReferenceEquals(CurrentAgentFiles?.Session, session))
		{
			return;
		}

		CurrentAgentFiles?.Dispose();
		CurrentAgentFiles = session is null
			? null
			: new AgentFilesViewModel(
				session,
				FindWorkspace(workspace => workspace.Sessions.Contains(session))?.RootPath,
				AgentFilesViewModel.DefaultTempRoots,
				ViewModel.RemoveSessionFileLinkAsync);
		OnPropertyChanged(nameof(CurrentAgentFiles));
	}

	// A paused or closed project keeps showing its last terminal, so the panel and the list of its
	// session are let go here rather than by a change of surface; loads still running are dropped.
	private void ReleaseAgentFiles(WorkspaceViewModel workspace)
	{
		var ownsList = CurrentAgentFiles is { } files && workspace.Sessions.Contains(files.Session);
		if (ownsList || string.Equals(ShownAgentFileProjectId, workspace.Id, StringComparison.Ordinal))
		{
			CloseAgentFile();
		}

		if (!ownsList)
		{
			return;
		}

		CurrentAgentFiles!.Dispose();
		CurrentAgentFiles = null;
		OnPropertyChanged(nameof(CurrentAgentFiles));
	}

	private void DisposeAgentFiles()
	{
		_agentFileGeneration++;
		CurrentAgentFiles?.Dispose();
		CurrentAgentFiles = null;
	}
}
