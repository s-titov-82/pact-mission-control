using Pact.Core.Reviews;
using Pact.Infrastructure.Reviews;

namespace Pact.Infrastructure.Tests.Reviews;

public sealed class ReviewLogWriterTests : IDisposable
{
	private static readonly DateTimeOffset StartedAt = new(2026, 10, 24, 9, 5, 0, TimeSpan.Zero);
	private readonly TemporaryDirectory _project = TemporaryDirectory.Create();

	[Test]
	public void Creating_a_log_path_ignores_the_pact_folder_in_git()
	{
		var path = ReviewLogWriter.CreatePath(_project.Path, StartedAt, "code-review", "7edcec18b3a74c8f");

		path.ShouldBe(Path.Combine(_project.Path, ".pact", "reviews", "2026-10-24-0905-code-review-7edcec18.md"));
		File.ReadAllText(Path.Combine(_project.Path, ".pact", ".gitignore")).ShouldBe("*\n");
	}

	[Test]
	public void A_new_log_keeps_only_the_ten_newest()
	{
		var reviews = Path.Combine(_project.Path, ".pact", "reviews");
		Directory.CreateDirectory(reviews);
		for (var day = 1; day <= 10; day++)
		{
			File.WriteAllText(Path.Combine(reviews, $"2026-10-{day:00}-0900-code-review-{day:00000000}.md"), "# old");
		}

		var path = ReviewLogWriter.CreatePath(_project.Path, StartedAt, "code-review", "7edcec18b3a74c8f");
		File.WriteAllText(path, "# new");

		var kept = Directory.GetFiles(reviews, "*.md").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
		kept.Length.ShouldBe(10);
		kept.ShouldNotContain("2026-10-01-0900-code-review-00000001.md");
		kept.ShouldContain(Path.GetFileName(path));
	}

	[Test]
	public void An_existing_pact_gitignore_is_kept()
	{
		Directory.CreateDirectory(Path.Combine(_project.Path, ".pact"));
		File.WriteAllText(Path.Combine(_project.Path, ".pact", ".gitignore"), "reviews/\n");

		ReviewLogWriter.CreatePath(_project.Path, StartedAt, "code-review", "7edcec18b3a74c8f");

		File.ReadAllText(Path.Combine(_project.Path, ".pact", ".gitignore")).ShouldBe("reviews/\n");
	}

	[Test]
	public async Task Turns_are_appended_in_order_with_header_and_footer()
	{
		using ReviewLogWriter writer = new();
		var path = ReviewLogWriter.CreatePath(_project.Path, StartedAt, "code-review", "7edcec18b3a74c8f");

		await writer.WriteHeaderAsync(
			path,
			new ReviewLogHeader("Code review loop", "main..HEAD", "Claude", "Claude", "Codex reviewer", "Codex", 5, StartedAt),
			CancellationToken.None);
		await writer.AppendTurnAsync(path, ReviewRole.Reviewer, 1, StartedAt.AddMinutes(3), "1. finding", CancellationToken.None);
		await writer.AppendTurnAsync(path, ReviewRole.Author, 1, StartedAt.AddMinutes(9), "fixed it", CancellationToken.None);
		await writer.WriteFooterAsync(path, ReviewState.Completed, "The reviewer approved the work.", StartedAt.AddMinutes(12), CancellationToken.None);

		var log = await File.ReadAllTextAsync(path);
		log.ShouldContain("Code review loop");
		log.ShouldContain("main..HEAD");
		log.ShouldContain("Codex reviewer (Codex)");
		log.ShouldContain("Iteration limit: 5");
		var finding = log.IndexOf("1. finding", StringComparison.Ordinal);
		var answer = log.IndexOf("fixed it", StringComparison.Ordinal);
		var footer = log.IndexOf("Completed", StringComparison.Ordinal);
		finding.ShouldBeGreaterThan(0);
		answer.ShouldBeGreaterThan(finding);
		footer.ShouldBeGreaterThan(answer);
		log.ShouldContain("The reviewer approved the work.");
	}

	[Test]
	public async Task Writes_from_several_threads_all_land()
	{
		using ReviewLogWriter writer = new();
		var path = ReviewLogWriter.CreatePath(_project.Path, StartedAt, "code-review", "7edcec18b3a74c8f");
		await File.WriteAllTextAsync(path, string.Empty);

		await Task.WhenAll(Enumerable.Range(1, 40).Select(index => Task.Run(() => writer.AppendTurnAsync(
			path,
			ReviewRole.Author,
			index,
			StartedAt,
			$"answer {index}.",
			CancellationToken.None))));

		var log = await File.ReadAllTextAsync(path);
		foreach (var index in Enumerable.Range(1, 40))
		{
			log.ShouldContain($"answer {index}.");
		}
	}

	public void Dispose() => _project.Dispose();
}
