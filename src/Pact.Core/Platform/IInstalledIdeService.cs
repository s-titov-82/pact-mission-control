namespace Pact.Core.Platform;

/// <summary>Discovers installed editors and opens an explicitly supplied current project root.</summary>
public interface IInstalledIdeService
{
	/// <summary>Returns cached installations unless refresh is requested; discovery never launches an editor.</summary>
	Task<IReadOnlyList<InstalledIde>> DiscoverAsync(bool refresh, CancellationToken cancellationToken);

	/// <summary>Opens the existing project root; process-start failures are returned to the caller.</summary>
	Task OpenAsync(InstalledIde ide, string projectRoot, CancellationToken cancellationToken);
}
