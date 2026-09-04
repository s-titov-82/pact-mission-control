using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Settings;

/// <summary>
/// Thin <see cref="IProjectSettingsEditor"/> wrapper over <see cref="MainWindowViewModel"/>, so
/// <see cref="ViewModels.ProjectsSectionViewModel"/> can edit live workspace/session state
/// without depending on the window view model directly.
/// </summary>
public sealed class ProjectSettingsEditor : IProjectSettingsEditor, IRootTabsSettingsEditor
{
	private readonly MainWindowViewModel _mainWindowViewModel;

	/// <summary>Creates an editor that applies edits through the main window model.</summary>
	public ProjectSettingsEditor(MainWindowViewModel mainWindowViewModel)
	{
		ArgumentNullException.ThrowIfNull(mainWindowViewModel);
		_mainWindowViewModel = mainWindowViewModel;
	}

	/// <inheritdoc />
	public Task UpdateProjectSettingsAsync(string projectId, ProjectSettingsEdit edit, CancellationToken ct)
		=> _mainWindowViewModel.UpdateProjectSettingsAsync(projectId, edit, ct);

	/// <inheritdoc />
	public Task UpdateSessionSettingsAsync(string sessionId, SessionSettingsEdit edit, CancellationToken ct)
		=> _mainWindowViewModel.UpdateSessionSettingsAsync(sessionId, edit, ct);

	/// <inheritdoc />
	public Task UpdateRootSessionSettingsAsync(
		string sessionId,
		SessionSettingsEdit edit,
		CancellationToken cancellationToken) =>
		_mainWindowViewModel.UpdateSessionSettingsAsync(sessionId, edit, cancellationToken);

	/// <inheritdoc />
	public Task UpdateRootWebPageSettingsAsync(
		string webPageId,
		RootWebPageSettingsEdit edit,
		CancellationToken cancellationToken) =>
		_mainWindowViewModel.UpdateRootWebPageSettingsAsync(webPageId, edit, cancellationToken);

	/// <summary>Ensures a workspace exists for <paramref name="directory"/> and returns its id.</summary>
	public async Task<string?> CreateProjectForDirectoryAsync(string directory, CancellationToken ct)
	{
		var workspace = await _mainWindowViewModel.EnsureWorkspaceForDirectoryAsync(directory, ct).ConfigureAwait(false);
		return workspace.Id;
	}
}
