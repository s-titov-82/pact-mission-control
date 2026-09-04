namespace Pact.Infrastructure.Tests.Documents;

public sealed class MarkdownTreeNodeTests
{
	[Test]
	public void Build_nests_folders_and_orders_folders_before_files()
	{
		ProjectMarkdownFileEntry[] entries = [
			Entry("README.md"),
			Entry("AGENTS.md"),
			Entry("src/Service/details.md"),
			Entry("src/Service/README.md"),
			Entry("tools/notes.md")
		];

		var tree = MarkdownTreeNode.Build(entries);

		tree.Select(node => node.Name).ShouldBe(["src", "tools", "AGENTS.md", "README.md"]);
		tree[0].IsFolder.ShouldBeTrue();
		tree[0].RelativePath.ShouldBe("src");
		tree[0].Children.Select(node => node.Name).ShouldBe(["Service"]);
		tree[0].Children[0].Children.Select(node => node.Name)
			.ShouldBe(["details.md", "README.md"]);
		tree[3].IsFolder.ShouldBeFalse();
		tree[3].RelativePath.ShouldBe("README.md");
		tree[3].FullPath.ShouldBe("C:\\repo\\README.md");
	}

	[Test]
	public void Build_trims_the_group_prefix_but_keeps_project_relative_paths()
	{
		ProjectMarkdownFileEntry[] entries = [
			Entry("docs/README.md"),
			Entry("docs/superpowers/specs/design.md")
		];

		var tree = MarkdownTreeNode.Build(entries, "docs/");

		tree.Select(node => node.Name).ShouldBe(["superpowers", "README.md"]);
		tree[0].RelativePath.ShouldBe("docs/superpowers");
		tree[0].Children[0].RelativePath.ShouldBe("docs/superpowers/specs");
		tree[0].Children[0].Children[0].RelativePath
			.ShouldBe("docs/superpowers/specs/design.md");
		tree[1].RelativePath.ShouldBe("docs/README.md");
	}

	[Test]
	public void Build_creates_no_branches_without_markdown()
	{
		var tree = MarkdownTreeNode.Build([]);

		tree.ShouldBeEmpty();
	}

	[Test]
	public void Build_rejects_a_null_trim_prefix()
	{
		Should.Throw<ArgumentNullException>(
			() => MarkdownTreeNode.Build([], null!));
	}

	private static ProjectMarkdownFileEntry Entry(string relativePath) =>
		new(
			Path.Combine("C:\\repo", relativePath.Replace('/', Path.DirectorySeparatorChar)),
			relativePath);
}
