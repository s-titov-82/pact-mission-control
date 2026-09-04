using System.Text.RegularExpressions;

namespace Pact.Presentation.Services.Git;

/// <summary>
/// Extracts the GitLab project path from a git remote URL, so web link templates can be filled
/// in without the user typing the repo id.
/// </summary>
public static partial class GitLabRemoteUrlParser
{
	/// <summary>
	/// Attempts to read the <c>group/project</c> id from a remote URL.
	/// </summary>
	/// <param name="remoteUrl">
	/// Remote URL in either standard (<c>https://host/group/project.git</c>) or SCP-like
	/// (<c>git@host:group/project.git</c>) form.
	/// </param>
	/// <param name="repoId">The project path with any <c>.git</c> suffix removed.</param>
	/// <returns>
	/// <see langword="false"/> when the URL is blank, unparseable, does not point at a GitLab
	/// host, or carries no group segment. A false result simply means the project has no GitLab
	/// link to offer.
	/// </returns>
	public static bool TryGetRepoId(string remoteUrl, out string repoId)
	{
		repoId = string.Empty;
		if (string.IsNullOrWhiteSpace(remoteUrl))
		{
			return false;
		}

		var trimmed = remoteUrl.Trim();
		if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
		{
			return TryCreateRepoId(uri.Host, uri.AbsolutePath, out repoId);
		}

		var scpLikeMatch = ScpLikeRemoteRegex().Match(trimmed);
		return scpLikeMatch.Success
			&& TryCreateRepoId(
				scpLikeMatch.Groups["host"].Value,
				scpLikeMatch.Groups["path"].Value,
				out repoId);
	}

	private static bool TryCreateRepoId(string host, string path, out string repoId)
	{
		repoId = string.Empty;
		if (!host.Contains("gitlab", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var normalizedPath = path
			.Trim()
			.TrimStart('/')
			.TrimEnd('/');
		if (normalizedPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			normalizedPath = normalizedPath[..^4];
		}

		if (string.IsNullOrWhiteSpace(normalizedPath)
			|| !normalizedPath.Contains('/', StringComparison.Ordinal))
		{
			return false;
		}

		repoId = normalizedPath;
		return true;
	}

	[GeneratedRegex(@"^[^@\s]+@(?<host>[^:\s]+):(?<path>.+)$")]
	private static partial Regex ScpLikeRemoteRegex();
}