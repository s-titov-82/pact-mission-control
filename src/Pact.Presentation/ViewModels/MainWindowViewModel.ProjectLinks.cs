using Pact.Core.Projects;

namespace Pact.Presentation.ViewModels;

public sealed partial class MainWindowViewModel
{
	/// <summary>Persists a normalized resource against the latest project state, then reconciles its projection.</summary>
	public async Task<ProjectLink> AddProjectLinkAsync(string projectId, string title, string url, string? kind, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
		var link = ProjectLinkRules.Create(title, url, kind, DateTimeOffset.UtcNow);
		var updated = await _persistence.UpdateProjectAsync(
			projectId,
			project => project with { Links = ProjectLinkRules.Upsert(project.Links, link) },
			cancellationToken)
			?? throw new KeyNotFoundException("The project no longer exists.");
		FindWorkspaceViewModel(projectId)?.UpdateRecord(updated);
		return link;
	}

	/// <summary>Removes a normalized resource address from the latest persisted project state.</summary>
	public async Task RemoveProjectLinkAsync(string projectId, string url, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
		var normalizedUrl = ProjectLinkRules.NormalizeUrl(url);
		var updated = await _persistence.UpdateProjectAsync(
			projectId,
			project => project with
			{
				Links = Array.AsReadOnly(project.Links.Where(link => !string.Equals(link.Url, normalizedUrl, StringComparison.Ordinal)).ToArray())
			},
			cancellationToken)
			?? throw new KeyNotFoundException("The project no longer exists.");
		FindWorkspaceViewModel(projectId)?.UpdateRecord(updated);
	}
}
