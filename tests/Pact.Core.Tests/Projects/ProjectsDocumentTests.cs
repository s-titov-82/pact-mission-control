using Pact.Core.Projects;

namespace Pact.Core.Tests.Projects;

public sealed class ProjectsDocumentTests
{
	[Test]
	public void CreateDefault_has_schema_version_and_no_projects()
	{
		var document = ProjectsDocument.CreateDefault();

		document.SchemaVersion.ShouldBe(1);
		document.Projects.ShouldBeEmpty();
	}
}