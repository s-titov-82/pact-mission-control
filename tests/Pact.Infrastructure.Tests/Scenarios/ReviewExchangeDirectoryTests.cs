namespace Pact.Infrastructure.Tests.Scenarios;

public sealed class ReviewExchangeDirectoryTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _projectRoot => _temporaryDirectory.Path;

	[Test]
	public async Task CreateStep_and_publish_task_use_unique_pass_role_paths()
	{
		ReviewExchangeDirectory exchange = new();
		var reviewer = ReviewExchangeDirectory.CreateStep(
			_projectRoot, "1234567890abcdef1234567890abcdef", 1, "reviewer");
		var author = ReviewExchangeDirectory.CreateStep(
			_projectRoot, "1234567890abcdef1234567890abcdef", 1, "author");

		reviewer.StepId.ShouldBe("pass-001-reviewer");
		reviewer.TaskPath.ShouldBe(Path.Combine(_projectRoot, ".pact-reviews", "12345678", "pass-001-reviewer-task.md"));
		reviewer.ResponsePath.ShouldBe(Path.Combine(_projectRoot, ".pact-reviews", "12345678", "pass-001-reviewer-response.md"));
		reviewer.CompletionFooter.ShouldBe("<!-- PACT_RESPONSE_COMPLETE:12345678:pass-001-reviewer -->");
		author.TaskPath.ShouldNotBe(reviewer.TaskPath);
		author.ResponsePath.ShouldNotBe(reviewer.ResponsePath);

		await ReviewExchangeDirectory.PublishTaskAsync(reviewer, "complete task", CancellationToken.None);

		(await File.ReadAllTextAsync(reviewer.TaskPath)).ShouldBe("complete task");
		Directory.EnumerateFiles(
			Path.GetDirectoryName(reviewer.TaskPath)!,
			"*.tmp",
			SearchOption.TopDirectoryOnly).ShouldBeEmpty();
		await Should.ThrowAsync<IOException>(() =>
			ReviewExchangeDirectory.PublishTaskAsync(reviewer, "must not overwrite", CancellationToken.None));
	}

	[Test]
	public async Task WaitForCompletedResponseAsync_waits_for_exact_final_footer_and_strips_it()
	{
		ReviewExchangeDirectory exchange = new();
		var step = ReviewExchangeDirectory.CreateStep(
			_projectRoot, "1234567890abcdef", 1, "reviewer");
		var incompleteNotifications = 0;
		var wait = ReviewExchangeDirectory.WaitForCompletedResponseAsync(
			step,
			watchdogTimeout: TimeSpan.FromSeconds(2),
			pollInterval: TimeSpan.FromMilliseconds(10),
			incompleteResponseDetected: () => incompleteNotifications++,
			cancellationToken: CancellationToken.None);

		await File.WriteAllTextAsync(
			step.ResponsePath,
			$"partial\n{step.CompletionFooter}\nstill writing",
			CancellationToken.None);
		// Exercise the real file-polling boundary: allow at least one incomplete read before
		// replacing the response with its footer-complete form.
		await Task.Delay(50, CancellationToken.None);
		wait.IsCompleted.ShouldBeFalse();

		await File.WriteAllTextAsync(
			step.ResponsePath,
			$"complete response\r\n\r\n{step.CompletionFooter}\r\n",
			CancellationToken.None);

		(await wait).ShouldBe("complete response");
		incompleteNotifications.ShouldBe(1);
	}

	[Test]
	[TestCase(
		"complete response\n  <!-- PACT_RESPONSE_COMPLETE:12345678:pass-001-reviewer -->  ")]
	[TestCase(
		"first section\n<!-- PACT_RESPONSE_COMPLETE:12345678:pass-001-reviewer -->\nsecond section\n<!-- PACT_RESPONSE_COMPLETE:12345678:pass-001-reviewer -->")]
	[TestCase(
		"complete response\n<!-- PACT_RESPONSE_COMPLETE:12345678:pass-999-reviewer -->\n<!-- PACT_RESPONSE_COMPLETE:12345678:pass-001-reviewer -->")]
	[TestCase(
		"complete response\n<!-- PACT_RESPONSE_COMPLETE:12345678:pass-999-reviewer-->\n<!-- PACT_RESPONSE_COMPLETE:12345678:pass-001-reviewer -->")]
	public async Task WaitForCompletedResponseAsync_rejects_padded_or_earlier_transport_footer(
		string incompleteContent)
	{
		ReviewExchangeDirectory exchange = new();
		var step = ReviewExchangeDirectory.CreateStep(
			_projectRoot, "1234567890abcdef", 1, "reviewer");
		await File.WriteAllTextAsync(step.ResponsePath, incompleteContent, CancellationToken.None);
		var wait = ReviewExchangeDirectory.WaitForCompletedResponseAsync(
			step,
			TimeSpan.FromSeconds(2),
			TimeSpan.FromMilliseconds(10),
			incompleteResponseDetected: null,
			CancellationToken.None);

		// The production contract is polling-based; this delay proves the malformed footer is
		// observed without completing the wait before the response is corrected.
		await Task.Delay(50, CancellationToken.None);
		wait.IsCompleted.ShouldBeFalse();

		await File.WriteAllTextAsync(
			step.ResponsePath,
			$"complete response\n{step.CompletionFooter}",
			CancellationToken.None);

		(await wait).ShouldBe("complete response");
	}

	[Test]
	public async Task WaitForCompletedResponseAsync_preserves_leading_Markdown_whitespace()
	{
		ReviewExchangeDirectory exchange = new();
		var step = ReviewExchangeDirectory.CreateStep(
			_projectRoot, "1234567890abcdef", 1, "reviewer");
		await File.WriteAllTextAsync(
			step.ResponsePath,
			$"    indented Markdown\n\nbody with trailing spaces   \n\n{step.CompletionFooter}\n\n",
			CancellationToken.None);

		var response = await ReviewExchangeDirectory.WaitForCompletedResponseAsync(
			step,
			TimeSpan.FromSeconds(2),
			TimeSpan.FromMilliseconds(10),
			incompleteResponseDetected: null,
			CancellationToken.None);

		response.ShouldBe("    indented Markdown\n\nbody with trailing spaces");
		response.Contains("PACT_RESPONSE_COMPLETE", StringComparison.Ordinal).ShouldBeFalse();
	}

	[Test]
	public async Task WaitForCompletedResponseAsync_times_out_when_response_never_completes()
	{
		ReviewExchangeDirectory exchange = new();
		var step = ReviewExchangeDirectory.CreateStep(
			_projectRoot, "1234567890abcdef", 1, "reviewer");

		var exception = await Should.ThrowAsync<ScenarioStepTimeoutException>(() =>
			ReviewExchangeDirectory.WaitForCompletedResponseAsync(
				step,
				watchdogTimeout: TimeSpan.FromMilliseconds(50),
				pollInterval: TimeSpan.FromMilliseconds(10),
				incompleteResponseDetected: null,
				CancellationToken.None));

		exception.Message.Contains(step.ResponsePath, StringComparison.Ordinal).ShouldBeTrue();
	}

	[Test]
	[TestCase("")]
	[TestCase("body without footer")]
	[TestCase("<!-- PACT_RESPONSE_COMPLETE:12345678:pass-999-reviewer -->")]
	[TestCase("body\n<!-- PACT_RESPONSE_COMPLETE:12345678:pass-001-reviewer -->\nmore")]
	public async Task WaitForCompletedResponseAsync_rejects_incomplete_shapes(string initialContent)
	{
		ReviewExchangeDirectory exchange = new();
		var step = ReviewExchangeDirectory.CreateStep(
			_projectRoot, "1234567890abcdef", 1, "reviewer");
		await File.WriteAllTextAsync(
			step.ResponsePath,
			initialContent,
			CancellationToken.None);
		var wait = ReviewExchangeDirectory.WaitForCompletedResponseAsync(
			step,
			TimeSpan.FromSeconds(2),
			TimeSpan.FromMilliseconds(10),
			incompleteResponseDetected: null,
			CancellationToken.None);

		// Let the real poller inspect the incomplete shape before publishing valid content.
		await Task.Delay(50, CancellationToken.None);
		wait.IsCompleted.ShouldBeFalse();
		await File.WriteAllTextAsync(
			step.ResponsePath,
			$"complete\n{step.CompletionFooter}",
			CancellationToken.None);

		(await wait).ShouldBe("complete");
	}

	[Test]
	public async Task WaitForCompletedResponseAsync_retries_a_sharing_violation()
	{
		ReviewExchangeDirectory exchange = new();
		var step = ReviewExchangeDirectory.CreateStep(
			_projectRoot, "1234567890abcdef", 1, "reviewer");
		await using FileStream locked = new(
			step.ResponsePath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None);
		var wait = ReviewExchangeDirectory.WaitForCompletedResponseAsync(
			step,
			TimeSpan.FromSeconds(2),
			TimeSpan.FromMilliseconds(10),
			incompleteResponseDetected: null,
			CancellationToken.None);

		// Keep the sharing violation active across at least one real poll attempt.
		await Task.Delay(50, CancellationToken.None);
		wait.IsCompleted.ShouldBeFalse();
		await locked.DisposeAsync();
		await File.WriteAllTextAsync(
			step.ResponsePath,
			$"complete\n{step.CompletionFooter}",
			CancellationToken.None);

		(await wait).ShouldBe("complete");
	}

	[Test]
	public void CleanupRun_removes_only_exact_run_and_removes_root_after_last_run()
	{
		ReviewExchangeDirectory exchange = new();
		ReviewExchangeDirectory.CreateStep(_projectRoot, "aaaaaaaa11111111", 1, "reviewer");
		ReviewExchangeDirectory.CreateStep(_projectRoot, "bbbbbbbb22222222", 1, "reviewer");

		ReviewExchangeDirectory.CleanupRun(_projectRoot, "aaaaaaaa11111111");

		Directory.Exists(Path.Combine(_projectRoot, ".pact-reviews", "aaaaaaaa")).ShouldBeFalse();
		Directory.Exists(Path.Combine(_projectRoot, ".pact-reviews", "bbbbbbbb")).ShouldBeTrue();

		ReviewExchangeDirectory.CleanupRun(_projectRoot, "bbbbbbbb22222222");

		Directory.Exists(Path.Combine(_projectRoot, ".pact-reviews")).ShouldBeFalse();
	}

	[Test]
	public void CleanupAbandoned_removes_owned_root_but_never_touches_generic_reviews_directory()
	{
		Directory.CreateDirectory(Path.Combine(_projectRoot, ".reviews", "owned-by-someone-else"));
		ReviewExchangeDirectory exchange = new();
		ReviewExchangeDirectory.CreateStep(_projectRoot, "aaaaaaaa11111111", 1, "reviewer");

		ReviewExchangeDirectory.CleanupAbandoned(_projectRoot);

		Directory.Exists(Path.Combine(_projectRoot, ".pact-reviews")).ShouldBeFalse();
		Directory.Exists(Path.Combine(_projectRoot, ".reviews")).ShouldBeTrue();
	}
	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}
}
