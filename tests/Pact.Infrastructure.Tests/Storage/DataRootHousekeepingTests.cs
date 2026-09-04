using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class DataRootHousekeepingTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _root => _temporaryDirectory.Path;

	[Test]
	public void Prepare_creates_only_the_four_top_level_directories()
	{
		AppPaths paths = new(_root);

		DataRootHousekeeping.Prepare(paths);

		Directory.GetDirectories(_root).Select(path => Path.GetFileName(path)).Order().ToArray().ShouldBe(["Logs", "Settings", "Temp", "WebView"]);
	}

	[Test]
	public void ClearSessionTemp_removes_only_session_children_and_preserves_retained_temp()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		Directory.CreateDirectory(Path.Combine(paths.SessionTempDirectory, "nested"));
		File.WriteAllText(Path.Combine(paths.SessionTempDirectory, "nested", "payload.tmp"), "temporary");
		File.WriteAllText(Path.Combine(paths.SessionTempDirectory, "loose.tmp"), "temporary");
		File.WriteAllText(Path.Combine(paths.RetainedTempDirectory, "keep.tmp"), "retained");
		Directory.CreateDirectory(paths.PactSkillsDirectory);
		File.WriteAllText(paths.PactCommonSkillPath, "published");
		File.WriteAllText(Path.Combine(paths.SettingsDirectory, "keep.json"), "durable");

		DataRootHousekeeping.ClearSessionTemp(paths);

		Directory.Exists(paths.SessionTempDirectory).ShouldBeTrue();
		Directory.EnumerateFileSystemEntries(paths.SessionTempDirectory).ShouldBeEmpty();
		File.ReadAllText(Path.Combine(paths.RetainedTempDirectory, "keep.tmp")).ShouldBe("retained");
		File.ReadAllText(paths.PactCommonSkillPath).ShouldBe("published");
		File.ReadAllText(Path.Combine(paths.SettingsDirectory, "keep.json")).ShouldBe("durable");
	}

	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}
}
