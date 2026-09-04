using Pact.Core.AgentControl;

namespace Pact.Core.Tests.AgentControl;

public sealed class ProjectNotesRevisionTests
{
	[Test]
	public void Revision_is_stable_for_the_exact_same_text()
	{
		ProjectNotesRevision.Compute("a\r\nb")
			.ShouldBe(ProjectNotesRevision.Compute("a\r\nb"));
	}

	[Test]
	public void Revision_changes_when_even_line_endings_change()
	{
		ProjectNotesRevision.Compute("a\r\nb")
			.ShouldNotBe(ProjectNotesRevision.Compute("a\nb"));
	}
}
