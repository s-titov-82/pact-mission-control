using System.Text.Json;
using Pact.Core.Projects;

namespace Pact.Core.Tests.Projects;

public sealed class ProjectLinkTests
{
	[Test]
	public void Normalizes_http_address_and_title_without_fetching()
	{
		var link = ProjectLinkRules.Create("  Build  ", "HTTPS://EXAMPLE.COM:443/a/../build", null, DateTimeOffset.UnixEpoch);
		link.ShouldBe(new ProjectLink("Build", "https://example.com/build", "other", DateTimeOffset.UnixEpoch));
	}

	[TestCase("", "https://example.com", null)]
	[TestCase("   ", "https://example.com", null)]
	[TestCase("Title", "relative", null)]
	[TestCase("Title", "file:///C:/foo", null)]
	[TestCase("Title", "https://user:secret@example.com", null)]
	[TestCase("Title", "https://example.com", "CI")]
	[TestCase("Title", "https://example.com", "")]
	public void Rejects_invalid_arguments(string title, string url, string? kind) =>
		Should.Throw<ArgumentException>(() => ProjectLinkRules.Create(title, url, kind, DateTimeOffset.UnixEpoch));

	[Test]
	public void Rejects_title_over_200_characters() =>
		Should.Throw<ArgumentException>(() => ProjectLinkRules.Create(new string('x', 201), "https://example.com", "ci", DateTimeOffset.UnixEpoch));

	[TestCase("ci")]
	[TestCase("merge_request")]
	[TestCase("issue")]
	[TestCase("other")]
	public void Accepts_exact_kind_http_and_200_character_title(string kind)
	{
		var link = ProjectLinkRules.Create(new string('x', 200), "http://EXAMPLE.COM:80", kind, DateTimeOffset.UnixEpoch);
		link.Title.Length.ShouldBe(200);
		link.Url.ShouldBe("http://example.com/");
		link.Kind.ShouldBe(kind);
	}

	[Test]
	public void Upsert_at_capacity_updates_normalized_duplicate_and_keeps_distinct_same_kind()
	{
		var links = Enumerable.Range(0, 50).Select(index => ProjectLinkRules.Create($"Build {index}", $"https://example.com/{index}", "ci", DateTimeOffset.UnixEpoch)).ToArray();
		var updated = ProjectLinkRules.Create("Latest", "https://EXAMPLE.COM:443/7", "ci", DateTimeOffset.UnixEpoch.AddDays(1));
		var result = ProjectLinkRules.Upsert(links, updated);
		result.Count.ShouldBe(50);
		result[0].ShouldBe(updated);
		result.Count(link => link.Url == updated.Url).ShouldBe(1);
		links[7].Title.ShouldBe("Build 7");
		Should.Throw<InvalidOperationException>(() => ProjectLinkRules.Upsert(result, updated with { Url = "https://example.com/new" }));
		result.Count.ShouldBe(50);
	}

	[Test]
	public void Old_project_json_defaults_to_empty_links()
	{
		var record = JsonSerializer.Deserialize<ProjectRecord>("""{"Id":"p","Name":"P","RootPath":"C:/P","CreatedAt":"2026-01-01T00:00:00Z","LastActiveAt":"2026-01-01T00:00:00Z","Notes":null}""");
		record.ShouldNotBeNull().Links.ShouldBeEmpty();
	}
}
