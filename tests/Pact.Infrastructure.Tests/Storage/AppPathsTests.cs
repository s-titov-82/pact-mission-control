using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class AppPathsTests
{
	[Test]
	public void Constructor_maps_product_paths_below_the_four_data_directories()
	{
		AppPaths paths = new(@"C:\profile");

		paths.SettingsDirectory.ShouldBe(@"C:\profile\Settings");
		paths.ProjectsPath.ShouldBe(@"C:\profile\Settings\projects.json");
		paths.RootTabsPath.ShouldBe(@"C:\profile\Settings\root-tabs.json");
		paths.NotesDirectory.ShouldBe(@"C:\profile\Settings\Notes");
		paths.WebViewDirectory.ShouldBe(@"C:\profile\WebView");
		paths.LogsDirectory.ShouldBe(@"C:\profile\Logs");
		paths.TempDirectory.ShouldBe(@"C:\profile\Temp");
		paths.SessionTempDirectory.ShouldBe(@"C:\profile\Temp\Session");
		paths.RetainedTempDirectory.ShouldBe(@"C:\profile\Temp\Retained");
		paths.WebMonitorSnapshotsDirectory.ShouldBe(@"C:\profile\Temp\Retained\WebMonitoring");
		paths.AgentControlDirectory.ShouldBe(@"C:\profile\Temp\Retained\AgentControl");
		paths.PactSkillsDirectory.ShouldBe(@"C:\profile\Temp\Retained\PactSkills");
		paths.PactMcpSkillPath.ShouldBe(
			@"C:\profile\Temp\Retained\PactSkills\PactMcpSkill.md");
		paths.PactCommonSkillPath.ShouldBe(
			@"C:\profile\Temp\Retained\PactSkills\PactCommonSkill.md");
		paths.AtomicTempDirectory.ShouldBe(@"C:\profile\Temp\Session\atomic");
	}
}
