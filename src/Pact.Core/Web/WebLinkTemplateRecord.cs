namespace Pact.Core.Web;

/// <summary>
/// A reusable "open this page for the current project or ROOT" entry from
/// <c>web-link-templates.json</c>.
/// </summary>
/// <param name="Id">Stable key surviving edits to the title or URL.</param>
/// <param name="Title">Label shown in the web link menu.</param>
/// <param name="StartUrl">
/// URL template. Project placeholders such as <c>%gitLabRepoId%</c> and
/// <c>%teamCityProjectId%</c> are substituted before navigation. A missing known value,
/// including from ROOT, falls back to the template's site root.
/// </param>
public sealed record WebLinkTemplateRecord(
	string Id,
	string Title,
	string StartUrl);
