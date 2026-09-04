using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Pact.Core.Projects;
using Pact.Core.Web;

namespace Pact.Presentation.Services;

/// <summary>
/// Expands a web link template's <c>%name%</c> placeholders against a project or
/// synthetic ROOT context.
/// </summary>
public static partial class WebLinkTemplateRenderer
{
	/// <summary>
	/// Renders <paramref name="template"/> for <paramref name="project"/>.
	/// </summary>
	/// <returns>
	/// The resolved URL, or a failure carrying a message to show the user. A known placeholder
	/// the project has no value for degrades to the site root rather than failing, so the link
	/// still opens somewhere useful; an unknown placeholder or a non-HTTP(S) result fails.
	/// Substituted values are URL-escaped except for path separators.
	/// </returns>
	public static WebLinkRenderResult Render(WebLinkTemplateRecord template, ProjectRecord project)
	{
		ArgumentNullException.ThrowIfNull(template);
		ArgumentNullException.ThrowIfNull(project);

		string rendered;
		var missingKnownPlaceholder = false;
		try
		{
			rendered = PlaceholderRegex().Replace(
				template.StartUrl,
				match =>
				{
					var name = match.Groups["name"].Value;
					var value = name switch
					{
						"gitLabRepoId" => project.GitLabRepoId,
						"teamCityProjectId" => project.TeamCityProjectId,
						_ => null
					};

					if (value is null && name is not "gitLabRepoId" and not "teamCityProjectId")
					{
						throw new WebLinkRenderException($"Unknown web link placeholder: {name}.");
					}

					if (string.IsNullOrWhiteSpace(value))
					{
						missingKnownPlaceholder = true;
						return string.Empty;
					}

					return Uri.EscapeDataString(value).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
				});

			if (missingKnownPlaceholder)
			{
				if (TryCreateSiteRoot(template.StartUrl, out var siteRoot))
				{
					return WebLinkRenderResult.Success(siteRoot);
				}

				return WebLinkRenderResult.Failure("Web link URL must be an absolute HTTP or HTTPS URL.");
			}

			if (!Uri.TryCreate(rendered, UriKind.Absolute, out var uri)
				|| uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
			{
				return WebLinkRenderResult.Failure("Web link URL must be an absolute HTTP or HTTPS URL.");
			}
		}
		catch (UriFormatException)
		{
			return WebLinkRenderResult.Failure("Web link URL must be an absolute HTTP or HTTPS URL.");
		}
		catch (WebLinkRenderException ex)
		{
			return WebLinkRenderResult.Failure(ex.Message);
		}

		return WebLinkRenderResult.Success(rendered);
	}

	private static bool TryCreateSiteRoot(string url, out string siteRoot)
	{
		siteRoot = string.Empty;
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
			|| uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
		{
			return false;
		}

		siteRoot = uri.GetLeftPart(UriPartial.Authority) + "/";
		return true;
	}

	[GeneratedRegex("%(?<name>[A-Za-z_][A-Za-z0-9_-]*)%")]
	private static partial Regex PlaceholderRegex();

	[SuppressMessage(
		"Design",
		"CA1064:Exceptions should be public",
		Justification = "This private renderer sentinel is converted to WebLinkRenderResult before the method returns.")]
	private sealed class WebLinkRenderException(string message) : Exception(message);
}

/// <summary>
/// Outcome of rendering a web link template.
/// </summary>
/// <param name="IsSuccess">Whether a usable URL was produced.</param>
/// <param name="Url">The URL on success; <see langword="null"/> on failure.</param>
/// <param name="ErrorMessage">Message to show the user on failure; <see langword="null"/> on success.</param>
public sealed record WebLinkRenderResult(bool IsSuccess, string? Url, string? ErrorMessage)
{
	/// <summary>Creates a successful result carrying <paramref name="url"/>.</summary>
	public static WebLinkRenderResult Success(string url) => new(true, url, null);

	/// <summary>Creates a failed result carrying <paramref name="errorMessage"/>.</summary>
	public static WebLinkRenderResult Failure(string errorMessage) => new(false, null, errorMessage);
}
