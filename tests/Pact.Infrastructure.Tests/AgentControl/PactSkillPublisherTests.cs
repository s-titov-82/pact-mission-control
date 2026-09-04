using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.AgentControl;

public sealed class PactSkillPublisherTests
{
	[Test]
	public async Task PublishAsync_writes_all_application_owned_files_under_retained_temp()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		AppPaths paths = new(temporaryDirectory.Path);
		PactSkillPublisher publisher = new(paths);

		PactSkillPublication publication =
			await publisher.PublishAsync(CancellationToken.None);

		File.Exists(publication.McpSkillPath).ShouldBeTrue();
		File.Exists(publication.CommonSkillPath).ShouldBeTrue();
		Path.GetDirectoryName(publication.McpSkillPath).ShouldBe(paths.PactSkillsDirectory);
		Path.GetDirectoryName(publication.CommonSkillPath).ShouldBe(paths.PactSkillsDirectory);
	}

	[Test]
	public async Task PublishAsync_replaces_stale_application_owned_content()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		AppPaths paths = new(temporaryDirectory.Path);
		Directory.CreateDirectory(paths.PactSkillsDirectory);
		await File.WriteAllTextAsync(paths.PactMcpSkillPath, "stale");

		await new PactSkillPublisher(paths).PublishAsync(CancellationToken.None);

		string content = await File.ReadAllTextAsync(paths.PactMcpSkillPath);
		content.ShouldContain("pact_request_review");
		content.ShouldNotContain("stale");
	}

	[Test]
	public async Task Published_guidance_documents_all_new_notes_review_and_web_tools()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		AppPaths paths = new(temporaryDirectory.Path);

		var publication =
			await new PactSkillPublisher(paths).PublishAsync(CancellationToken.None);
		string content = await File.ReadAllTextAsync(publication.McpSkillPath!);

		string[] toolNames =
		[
			"pact_get_notes",
			"pact_replace_notes",
			"pact_get_review_run",
			"pact_pause_review",
			"pact_resume_review",
			"pact_get_project_notes",
			"pact_replace_project_notes",
			"pact_append_project_note",
			"pact_list_web_tabs",
			"pact_resume_web_tab",
			"pact_get_web_tab_html"
		];
		foreach (var toolName in toolNames)
		{
			content.ShouldContain(toolName);
		}
	}

	[Test]
	public async Task PublishAsync_is_idempotent()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		AppPaths paths = new(temporaryDirectory.Path);
		PactSkillPublisher publisher = new(paths);

		PactSkillPublication first = await publisher.PublishAsync(CancellationToken.None);
		byte[] firstMcpBytes = await File.ReadAllBytesAsync(first.McpSkillPath!);
		byte[] firstCommonBytes = await File.ReadAllBytesAsync(first.CommonSkillPath!);

		PactSkillPublication second = await publisher.PublishAsync(CancellationToken.None);

		second.ShouldBe(first);
		(await File.ReadAllBytesAsync(second.McpSkillPath!)).ShouldBe(firstMcpBytes);
		(await File.ReadAllBytesAsync(second.CommonSkillPath!)).ShouldBe(firstCommonBytes);
	}

	[Test]
	public async Task PublishAsync_does_not_publish_credentials()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		AppPaths paths = new(temporaryDirectory.Path);

		PactSkillPublication publication =
			await new PactSkillPublisher(paths).PublishAsync(CancellationToken.None);

		foreach (string path in new[] { publication.McpSkillPath!, publication.CommonSkillPath! })
		{
			string content = await File.ReadAllTextAsync(path);
			content.ShouldNotContain("PACT_AGENT_CONTROL_TOKEN");
			content.ShouldNotContain("Authorization");
			content.ShouldNotContain("Bearer ", Case.Insensitive);
		}
	}

	[Test]
	public async Task PublishAsync_failure_leaves_no_temporary_file()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		AppPaths paths = new(temporaryDirectory.Path);
		Directory.CreateDirectory(paths.RetainedTempDirectory);
		await File.WriteAllTextAsync(paths.PactSkillsDirectory, "not a directory");

		await Should.ThrowAsync<IOException>(
			() => new PactSkillPublisher(paths).PublishAsync(CancellationToken.None));

		Directory
			.EnumerateFiles(paths.RetainedTempDirectory, "*.tmp", SearchOption.AllDirectories)
			.ShouldBeEmpty();
	}
}
