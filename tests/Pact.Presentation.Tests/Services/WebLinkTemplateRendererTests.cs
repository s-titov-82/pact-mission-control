using Pact.Core.Projects;
using Pact.Core.Web;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class WebLinkTemplateRendererTests
{
	[Test]
	public void Render_replaces_known_project_placeholders()
	{
		WebLinkTemplateRecord template = new(
			"gitlab-mrs",
			"GitLab MRs",
			"https://gitlab/%gitLabRepoId%/-/merge_requests");
		var project = CreateProject() with { GitLabRepoId = "group/repo" };

		var result = WebLinkTemplateRenderer.Render(template, project);

		result.IsSuccess.ShouldBeTrue();
		result.Url.ShouldBe("https://gitlab/group/repo/-/merge_requests");
	}

	[Test]
	public void Render_preserves_percent_encoded_url_text()
	{
		WebLinkTemplateRecord template = new(
			"encoded",
			"Encoded",
			"https://example/search?q=a%3Db%20c");

		var result = WebLinkTemplateRenderer.Render(template, CreateProject());

		result.IsSuccess.ShouldBeTrue();
		result.Url.ShouldBe("https://example/search?q=a%3Db%20c");
	}

	[Test]
	public void Render_opens_site_root_when_project_placeholder_value_is_missing()
	{
		WebLinkTemplateRecord template = new(
			"gitlab-mrs",
			"GitLab MRs",
			"https://gitlab/%gitLabRepoId%/-/merge_requests");

		var result = WebLinkTemplateRenderer.Render(template, CreateProject());

		result.IsSuccess.ShouldBeTrue();
		result.Url.ShouldBe("https://gitlab/");
	}

	[Test]
	public void Render_opens_site_root_when_teamcity_project_placeholder_value_is_missing()
	{
		WebLinkTemplateRecord template = new(
			"teamcity",
			"TeamCity",
			"https://teamcity/project.html?projectId=%teamCityProjectId%");

		var result = WebLinkTemplateRenderer.Render(template, CreateProject());

		result.IsSuccess.ShouldBeTrue();
		result.Url.ShouldBe("https://teamcity/");
	}

	[Test]
	public void Render_rejects_unknown_placeholder()
	{
		WebLinkTemplateRecord template = new(
			"unknown",
			"Unknown",
			"https://example/%unknown%");

		var result = WebLinkTemplateRenderer.Render(template, CreateProject());

		result.IsSuccess.ShouldBeFalse();
		result.ErrorMessage.ShouldBe("Unknown web link placeholder: unknown.");
	}

	[Test]
	public void Render_rejects_malformed_placeholder()
	{
		WebLinkTemplateRecord template = new(
			"malformed",
			"Malformed",
			"https://gitlab/%git_lab_repo_id%");

		var result = WebLinkTemplateRenderer.Render(template, CreateProject());

		result.IsSuccess.ShouldBeFalse();
		result.ErrorMessage.ShouldBe("Unknown web link placeholder: git_lab_repo_id.");
	}

	[Test]
	public void Render_rejects_non_http_urls()
	{
		WebLinkTemplateRecord template = new("file", "File", "file:///C:/temp");

		var result = WebLinkTemplateRenderer.Render(template, CreateProject());

		result.IsSuccess.ShouldBeFalse();
		result.ErrorMessage.ShouldBe("Web link URL must be an absolute HTTP or HTTPS URL.");
	}

	private static ProjectRecord CreateProject()
	{
		var now = DateTimeOffset.UtcNow;
		return new ProjectRecord("project-1", "Project", @"D:\Work\Project", now, now, Notes: null);
	}
}