namespace Pact.Infrastructure.Tests.Git;

public sealed class ProjectFileChangeSignalTests
{
	[TestCase(".git/HEAD", true)]
	[TestCase(".git/index", true)]
	[TestCase(".git/objects/ab/cdef", false)]
	[TestCase(".git/refs/heads/main", false)]
	[TestCase(".git\\index.lock", false)]
	[TestCase("src/a.cs", true)]
	[TestCase(".pact/reviews/x.md", true)]
	[TestCase(".gitignore", true)]
	public void Relevance_ignores_git_internals_except_head_and_index(string path, bool relevant)
	{
		ProjectFileChangeSignal.IsRelevant(path).ShouldBe(relevant);
	}
}
