namespace Pact.Core.Projects;

/// <summary>A durable project-owned HTTP(S) resource; no remote status is stored.</summary>
/// <param name="Title">Short user-facing label.</param>
/// <param name="Url">Normalized absolute HTTP(S) address without user information.</param>
/// <param name="Kind">One of ci, merge_request, issue, or other.</param>
/// <param name="AddedAt">Time this address was most recently added.</param>
public sealed record ProjectLink(string Title, string Url, string Kind, DateTimeOffset AddedAt);

/// <summary>Validates project links and bounds the newest-first collection.</summary>
public static class ProjectLinkRules
{
	/// <summary>Creates a normalized link without accessing its remote resource.</summary>
	public static ProjectLink Create(string title, string url, string? kind, DateTimeOffset addedAt)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);
		var normalizedTitle = title.Trim();
		if (normalizedTitle.Length > 200)
		{
			throw new ArgumentException("Link titles must contain 1 to 200 characters.", nameof(title));
		}

		var normalizedKind = kind ?? "other";
		if (normalizedKind is not ("ci" or "merge_request" or "issue" or "other"))
		{
			throw new ArgumentException("Link kind must be ci, merge_request, issue, or other.", nameof(kind));
		}

		return new ProjectLink(normalizedTitle, NormalizeUrl(url), normalizedKind, addedAt);
	}

	/// <summary>Normalizes an absolute HTTP(S) address and rejects embedded credentials.</summary>
	public static string NormalizeUrl(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
			|| (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
			|| uri.UserInfo.Length != 0)
		{
			throw new ArgumentException("A link must be an absolute HTTP(S) address without user information.", nameof(url));
		}

		return uri.AbsoluteUri;
	}

	/// <summary>Replaces the same normalized address or adds a new one, refusing a 51st address.</summary>
	public static IReadOnlyList<ProjectLink> Upsert(IReadOnlyList<ProjectLink> links, ProjectLink link)
	{
		ArgumentNullException.ThrowIfNull(links);
		ArgumentNullException.ThrowIfNull(link);
		var others = links.Where(existing => !string.Equals(existing.Url, link.Url, StringComparison.Ordinal)).ToArray();
		if (others.Length >= 50)
		{
			throw new InvalidOperationException("The project already has 50 links; remove a link before adding a new address.");
		}

		return Array.AsReadOnly(new[] { link }.Concat(others).OrderByDescending(existing => existing.AddedAt).ToArray());
	}
}
