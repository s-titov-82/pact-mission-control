using Pact.Core.Git;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// What the push dialog produced.
/// </summary>
/// <param name="Remote">Target remote.</param>
/// <param name="Mode">Whether and how to force.</param>
/// <param name="SetUpstream">Whether to record the remote branch as upstream.</param>
public sealed record GitPushDialogResult(
	string Remote,
	GitPushMode Mode,
	bool SetUpstream);

/// <summary>Backs the push dialog: force mode and upstream tracking.</summary>
public sealed class GitPushDialogViewModel : SettingsObservableObject
{
	private bool _setUpstream;

	/// <summary>Creates the dialog model for a branch, disabling upstream when one already exists.</summary>
	public GitPushDialogViewModel(string branch, bool hasUpstream)
	{
		Branch = branch ?? string.Empty;
		CanChangeSetUpstream = !hasUpstream;
		_setUpstream = !hasUpstream;
	}

	/// <summary>Branch being pushed.</summary>
	public string Branch { get; }

	/// <summary>Selectable force modes.</summary>
	public IReadOnlyList<GitPushMode> Modes { get; } = Enum.GetValues<GitPushMode>();

	/// <summary>Whether upstream tracking is still a choice; false once the branch has one.</summary>
	public bool CanChangeSetUpstream { get; }

	/// <summary>Chosen force mode.</summary>
	public GitPushMode Mode
	{
		get;
		set => SetField(ref field, value);
	} = GitPushMode.Normal;

	/// <summary>Whether to set upstream on push.</summary>
	public bool SetUpstream
	{
		get => _setUpstream;
		set => SetField(ref _setUpstream, CanChangeSetUpstream && value);
	}

	/// <summary>Builds the result, always pushing to <c>origin</c>.</summary>
	public GitPushDialogResult CreateResult() => new("origin", Mode, SetUpstream);
}