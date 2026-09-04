namespace Pact.Infrastructure.Tests.Documents;

public sealed class ProjectMarkdownCatalogTests : IDisposable
{
	private readonly TemporaryDirectory _projectDirectory = TemporaryDirectory.Create();
	private readonly TemporaryDirectory _externalDirectory = TemporaryDirectory.Create();
	private string _root => _projectDirectory.Path;
	private string _externalRoot => _externalDirectory.Path;

	[Test]
	public void Scan_puts_every_non_docs_markdown_in_common_and_the_docs_tree_in_docs()
	{
		Write("ReadMe.md");
		Write("AGENTS.md");
		Write("src/Service/README.md");
		Write("src/Service/details.md");
		Write("docs/README.md");
		Write("docs/manual-tests/checklist.md");
		Write("docs/superpowers/specs/feature.md");

		var catalog = ProjectMarkdownCatalog.Scan(_root);

		catalog.Common.Select(file => file.RelativePath).ShouldBe([
			"AGENTS.md",
			"ReadMe.md",
			"src/Service/details.md",
			"src/Service/README.md"
		]);
		catalog.Docs.Select(file => file.RelativePath).ShouldBe([
			"docs/manual-tests/checklist.md",
			"docs/README.md",
			"docs/superpowers/specs/feature.md"
		]);
	}

	[Test]
	public void Scan_skips_generated_directory_markdown()
	{
		string[] generatedPaths = [
			".git/README.md",
			".worktrees/branch/README.md",
			".pact-reviews/run/README.md",
			"src/bin/README.md",
			"src/obj/README.md",
			"node_modules/package/README.md"
		];
		foreach (var generatedPath in generatedPaths)
		{
			Write(generatedPath);
		}

		var catalog = ProjectMarkdownCatalog.Scan(_root);

		foreach (var generatedPath in generatedPaths)
		{
			catalog.Common.Select(file => file.RelativePath).ShouldNotContain(generatedPath);
			catalog.Docs.Select(file => file.RelativePath).ShouldNotContain(generatedPath);
		}
	}

	[Test]
	public void Scan_does_not_traverse_docs_reparse_root()
	{
		WriteExternal("outside.md");
		WriteExternal("nested/README.md");
		WriteExternal("superpowers/specs/outside.md");
		var docsLink = Path.Combine(_root, "docs");
		Directory.CreateDirectory(_root);
		CreateDirectoryJunction(docsLink, _externalRoot);

		var catalog = ProjectMarkdownCatalog.Scan(_root);

		catalog.Common.ShouldBeEmpty();
		catalog.Docs.ShouldBeEmpty();
	}

	[Test]
	public void Scan_reads_a_project_root_that_is_itself_a_junction()
	{
		WriteExternal("README.md");
		WriteExternal("docs/guide.md");
		var rootLink = Path.Combine(_root, "linked-root");
		Directory.CreateDirectory(_root);
		CreateDirectoryJunction(rootLink, _externalRoot);

		var catalog = ProjectMarkdownCatalog.Scan(rootLink);

		catalog.Common.Select(file => file.RelativePath).ShouldBe(["README.md"]);
		catalog.Docs.Select(file => file.RelativePath).ShouldBe(["docs/guide.md"]);
	}

	[Test]
	public void Scan_returns_empty_groups_for_a_project_without_markdown()
	{
		Directory.CreateDirectory(_root);

		var catalog = ProjectMarkdownCatalog.Scan(_root);

		catalog.Common.ShouldBeEmpty();
		catalog.Docs.ShouldBeEmpty();
	}

	public void Dispose()
	{
		foreach (var junctionName in new[] { "docs", "linked-root" })
		{
			var junctionPath = Path.Combine(_root, junctionName);
			if (Directory.Exists(junctionPath)
				&& (File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) != 0)
			{
				Directory.Delete(junctionPath);
			}
		}

		_projectDirectory.Dispose();
		_externalDirectory.Dispose();
	}

	private void Write(string relativePath)
	{
		var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "# Test");
	}

	private void WriteExternal(string relativePath)
	{
		var path = Path.Combine(
			_externalRoot,
			relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "# External");
	}

	private static void CreateDirectoryJunction(string linkPath, string targetPath)
	{
		System.Diagnostics.ProcessStartInfo startInfo = new()
		{
			FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add("/d");
		startInfo.ArgumentList.Add("/c");
		startInfo.ArgumentList.Add("mklink");
		startInfo.ArgumentList.Add("/J");
		startInfo.ArgumentList.Add(linkPath);
		startInfo.ArgumentList.Add(targetPath);

		using var process =
			System.Diagnostics.Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start junction creation.");
		var standardOutput = process.StandardOutput.ReadToEnd();
		var standardError = process.StandardError.ReadToEnd();
		process.WaitForExit();
		process.ExitCode.ShouldBe(
			0,
			$"Failed to create junction.{Environment.NewLine}{standardOutput}{standardError}");
	}
}
