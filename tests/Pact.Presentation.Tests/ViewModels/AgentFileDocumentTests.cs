using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class AgentFileDocumentTests
{
	[Test]
	public async Task Loading_reports_placeholders()
	{
		using var folder = TemporaryDirectory.Create();
		var binary = Path.Combine(folder.Path, "a.bin");
		await File.WriteAllBytesAsync(binary, [0x41, 0x00]);
		var large = Path.Combine(folder.Path, "big.log");
		await File.WriteAllBytesAsync(large, new byte[(2 * 1024 * 1024) + 1]);

		var missing = await AgentFileDocument.LoadAsync(Path.Combine(folder.Path, "none.txt"), null, CancellationToken.None);
		var binaryDocument = await AgentFileDocument.LoadAsync(binary, null, CancellationToken.None);
		var largeDocument = await AgentFileDocument.LoadAsync(large, null, CancellationToken.None);

		(missing.Placeholder, missing.Text, missing.FileName).ShouldBe(("File not found", null, "none.txt"));
		(binaryDocument.Placeholder, binaryDocument.Text).ShouldBe(("Binary file, not shown", null));
		(largeDocument.Placeholder, largeDocument.Text).ShouldBe(("File is too large to show", null));
	}

	[Test]
	public async Task A_text_file_carries_its_text()
	{
		using var folder = TemporaryDirectory.Create();
		var path = Path.Combine(folder.Path, "run.ps1");
		await File.WriteAllTextAsync(path, "Write-Host hi");

		var document = await AgentFileDocument.LoadAsync(path, null, CancellationToken.None);

		document.ShouldBe(new AgentFileDocument(path, "run.ps1", "Write-Host hi", null, IsMarkdown: false, null, null));
	}

	[Test]
	public async Task A_markdown_file_in_the_project_keeps_its_project_path()
	{
		using var folder = TemporaryDirectory.Create();
		Directory.CreateDirectory(Path.Combine(folder.Path, "docs"));
		var path = Path.Combine(folder.Path, "docs", "Plan.MD");
		await File.WriteAllTextAsync(path, "# Plan");

		var document = await AgentFileDocument.LoadAsync(path, folder.Path, CancellationToken.None);

		document.IsMarkdown.ShouldBeTrue();
		document.ProjectRoot.ShouldBe(folder.Path);
		document.ProjectRelativePath.ShouldBe("docs/Plan.MD");
	}

	[Test]
	public async Task A_markdown_file_outside_the_project_has_no_project_path()
	{
		using var project = TemporaryDirectory.Create();
		using var elsewhere = TemporaryDirectory.Create();
		var path = Path.Combine(elsewhere.Path, "notes.md");
		await File.WriteAllTextAsync(path, "# Notes");

		var document = await AgentFileDocument.LoadAsync(path, project.Path, CancellationToken.None);

		document.IsMarkdown.ShouldBeTrue();
		document.ProjectRoot.ShouldBeNull();
		document.ProjectRelativePath.ShouldBeNull();
	}
}
