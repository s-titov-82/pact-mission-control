using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class WorkspaceRootDetectorTests
{
	[Test]
	public void NormalizeRoot_returns_full_path_without_trailing_separator()
	{
		var root = WorkspaceRootDetector.NormalizeRoot(@"D:\Personal\Pact\");

		root.ShouldBe(@"D:\Personal\Pact");
	}

	[Test]
	public void GetWorkspaceName_uses_last_directory_name()
	{
		var name = WorkspaceRootDetector.GetWorkspaceName(@"D:\Personal\Pact");

		name.ShouldBe("Pact");
	}
}