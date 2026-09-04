using Pact.Core.AgentControl;

namespace Pact.Presentation.Services.AgentControl;

/// <summary>
/// Holds the reviewer profiles used by the running application and replaces the whole snapshot
/// whenever external settings are reloaded.
/// </summary>
public sealed class ReviewProfileProvider
{
	private readonly string _path;
	private IReadOnlyList<ReviewProfile> _current = [];

	/// <summary>Creates a provider over the reviewer-profile file at <paramref name="path"/>.</summary>
	public ReviewProfileProvider(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		_path = path;
	}

	/// <summary>Profiles as of the last successful refresh; empty before the first refresh.</summary>
	public IReadOnlyList<ReviewProfile> Current => _current;

	/// <summary>Re-reads the settings file and atomically replaces the published snapshot.</summary>
	public async Task RefreshAsync(CancellationToken cancellationToken)
	{
		var profiles = await ReviewProfileReader.ReadAsync(_path, cancellationToken)
			.ConfigureAwait(false);
		Interlocked.Exchange(ref _current, profiles);
	}
}
