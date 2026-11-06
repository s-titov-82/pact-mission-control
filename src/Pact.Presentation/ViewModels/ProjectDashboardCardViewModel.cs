using System.ComponentModel;
using System.Globalization;

namespace Pact.Presentation.ViewModels;

/// <summary>Projects existing session facts and a lightweight Git summary for one open project.</summary>
public sealed class ProjectDashboardCardViewModel(WorkspaceViewModel workspace) : INotifyPropertyChanged
{
	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;
	/// <summary>The original project owner, revalidated when navigating.</summary>
	public WorkspaceViewModel Workspace { get; } = workspace;
	/// <summary>Root captured with this card, preventing old Git reads from crossing a root edit.</summary>
	public string RootPath { get; } = workspace.RootPath;
	/// <summary>Live agent sessions; status remains owned by each session.</summary>
	public IReadOnlyList<SessionViewModel> Agents { get; private set; } = [];
	/// <summary>Whether this project has no live agent sessions.</summary>
	public bool HasNoAgents => Agents.Count == 0;
	/// <summary>Whether this project offers working-tree changes.</summary>
	public bool HasGitRepository { get; } = workspace.IsGitRepository;
	/// <summary>Unknown, failed, clean or changed status, never a guessed clean state.</summary>
	public string ChangesText { get; private set; } = "Checking changes…";

	internal void RefreshAgents(Func<SessionViewModel, bool> isLiveAgent)
	{
		var agents = Workspace.Sessions.Where(isLiveAgent).ToArray();
		if (Agents.SequenceEqual(agents))
		{ return; }
		Agents = agents;
		PropertyChanged?.Invoke(this, new(nameof(Agents)));
		PropertyChanged?.Invoke(this, new(nameof(HasNoAgents)));
	}

	internal void SetCount(int? count)
	{
		ChangesText = count switch
		{
			null => "Changes unavailable",
			0 => "No uncommitted changes",
			_ => string.Create(CultureInfo.InvariantCulture, $"Changes · {count}")
		};
		PropertyChanged?.Invoke(this, new(nameof(ChangesText)));
	}
}