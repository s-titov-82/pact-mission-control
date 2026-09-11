using System.Text.Json.Serialization;

namespace Pact.Infrastructure.Updates;

internal sealed class GitHubReleaseDocument
{
	[JsonPropertyName("tag_name")]
	public string? Tag { get; init; }

	[JsonPropertyName("draft")]
	public bool? Draft { get; init; }

	[JsonPropertyName("prerelease")]
	public bool? Prerelease { get; init; }

	[JsonPropertyName("html_url")]
	public string? ReleaseNotesUrl { get; init; }

	[JsonPropertyName("assets")]
	public IReadOnlyList<GitHubReleaseAssetDocument>? Assets { get; init; }
}

internal sealed class GitHubReleaseAssetDocument
{
	[JsonPropertyName("name")]
	public string? Name { get; init; }

	[JsonPropertyName("browser_download_url")]
	public string? DownloadUrl { get; init; }

	[JsonPropertyName("size")]
	public long? Size { get; init; }

	[JsonPropertyName("state")]
	public string? State { get; init; }
}
