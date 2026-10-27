namespace Pact.Infrastructure.Tests.Documents;

public sealed class ReviewLogFilesTests
{
	[Test]
	public void Review_logs_are_listed_newest_first_and_ignore_subfolders_and_other_files()
	{
		using var root = TemporaryDirectory.Create();
		var folder = Path.Combine(root.Path, ".pact", "reviews");
		Directory.CreateDirectory(Path.Combine(folder, "old"));
		File.WriteAllText(Path.Combine(folder, "2026-10-24-1400-code-review-a.md"), "a");
		File.WriteAllText(Path.Combine(folder, "2026-10-25-0900-plan-review-b.md"), "b");
		File.WriteAllText(Path.Combine(folder, "notes.txt"), "c");
		File.WriteAllText(Path.Combine(folder, "old", "2026-10-26-0000-code-review-c.md"), "d");

		ReviewLogFiles.List(root.Path).ShouldBe(
		[
			".pact/reviews/2026-10-25-0900-plan-review-b.md",
			".pact/reviews/2026-10-24-1400-code-review-a.md"
		]);
	}

	[Test]
	public void Deleting_a_log_removes_its_file()
	{
		using var root = TemporaryDirectory.Create();
		var folder = Path.Combine(root.Path, ".pact", "reviews");
		Directory.CreateDirectory(folder);
		var log = Path.Combine(folder, "2026-10-24-1400-code-review-a.md");
		File.WriteAllText(log, "a");

		ReviewLogFiles.Delete(root.Path, ".pact/reviews/2026-10-24-1400-code-review-a.md");

		File.Exists(log).ShouldBeFalse();
	}

	[TestCase("docs/plan.md")]
	[TestCase(".pact/reviews/../../plan.md")]
	[TestCase(".pact/reviews/old/2026-10-26-0000-code-review-c.md")]
	public void Deleting_refuses_anything_but_a_log_in_the_folder(string relativePath)
	{
		using var root = TemporaryDirectory.Create();

		Should.Throw<ArgumentException>(() => ReviewLogFiles.Delete(root.Path, relativePath));
	}

	[Test]
	public void A_missing_reviews_folder_lists_nothing()
	{
		using var root = TemporaryDirectory.Create();

		ReviewLogFiles.List(root.Path).ShouldBeEmpty();
	}
}
