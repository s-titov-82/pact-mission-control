namespace Pact.Presentation.Services;

/// <summary>Where a link in a Markdown preview leads.</summary>
public abstract record MarkdownLinkTarget;

/// <summary>A Markdown file of the project, and a heading in it when the link names one.</summary>
public sealed record ProjectDocumentLink(string RelativePath, string? Heading) : MarkdownLinkTarget;

/// <summary>A heading of the document the link is in.</summary>
public sealed record HeadingLink(string Heading) : MarkdownLinkTarget;

/// <summary>A web page, for the system browser.</summary>
public sealed record WebLink(Uri Uri) : MarkdownLinkTarget;

/// <summary>Nowhere Pact can go; the notice says why.</summary>
public sealed record RefusedLink(string Notice) : MarkdownLinkTarget;

/// <summary>Resolves the links of a project's Markdown against the document they are in.</summary>
public static class MarkdownLinks
{
	/// <summary>
	/// Where <paramref name="href"/>, written in <paramref name="documentRelativePath"/>, leads:
	/// relative to the document's folder, or to the project root after a leading <c>/</c>. A file is
	/// opened only when it is Markdown inside the project and exists. Without a document, as for
	/// notes, only web links and headings lead anywhere.
	/// </summary>
	public static MarkdownLinkTarget Resolve(string? projectRoot, string? documentRelativePath, string href)
	{
		ArgumentNullException.ThrowIfNull(href);
		var link = href.Trim();
		if (link.Length == 0)
		{
			return new RefusedLink("Not found: (empty link)");
		}

		if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
		{
			return new WebLink(uri);
		}

		var hash = link.IndexOf('#', StringComparison.Ordinal);
		var heading = hash < 0 ? null : Uri.UnescapeDataString(link[(hash + 1)..]);
		var target = Uri.UnescapeDataString(hash < 0 ? link : link[..hash]).Replace('\\', '/');
		if (target.Length == 0)
		{
			return heading is { Length: > 0 }
				? new HeadingLink(heading)
				: new RefusedLink($"Not found: {href}");
		}

		if (string.IsNullOrWhiteSpace(projectRoot) || documentRelativePath is null)
		{
			return new RefusedLink($"Not found: {href}");
		}

		var root = Path.GetFullPath(projectRoot);
		string full;
		if (Path.IsPathRooted(target) && !target.StartsWith('/'))
		{
			full = Path.GetFullPath(target);
		}
		else
		{
			var folder = target.StartsWith('/')
				? root
				: Path.GetDirectoryName(Path.Combine(root, documentRelativePath.Replace('/', Path.DirectorySeparatorChar))) ?? root;
			full = Path.GetFullPath(Path.Combine(folder, target.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
		}

		var relative = Path.GetRelativePath(root, full);
		if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
		{
			return new RefusedLink($"Outside the project: {href}");
		}

		if (!string.Equals(Path.GetExtension(full), ".md", StringComparison.OrdinalIgnoreCase))
		{
			return new RefusedLink($"Not a Markdown file: {href}");
		}

		return File.Exists(full)
			? new ProjectDocumentLink(relative.Replace(Path.DirectorySeparatorChar, '/'), string.IsNullOrEmpty(heading) ? null : heading)
			: new RefusedLink($"Not found: {href}");
	}
}
