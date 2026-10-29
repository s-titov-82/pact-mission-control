using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class MarkdownLinksTests : IDisposable
{
	private const string Document = "docs/a/x.md";
	private readonly TemporaryDirectory _root = TemporaryDirectory.Create();

	public MarkdownLinksTests()
	{
		foreach (var file in new[] { "README.md", "docs/b.md", "docs/a/x.md", "docs/a/other.md", "docs/a/my file.md", "docs/a/sub/c.md", "docs/a/x.cs" })
		{
			var path = Path.Combine(_root.Path, file.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, "# x");
		}
	}

	public void Dispose() => _root.Dispose();

	[TestCase("other.md", "docs/a/other.md")]
	[TestCase("../b.md", "docs/b.md")]
	[TestCase("/README.md", "README.md")]
	[TestCase("my%20file.md", "docs/a/my file.md")]
	[TestCase("sub\\c.md", "docs/a/sub/c.md")]
	[TestCase("./other.md", "docs/a/other.md")]
	public void A_relative_link_to_a_project_markdown_file_resolves_to_its_path(string href, string expected) =>
		Resolve(href).ShouldBe(new ProjectDocumentLink(expected, null));

	[Test]
	public void A_heading_after_a_file_is_kept() =>
		Resolve("other.md#Some-Part").ShouldBe(new ProjectDocumentLink("docs/a/other.md", "Some-Part"));

	[Test]
	public void A_bare_heading_stays_in_the_document() =>
		Resolve("#usage").ShouldBe(new HeadingLink("usage"));

	[Test]
	public void A_link_climbing_out_of_the_project_is_refused() =>
		Resolve("../../../x.md").ShouldBe(new RefusedLink("Outside the project: ../../../x.md"));

	[Test]
	public void An_absolute_path_outside_the_project_is_refused() =>
		Resolve("C:/Windows/x.md").ShouldBe(new RefusedLink("Outside the project: C:/Windows/x.md"));

	[Test]
	public void A_missing_file_is_refused() =>
		Resolve("nope.md").ShouldBe(new RefusedLink("Not found: nope.md"));

	[TestCase("https://example.com/a?b=c")]
	[TestCase("http://example.com")]
	public void An_http_link_is_web(string href) =>
		Resolve(href).ShouldBe(new WebLink(new Uri(href)));

	[Test]
	public void A_link_to_a_non_markdown_project_file_is_refused() =>
		Resolve("x.cs").ShouldBe(new RefusedLink("Not a Markdown file: x.cs"));

	[Test]
	public void An_empty_link_is_refused() =>
		Resolve(" ").ShouldBeOfType<RefusedLink>();

	// Notes have no place in the project, yet web links and their own headings still work.
	[Test]
	public void Without_a_document_web_links_and_headings_still_resolve()
	{
		MarkdownLinks.Resolve(null, null, "https://example.com").ShouldBe(new WebLink(new Uri("https://example.com")));
		MarkdownLinks.Resolve(null, null, "#usage").ShouldBe(new HeadingLink("usage"));
		MarkdownLinks.Resolve(null, null, "other.md").ShouldBe(new RefusedLink("Not found: other.md"));
	}

	private MarkdownLinkTarget Resolve(string href) => MarkdownLinks.Resolve(_root.Path, Document, href);
}
