using Pact.Core.AgentControl;

namespace Pact.Core.Tests.AgentControl;

public sealed class ProjectDocumentPathTests : IDisposable
{
	private readonly TemporaryDirectory _projectDirectory = TemporaryDirectory.Create();
	private readonly TemporaryDirectory _externalDirectory = TemporaryDirectory.Create();
	private readonly List<string> _links = [];

	private string Root => _projectDirectory.Path;

	[Test]
	public void Relative_markdown_inside_the_root_resolves()
	{
		Write("docs/plan.md");

		ProjectDocumentPath.TryResolve(Root, "docs/plan.md", out var relativePath, out var failure)
			.ShouldBeTrue();

		relativePath.ShouldBe("docs/plan.md");
		failure.ShouldBeNull();
	}

	[Test]
	public void Absolute_path_inside_the_root_resolves()
	{
		Write("docs/plan.md");

		ProjectDocumentPath.TryResolve(
				Root,
				Path.Combine(Root, "docs", "plan.md"),
				out var relativePath,
				out _)
			.ShouldBeTrue();

		relativePath.ShouldBe("docs/plan.md");
	}

	[TestCase("../x.md")]
	[TestCase("docs/../../x.md")]
	public void Escaping_paths_are_refused(string path)
	{
		ProjectDocumentPath.TryResolve(Root, path, out _, out var failure).ShouldBeFalse();

		failure!.Code.ShouldBe("outside-project");
	}

	[Test]
	public void An_absolute_path_outside_the_root_is_refused()
	{
		var outside = WriteExternal("plan.md");

		ProjectDocumentPath.TryResolve(Root, outside, out _, out var failure).ShouldBeFalse();

		failure!.Code.ShouldBe("outside-project");
	}

	[Test]
	public void Junction_escaping_the_root_is_refused()
	{
		WriteExternal("plan.md");
		var link = Path.Combine(Root, "docs");
		if (!TryCreateJunction(link, _externalDirectory.Path))
		{
			Assert.Ignore("Directory junctions cannot be created here.");
		}

		ProjectDocumentPath.TryResolve(Root, "docs/plan.md", out _, out var failure).ShouldBeFalse();

		failure!.Code.ShouldBe("outside-project");
	}

	[Test]
	public void File_link_escaping_the_root_is_refused()
	{
		var target = WriteExternal("plan.md");
		var link = Path.Combine(Root, "docs", "plan.md");
		Directory.CreateDirectory(Path.GetDirectoryName(link)!);
		try
		{
			File.CreateSymbolicLink(link, target);
			_links.Add(link);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Assert.Ignore("File symbolic links are not permitted here.");
		}

		ProjectDocumentPath.TryResolve(Root, "docs/plan.md", out _, out var failure).ShouldBeFalse();

		failure!.Code.ShouldBe("outside-project");
	}

	[Test]
	public void Non_markdown_is_refused()
	{
		Write("a.txt");

		ProjectDocumentPath.TryResolve(Root, "a.txt", out _, out var failure).ShouldBeFalse();

		failure!.Code.ShouldBe("invalid-argument");
	}

	[TestCase("")]
	[TestCase("   ")]
	public void Blank_path_is_refused(string path)
	{
		ProjectDocumentPath.TryResolve(Root, path, out _, out var failure).ShouldBeFalse();

		failure!.Code.ShouldBe("invalid-argument");
	}

	[Test]
	public void Missing_file_is_refused()
	{
		ProjectDocumentPath.TryResolve(Root, "docs/none.md", out _, out var failure).ShouldBeFalse();

		failure!.Code.ShouldBe("not-found");
	}

	public void Dispose()
	{
		foreach (var link in _links)
		{
			if (Directory.Exists(link))
			{
				Directory.Delete(link);
			}
			else if (File.Exists(link) || new FileInfo(link).LinkTarget is not null)
			{
				File.Delete(link);
			}
		}

		_projectDirectory.Dispose();
		_externalDirectory.Dispose();
	}

	private void Write(string relativePath)
	{
		var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "# Test");
	}

	private string WriteExternal(string relativePath)
	{
		var path = Path.Combine(
			_externalDirectory.Path,
			relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "# External");
		return path;
	}

	private bool TryCreateJunction(string linkPath, string targetPath)
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

		using var process = System.Diagnostics.Process.Start(startInfo);
		if (process is null)
		{
			return false;
		}

		process.StandardOutput.ReadToEnd();
		process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0)
		{
			return false;
		}

		_links.Add(linkPath);
		return true;
	}
}
