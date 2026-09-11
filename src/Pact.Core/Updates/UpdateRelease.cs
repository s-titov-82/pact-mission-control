namespace Pact.Core.Updates;

/// <summary>
/// Describes one immutable downloadable artifact attached to a release.
/// </summary>
/// <param name="Name">The exact published asset name.</param>
/// <param name="DownloadUri">The HTTPS asset download address.</param>
/// <param name="Size">The published content length in bytes.</param>
public sealed record UpdateAsset(string Name, Uri DownloadUri, long Size);

/// <summary>
/// Describes a validated stable release and its required Setup and checksum assets.
/// </summary>
/// <param name="Version">The parsed stable version.</param>
/// <param name="Tag">The exact GitHub tag.</param>
/// <param name="ReleaseNotesUri">The public release-notes address.</param>
/// <param name="Setup">The required Windows Setup asset.</param>
/// <param name="Checksums">The required checksum manifest asset.</param>
public sealed record UpdateRelease(
	StableReleaseVersion Version,
	string Tag,
	Uri ReleaseNotesUri,
	UpdateAsset Setup,
	UpdateAsset Checksums);
