using Pact.Core.Updates;

namespace Pact.Infrastructure.Updates;

/// <summary>Owns download, integrity verification, and reuse of staged Pact Setup packages.</summary>
public interface IUpdatePackageStore
{
	/// <summary>Returns an existing package only after revalidating its manifest, size, and hash.</summary>
	Task<PreparedUpdatePackage?> TryGetVerifiedAsync(
		UpdateRelease release,
		CancellationToken cancellationToken);

	/// <summary>Streams both release assets, verifies them, and atomically publishes the Setup.</summary>
	Task<PreparedUpdatePackage> DownloadAndVerifyAsync(
		UpdateRelease release,
		IProgress<long>? progress,
		CancellationToken cancellationToken);
}
