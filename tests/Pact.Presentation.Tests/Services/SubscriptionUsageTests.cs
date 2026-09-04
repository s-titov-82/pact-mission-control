using System.Diagnostics;
using System.Text.Json;
using Pact.Core.Agents;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class SubscriptionUsageTests : IDisposable
{
	private readonly List<TemporaryDirectory> _temporaryDirectories = [];

	[Test]
	public async Task ClaudeUsageCommandRunner_kills_process_tree_on_timeout()
	{
		FakeClaudeUsageProcess process = new();
		PowerShellClaudeUsageCommandRunner runner = new(
			TimeSpan.FromMilliseconds(10),
			new FakeClaudeUsageProcessFactory(process));

		var result = await runner.RunAsync("claude", CancellationToken.None);

		result.Succeeded.ShouldBeFalse();
		result.FailureMessage.ShouldBe("Claude usage command timed out.");
		process.KilledEntireProcessTree.ShouldBeTrue();
		process.Disposed.ShouldBeTrue();
	}

	[Test]
	public async Task ClaudeUsageCommandRunner_preserves_partial_output_after_timeout_kills_process_tree()
	{
		FakeClaudeUsageProcess process = new(
			standardOutputAfterKill: "partial stdout",
			standardErrorAfterKill: "partial stderr");
		PowerShellClaudeUsageCommandRunner runner = new(
			TimeSpan.FromMilliseconds(10),
			new FakeClaudeUsageProcessFactory(process));

		var result = await runner.RunAsync("claude", CancellationToken.None);

		result.Succeeded.ShouldBeFalse();
		result.FailureMessage.ShouldBe("Claude usage command timed out.");
		result.StandardOutput.ShouldBe("partial stdout");
		result.StandardError.ShouldBe("partial stderr");
		process.KilledEntireProcessTree.ShouldBeTrue();
	}

	[Test]
	public async Task ClaudeUsageCommandRunner_kills_process_tree_and_propagates_caller_cancellation()
	{
		FakeClaudeUsageProcess process = new();
		PowerShellClaudeUsageCommandRunner runner = new(
			TimeSpan.FromMinutes(1),
			new FakeClaudeUsageProcessFactory(process));
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Should.ThrowAsync<OperationCanceledException>(
			() => runner.RunAsync("claude", cancellation.Token));

		process.KilledEntireProcessTree.ShouldBeTrue();
		process.Disposed.ShouldBeTrue();
	}

	[Test]
	public void CreatePendingRows_includes_codex_and_all_claude_profiles()
	{
		AgentProfileRecord[] profiles =
		[
			new("pwsh", AgentKind.Pwsh, "Empty Terminal", "pwsh", null, "pwsh"),
			new("codex", AgentKind.Codex, "Codex session", "codex", "codex resume", "pwsh"),
			new("claude", AgentKind.Claude, "Claude", "claude", "claude --resume", "pwsh"),
			new("claude-personal", AgentKind.Claude, "Claude personal", "claude-personal", "claude-personal --resume", "pwsh")
		];

		var rows = SubscriptionUsageRows.CreatePendingRows(profiles).ToArray();

		rows.Select(row => row.ProfileId).ToArray().ShouldBe(["codex", "claude", "claude-personal"]);
		rows.ShouldAllBe(row => row.StatusText == "Updating...");
	}

	[Test]
	public void GetNextRefreshInterval_uses_short_interval_when_any_limit_has_less_than_ten_percent_left()
	{
		SubscriptionUsageRow[] rows =
		[
			CreateRow("codex", fiveHourUsedPercent: 91, weeklyUsedPercent: 50),
			CreateRow("claude", fiveHourUsedPercent: 20, weeklyUsedPercent: 20)
		];

		var interval = SubscriptionUsageRefreshPolicy.GetNextRefreshInterval(rows);

		interval.ShouldBe(TimeSpan.FromSeconds(30));
	}

	[Test]
	public void GetNextRefreshInterval_uses_normal_interval_when_limits_are_not_low()
	{
		SubscriptionUsageRow[] rows =
		[
			CreateRow("codex", fiveHourUsedPercent: 90, weeklyUsedPercent: 50),
			CreateRow("claude", fiveHourUsedPercent: 20, weeklyUsedPercent: 20)
		];

		var interval = SubscriptionUsageRefreshPolicy.GetNextRefreshInterval(rows);

		interval.ShouldBe(TimeSpan.FromMinutes(2));
	}

	[Test]
	public void Apply_displays_reset_time_with_remaining_percent()
	{
		AgentProfileRecord profile = new("codex", AgentKind.Codex, "Codex", "codex", null, "pwsh");
		DateTimeOffset now = new(2026, 7, 8, 10, 0, 0, TimeSpan.Zero);
		DateTimeOffset resetAt = new(2026, 7, 8, 11, 30, 0, TimeSpan.Zero);
		SubscriptionUsageSnapshot snapshot = new(
			profile.Id,
			profile.DisplayName,
			profile.Kind,
			SubscriptionUsageState.Ready,
			new SubscriptionLimitSnapshot(48, resetAt),
			null,
			"Updated",
			RawResponseText: null,
			ErrorDetailsText: null,
			now);

		var row = new SubscriptionUsageRow(profile).Apply(snapshot, now);

		row.FiveHourText.ShouldBe($"52%{Environment.NewLine}{resetAt.ToLocalTime():HH:mm}");
	}

	[Test]
	[TestCase(27, 15, "1d 3h")]
	[TestCase(15, 27, "15h 27m")]
	public void Apply_displays_weekly_reset_as_remaining_time(int hours, int minutes, string expected)
	{
		AgentProfileRecord profile = new("codex", AgentKind.Codex, "Codex", "codex", null, "pwsh");
		DateTimeOffset now = new(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);
		SubscriptionUsageSnapshot snapshot = new(
			profile.Id, profile.DisplayName, profile.Kind, SubscriptionUsageState.Ready,
			FiveHour: null,
			Weekly: new SubscriptionLimitSnapshot(40, now.AddHours(hours).AddMinutes(minutes)),
			"Updated", null, null, now);

		var row = new SubscriptionUsageRow(profile).Apply(snapshot, now);

		row.WeeklyText.ShouldBe($"60%{Environment.NewLine}{expected}");
		row.FiveHourText.ShouldBe("--");
	}

	[Test]
	public void Apply_treats_passed_reset_time_as_reset_window()
	{
		AgentProfileRecord profile = new("codex", AgentKind.Codex, "Codex", "codex", null, "pwsh");
		DateTimeOffset now = new(2026, 7, 8, 10, 0, 0, TimeSpan.Zero);
		var resetAt = now.AddMinutes(-1);
		SubscriptionUsageSnapshot snapshot = new(
			profile.Id,
			profile.DisplayName,
			profile.Kind,
			SubscriptionUsageState.Ready,
			new SubscriptionLimitSnapshot(97, resetAt),
			null,
			"Updated",
			RawResponseText: "details",
			ErrorDetailsText: null,
			now);

		var row = new SubscriptionUsageRow(profile).Apply(snapshot, now);

		row.FiveHourText.ShouldBe($"100%{Environment.NewLine}reset {resetAt.ToLocalTime():HH:mm}");
		row.IsNearLimit.ShouldBeFalse();
		row.HasRawResponse.ShouldBeTrue();
		row.RawResponseText.ShouldBe("details");
	}

	[Test]
	public void Apply_failure_preserves_previous_limits_replaces_raw_response_and_sets_error()
	{
		AgentProfileRecord profile = new("claude", AgentKind.Claude, "Claude", "claude", null, "pwsh");
		DateTimeOffset now = new(2026, 7, 8, 10, 0, 0, TimeSpan.Zero);
		var row = new SubscriptionUsageRow(profile).Apply(
			new SubscriptionUsageSnapshot(
				profile.Id,
				profile.DisplayName,
				profile.Kind,
				SubscriptionUsageState.Ready,
				new SubscriptionLimitSnapshot(30, null),
				new SubscriptionLimitSnapshot(40, null),
				"Updated",
				RawResponseText: "last raw response",
				ErrorDetailsText: null,
				now),
			now);

		row.Apply(
			new SubscriptionUsageSnapshot(
				profile.Id,
				profile.DisplayName,
				profile.Kind,
				SubscriptionUsageState.Failed,
				FiveHour: null,
				Weekly: null,
				"claude usage failed",
				RawResponseText: "latest failed response",
				ErrorDetailsText: "command timed out",
				now.AddMinutes(1)),
			now.AddMinutes(1));

		row.State.ShouldBe(SubscriptionUsageState.Failed);
		row.FiveHourText.ShouldBe("70%");
		row.WeeklyText.ShouldBe("60%");
		row.RawResponseText.ShouldBe("latest failed response");
		row.HasRawResponse.ShouldBeTrue();
		row.HasErrorDetails.ShouldBeTrue();
		row.ErrorDetailsText.ShouldBe("command timed out");
	}

	[Test]
	public void Apply_failure_without_raw_response_hides_raw_details_and_uses_status_as_error_details()
	{
		AgentProfileRecord profile = new("claude", AgentKind.Claude, "Claude", "claude", null, "pwsh");
		var row = new SubscriptionUsageRow(profile);

		row.Apply(new SubscriptionUsageSnapshot(
			profile.Id,
			profile.DisplayName,
			profile.Kind,
			SubscriptionUsageState.Unavailable,
			FiveHour: null,
			Weekly: null,
			"No Claude usage data",
			RawResponseText: "   ",
			ErrorDetailsText: null,
			DateTimeOffset.UtcNow));

		row.FiveHourText.ShouldBe("n/a");
		row.HasRawResponse.ShouldBeFalse();
		row.RawResponseText.ShouldBeNull();
		row.HasErrorDetails.ShouldBeTrue();
		row.ErrorDetailsText.ShouldBe("No Claude usage data");
	}

	[Test]
	public async Task LocalReader_reads_claude_statusline_rate_limits()
	{
		var root = CreateTempDirectory();
		var claudePath = Path.Combine(root, ".claude", "statusline-input.json");
		Directory.CreateDirectory(Path.GetDirectoryName(claudePath)!);
		await File.WriteAllTextAsync(
			claudePath,
								 /*lang=json,strict*/
								 """
            {
              "rate_limits": {
                "five_hour": { "used_percentage": 34, "resets_at": 1780000000 },
                "seven_day": { "used_percentage": 70, "resets_at": 1780100000 }
              }
            }
            """);
		DateTimeOffset sourceUpdatedAt = new(2026, 7, 8, 8, 15, 0, TimeSpan.Zero);
		File.SetLastWriteTimeUtc(claudePath, sourceUpdatedAt.UtcDateTime);
		StubClaudeUsageCommandRunner commandRunner = new();
		LocalSubscriptionUsageReader reader = new(
			codexSessionsDirectory: Path.Combine(root, "missing-codex"),
			claudeStatuslineInputPath: claudePath,
			claudeUsageCommandRunner: commandRunner);
		AgentProfileRecord profile = new("claude", AgentKind.Claude, "Claude", " ", "claude --resume", "pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.State.ShouldBe(SubscriptionUsageState.Ready);
		(snapshot.FiveHour?.RemainingPercent).ShouldBe(66);
		(snapshot.Weekly?.RemainingPercent).ShouldBe(30);
		snapshot.UpdatedAt.ShouldBe(sourceUpdatedAt);
		snapshot.RawResponseText!.Contains("\"rate_limits\"", StringComparison.Ordinal).ShouldBeTrue();
		snapshot.ErrorDetailsText.ShouldBeNull();
		commandRunner.Commands.ShouldBeEmpty();
	}

	[Test]
	public async Task Claude_statusline_read_handle_allows_write_and_delete_sharing()
	{
		var root = CreateTempDirectory();
		var path = await WriteClaudeStatuslineAsync(root, usedPercent: 10);

		await using var readStream = LocalSubscriptionUsageReader.OpenClaudeStatuslineInput(path);
		await using FileStream writeStream = new(
			path,
			FileMode.Open,
			FileAccess.Write,
			FileShare.ReadWrite | FileShare.Delete);

		File.Delete(path);

		File.Exists(path).ShouldBeFalse();
	}

	[Test]
	public async Task LocalReader_reads_profile_specific_claude_statusline_rate_limits()
	{
		var root = CreateTempDirectory();
		var defaultClaudePath = Path.Combine(root, ".claude", "statusline-input.json");
		var personalClaudePath = Path.Combine(root, ".claude-personal", "statusline-input.json");
		Directory.CreateDirectory(Path.GetDirectoryName(defaultClaudePath)!);
		Directory.CreateDirectory(Path.GetDirectoryName(personalClaudePath)!);
		await File.WriteAllTextAsync(
			defaultClaudePath,
								 /*lang=json,strict*/
								 """
            {
              "rate_limits": {
                "five_hour": { "used_percentage": 10 },
                "seven_day": { "used_percentage": 20 }
              }
            }
            """);
		await File.WriteAllTextAsync(
			personalClaudePath,
								 /*lang=json,strict*/
								 """
            {
              "rate_limits": {
                "five_hour": { "used_percentage": 80 },
                "seven_day": { "used_percentage": 90 }
              }
            }
            """);
		LocalSubscriptionUsageReader reader = new(
			codexSessionsDirectory: Path.Combine(root, "missing-codex"),
			claudeStatuslineInputPath: defaultClaudePath,
			claudeUsageCommandRunner: new StubClaudeUsageCommandRunner());
		AgentProfileRecord profile = new(
			"claude-personal",
			AgentKind.Claude,
			"Claude personal",
			" ",
			"claude-personal --resume",
			"pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		(snapshot.FiveHour?.RemainingPercent).ShouldBe(20);
		(snapshot.Weekly?.RemainingPercent).ShouldBe(10);
		snapshot.RawResponseText!.Contains("\"rate_limits\"", StringComparison.Ordinal).ShouldBeTrue();
		snapshot.ErrorDetailsText.ShouldBeNull();
	}

	[Test]
	public async Task LocalReader_exposes_malformed_statusline_json_as_raw_and_error()
	{
		var root = CreateTempDirectory();
		var path = Path.Combine(root, ".claude", "statusline-input.json");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(path, "{ malformed json");
		LocalSubscriptionUsageReader reader = new(
			Path.Combine(root, "missing-codex"),
			path,
			new StubClaudeUsageCommandRunner());
		AgentProfileRecord profile = new("claude", AgentKind.Claude, "Claude", " ", null, "pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.State.ShouldBe(SubscriptionUsageState.Failed);
		snapshot.RawResponseText.ShouldBe("{ malformed json");
		snapshot.ErrorDetailsText.ShouldNotBeNull();
	}

	[Test]
	public async Task LocalReader_exposes_statusline_without_rate_limits_as_raw_and_error()
	{
		var root = CreateTempDirectory();
		var path = Path.Combine(root, ".claude", "statusline-input.json");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(path, /*lang=json,strict*/ "{ \"model\": \"claude\" }");
		LocalSubscriptionUsageReader reader = new(
			Path.Combine(root, "missing-codex"),
			path,
			new StubClaudeUsageCommandRunner());
		AgentProfileRecord profile = new("claude", AgentKind.Claude, "Claude", " ", null, "pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.State.ShouldBe(SubscriptionUsageState.Unavailable);
		snapshot.RawResponseText!.Contains("\"model\"", StringComparison.Ordinal).ShouldBeTrue();
		snapshot.ErrorDetailsText.ShouldBe("No Claude rate limits");
	}

	[Test]
	public async Task LocalReader_does_not_reuse_default_claude_statusline_for_profile_specific_claude()
	{
		var root = CreateTempDirectory();
		var defaultClaudePath = Path.Combine(root, ".claude", "statusline-input.json");
		Directory.CreateDirectory(Path.GetDirectoryName(defaultClaudePath)!);
		await File.WriteAllTextAsync(
			defaultClaudePath,
								 /*lang=json,strict*/
								 """
            {
              "rate_limits": {
                "five_hour": { "used_percentage": 10 },
                "seven_day": { "used_percentage": 20 }
              }
            }
            """);
		LocalSubscriptionUsageReader reader = new(
			codexSessionsDirectory: Path.Combine(root, "missing-codex"),
			claudeStatuslineInputPath: defaultClaudePath,
			claudeUsageCommandRunner: new StubClaudeUsageCommandRunner());
		AgentProfileRecord profile = new(
			"claude-personal",
			AgentKind.Claude,
			"Claude personal",
			" ",
			"claude-personal --resume",
			"pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.State.ShouldBe(SubscriptionUsageState.Unavailable);
		snapshot.StatusText.ShouldBe("No Claude usage data");
	}

	[Test]
	public async Task LocalReader_reads_claude_usage_command_output()
	{
		var root = CreateTempDirectory();
		DateTimeOffset updatedAt = new(2026, 7, 8, 10, 0, 0, TimeSpan.Zero);
		StubClaudeUsageCommandRunner commandRunner = new(new ClaudeUsageCommandResult(
			Succeeded: true,
			StandardOutput: """
            You are currently using your subscription to power your Claude Code usage

            Current session: 2% used · resets Jul 8, 5:59pm (Europe/Moscow)
            Current week (all models): 75% used · resets Jul 10, 5:59am (Europe/Moscow)
            Current week (Fable): 67% used · resets Jul 10, 5:59am (Europe/Moscow)
            """,
			StandardError: string.Empty,
			FailureMessage: null,
			updatedAt));
		LocalSubscriptionUsageReader reader = new(
			codexSessionsDirectory: Path.Combine(root, "missing-codex"),
			claudeStatuslineInputPath: Path.Combine(root, ".claude", "statusline-input.json"),
			claudeUsageCommandRunner: commandRunner);
		AgentProfileRecord profile = new("claude", AgentKind.Claude, "Claude", "claude", "claude --resume", "pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.State.ShouldBe(SubscriptionUsageState.Ready);
		(snapshot.FiveHour?.RemainingPercent).ShouldBe(98);
		(snapshot.Weekly?.RemainingPercent).ShouldBe(25);
		(snapshot.FableWeekly?.RemainingPercent).ShouldBe(33);
		(snapshot.FiveHour?.ResetsAt).ShouldNotBeNull();
		(snapshot.Weekly?.ResetsAt).ShouldNotBeNull();
		snapshot.RawResponseText!.Contains("subscription", StringComparison.Ordinal).ShouldBeTrue();
		commandRunner.Commands.ShouldBe(["claude"]);

		var row = new SubscriptionUsageRow(profile).Apply(snapshot, updatedAt);
		row.WeeklyText.StartsWith("25% [F: 33%]", StringComparison.Ordinal).ShouldBeTrue();
	}

	[Test]
	public async Task LocalReader_reads_profile_specific_claude_usage_command_output()
	{
		var root = CreateTempDirectory();
		StubClaudeUsageCommandRunner commandRunner = new(new ClaudeUsageCommandResult(
			Succeeded: true,
			StandardOutput: """
            You are currently using your subscription to power your Claude Code usage

            Current session: 0% used
            Current week (all models): 5% used · resets Jul 12, 6am (Europe/Moscow)
            """,
			StandardError: string.Empty,
			FailureMessage: null,
			new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero)));
		LocalSubscriptionUsageReader reader = new(
			codexSessionsDirectory: Path.Combine(root, "missing-codex"),
			claudeStatuslineInputPath: Path.Combine(root, ".claude", "statusline-input.json"),
			claudeUsageCommandRunner: commandRunner);
		AgentProfileRecord profile = new(
			"claude-personal",
			AgentKind.Claude,
			"Claude personal",
			"claude-personal",
			"claude-personal --resume",
			"pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.State.ShouldBe(SubscriptionUsageState.Ready);
		(snapshot.FiveHour?.RemainingPercent).ShouldBe(100);
		(snapshot.FiveHour?.ResetsAt).ShouldBeNull();
		(snapshot.Weekly?.RemainingPercent).ShouldBe(95);
		snapshot.FableWeekly.ShouldBeNull();
		(snapshot.Weekly?.ResetsAt).ShouldNotBeNull();
		snapshot.RawResponseText!.Contains("Current week (all models)", StringComparison.Ordinal).ShouldBeTrue();
		commandRunner.Commands.ShouldBe(["claude-personal"]);

		var row = new SubscriptionUsageRow(profile).Apply(
			snapshot,
			new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero));
		row.WeeklyText.StartsWith("95%", StringComparison.Ordinal).ShouldBeTrue();
		row.WeeklyText.Contains("F:", StringComparison.Ordinal).ShouldBeFalse();
	}

	[Test]
	public async Task LocalReader_returns_failed_snapshot_with_raw_and_error_when_claude_command_exits_nonzero()
	{
		var root = CreateTempDirectory();
		var fallbackPath = await WriteClaudeStatuslineAsync(root, usedPercent: 10);
		StubClaudeUsageCommandRunner runner = new(new ClaudeUsageCommandResult(
			Succeeded: false,
			StandardOutput: "partial stdout",
			StandardError: "authentication failed",
			FailureMessage: "Claude usage command exited with code 1.",
			DateTimeOffset.UtcNow));
		LocalSubscriptionUsageReader reader = new(Path.Combine(root, "missing-codex"), fallbackPath, runner);
		AgentProfileRecord profile = new("claude", AgentKind.Claude, "Claude", "claude", null, "pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.State.ShouldBe(SubscriptionUsageState.Failed);
		snapshot.RawResponseText.ShouldBe("partial stdout" + Environment.NewLine + Environment.NewLine
			+ "[stderr]" + Environment.NewLine + "authentication failed");
		snapshot.ErrorDetailsText.ShouldBe("Claude usage command exited with code 1.");
		snapshot.FiveHour.ShouldBeNull();
	}

	[Test]
	public async Task LocalReader_returns_failed_snapshot_with_stdout_when_claude_command_output_is_unparseable()
	{
		var root = CreateTempDirectory();
		var fallbackPath = await WriteClaudeStatuslineAsync(root, usedPercent: 10);
		StubClaudeUsageCommandRunner runner = new(new ClaudeUsageCommandResult(
			Succeeded: true,
			StandardOutput: "unexpected usage screen",
			StandardError: string.Empty,
			FailureMessage: null,
			DateTimeOffset.UtcNow));
		LocalSubscriptionUsageReader reader = new(Path.Combine(root, "missing-codex"), fallbackPath, runner);
		AgentProfileRecord profile = new("claude", AgentKind.Claude, "Claude", "claude", null, "pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.State.ShouldBe(SubscriptionUsageState.Failed);
		snapshot.RawResponseText.ShouldBe("unexpected usage screen");
		snapshot.ErrorDetailsText.ShouldBe("Unable to parse Claude usage response.");
		snapshot.FiveHour.ShouldBeNull();
	}

	[Test]
	public async Task LocalReader_does_not_show_raw_response_for_timeout_without_process_output()
	{
		var root = CreateTempDirectory();
		StubClaudeUsageCommandRunner runner = new(new ClaudeUsageCommandResult(
			Succeeded: false,
			StandardOutput: string.Empty,
			StandardError: string.Empty,
			FailureMessage: "Claude usage command timed out.",
			DateTimeOffset.UtcNow));
		LocalSubscriptionUsageReader reader = new(
			Path.Combine(root, "missing-codex"),
			Path.Combine(root, "unused-statusline.json"),
			runner);
		AgentProfileRecord profile = new("claude", AgentKind.Claude, "Claude", "claude", null, "pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.State.ShouldBe(SubscriptionUsageState.Failed);
		snapshot.RawResponseText.ShouldBeNull();
		snapshot.ErrorDetailsText.ShouldBe("Claude usage command timed out.");
	}

	[Test]
	public async Task LocalReader_reads_latest_codex_token_count_rate_limits()
	{
		var root = CreateTempDirectory();
		var sessionDirectory = Path.Combine(root, "sessions", "2026", "07", "08");
		Directory.CreateDirectory(sessionDirectory);
		await File.WriteAllTextAsync(
			Path.Combine(sessionDirectory, "rollout.jsonl"),
			JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "other" } })
			+ Environment.NewLine
			+ /*lang=json,strict*/ """
            {"type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":48.0,"window_minutes":300,"resets_at":1780000000},"secondary":{"used_percent":97.0,"window_minutes":10080,"resets_at":1780100000}}}}
            """);
		LocalSubscriptionUsageReader reader = new(
			codexSessionsDirectory: Path.Combine(root, "sessions"),
			claudeStatuslineInputPath: Path.Combine(root, "missing-claude.json"));
		AgentProfileRecord profile = new("codex", AgentKind.Codex, "Codex", "codex", "codex resume", "pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.State.ShouldBe(SubscriptionUsageState.Ready);
		(snapshot.FiveHour?.RemainingPercent).ShouldBe(52);
		(snapshot.Weekly?.RemainingPercent).ShouldBe(3);
	}

	[Test]
	public async Task LocalReader_classifies_single_codex_weekly_window_by_duration()
	{
		var root = CreateTempDirectory();
		var sessionDirectory = Path.Combine(root, "sessions", "2026", "07", "13");
		Directory.CreateDirectory(sessionDirectory);
		await File.WriteAllTextAsync(
			Path.Combine(sessionDirectory, "rollout.jsonl"),
								 /*lang=json,strict*/
								 """
            {"type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":43.0,"window_minutes":10080,"resets_at":1784507451},"secondary":null}}}
            """);
		LocalSubscriptionUsageReader reader = new(
			Path.Combine(root, "sessions"), Path.Combine(root, "missing-claude.json"));
		AgentProfileRecord profile = new("codex", AgentKind.Codex, "Codex", "codex", "codex resume", "pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.FiveHour.ShouldBeNull();
		(snapshot.Weekly?.RemainingPercent).ShouldBe(57);
	}

	[Test]
	public async Task LocalReader_keeps_valid_codex_rate_limits_when_later_lines_contain_null_json()
	{
		var root = CreateTempDirectory();
		var sessionDirectory = Path.Combine(root, "sessions", "2026", "07", "13");
		Directory.CreateDirectory(sessionDirectory);
		await File.WriteAllTextAsync(
			Path.Combine(sessionDirectory, "rollout.jsonl"),
			"""
            {"type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":48.0,"resets_at":1780000000},"secondary":{"used_percent":97.0,"resets_at":1780100000}}}}
            {"type":"event_msg","payload":null}
            {"type":"event_msg","payload":{"type":"token_count","rate_limits":null}}
            {"type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":null,"secondary":null}}}
            """);
		LocalSubscriptionUsageReader reader = new(
			codexSessionsDirectory: Path.Combine(root, "sessions"),
			claudeStatuslineInputPath: Path.Combine(root, "missing-claude.json"));
		AgentProfileRecord profile = new("codex", AgentKind.Codex, "Codex", "codex", "codex resume", "pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.State.ShouldBe(SubscriptionUsageState.Ready);
		(snapshot.FiveHour?.RemainingPercent).ShouldBe(52);
		(snapshot.Weekly?.RemainingPercent).ShouldBe(3);
	}

	[Test]
	public async Task LocalReader_returns_failed_snapshot_when_unexpected_exception_is_thrown()
	{
		var root = CreateTempDirectory();
		LocalSubscriptionUsageReader reader = new(
			codexSessionsDirectory: Path.Combine(root, "missing-codex"),
			claudeStatuslineInputPath: Path.Combine(root, "missing-claude.json"),
			claudeUsageCommandRunner: new ThrowingClaudeUsageCommandRunner(
				new InvalidOperationException("unexpected boom")));
		AgentProfileRecord profile = new("claude", AgentKind.Claude, "Claude", "claude", null, "pwsh");

		var snapshot = await reader.ReadAsync(profile, CancellationToken.None);

		snapshot.State.ShouldBe(SubscriptionUsageState.Failed);
		snapshot.ErrorDetailsText.ShouldBe("unexpected boom");
	}

	[Test]
	public async Task LocalReader_still_propagates_cancellation()
	{
		var root = CreateTempDirectory();
		LocalSubscriptionUsageReader reader = new(
			codexSessionsDirectory: Path.Combine(root, "missing-codex"),
			claudeStatuslineInputPath: Path.Combine(root, "missing-claude.json"),
			claudeUsageCommandRunner: new ThrowingClaudeUsageCommandRunner(
				new OperationCanceledException()));
		AgentProfileRecord profile = new("claude", AgentKind.Claude, "Claude", "claude", null, "pwsh");

		await Should.ThrowAsync<OperationCanceledException>(
			() => reader.ReadAsync(profile, CancellationToken.None));
	}

	[Test]
	public void Apply_failure_without_previous_data_shows_dash_in_limit_cells()
	{
		AgentProfileRecord profile = new("claude", AgentKind.Claude, "Claude", "claude", null, "pwsh");
		SubscriptionUsageRow row = new(profile);

		row.Apply(new SubscriptionUsageSnapshot(
			profile.Id,
			profile.DisplayName,
			profile.Kind,
			SubscriptionUsageState.Failed,
			FiveHour: null,
			Weekly: null,
			"claude usage failed",
			RawResponseText: null,
			ErrorDetailsText: "unexpected boom",
			DateTimeOffset.UtcNow));

		row.FiveHourText.ShouldBe("—");
		row.WeeklyText.ShouldBe("—");
		row.HasErrorDetails.ShouldBeTrue();
		row.ErrorDetailsText.ShouldBe("unexpected boom");
	}

	private static SubscriptionUsageRow CreateRow(
		string profileId,
		int fiveHourUsedPercent,
		int weeklyUsedPercent)
	{
		AgentProfileRecord profile = new(
			profileId,
			profileId == "codex" ? AgentKind.Codex : AgentKind.Claude,
			profileId,
			profileId,
			null,
			"pwsh");
		SubscriptionUsageSnapshot snapshot = new(
			profile.Id,
			profile.DisplayName,
			profile.Kind,
			SubscriptionUsageState.Ready,
			new SubscriptionLimitSnapshot(fiveHourUsedPercent, null),
			new SubscriptionLimitSnapshot(weeklyUsedPercent, null),
			"Updated",
			RawResponseText: null,
			ErrorDetailsText: null,
			DateTimeOffset.UtcNow);

		return new SubscriptionUsageRow(profile).Apply(snapshot);
	}

	private string CreateTempDirectory()
	{
		var directory = TemporaryDirectory.Create();
		_temporaryDirectories.Add(directory);
		return directory.Path;
	}

	public void Dispose() => _temporaryDirectories.ForEach(static directory => directory.Dispose());

	private static async Task<string> WriteClaudeStatuslineAsync(string root, int usedPercent)
	{
		var path = Path.Combine(root, ".claude", "statusline-input.json");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(
			path,
			$$"""
            {
              "rate_limits": {
                "five_hour": { "used_percentage": {{usedPercent}} },
                "seven_day": { "used_percentage": {{usedPercent}} }
              }
            }
            """);
		return path;
	}

	private sealed class StubClaudeUsageCommandRunner : IClaudeUsageCommandRunner
	{
		private readonly Queue<ClaudeUsageCommandResult> _results;

		public StubClaudeUsageCommandRunner(params ClaudeUsageCommandResult[] results)
		{
			_results = new Queue<ClaudeUsageCommandResult>(results);
		}

		public List<string> Commands { get; } = [];

		public Task<ClaudeUsageCommandResult> RunAsync(
			string commandName,
			CancellationToken cancellationToken)
		{
			Commands.Add(commandName);
			return Task.FromResult(_results.Dequeue());
		}
	}

	private sealed class ThrowingClaudeUsageCommandRunner : IClaudeUsageCommandRunner
	{
		private readonly Exception _exception;

		public ThrowingClaudeUsageCommandRunner(Exception exception)
		{
			_exception = exception;
		}

		public Task<ClaudeUsageCommandResult> RunAsync(
			string commandName,
			CancellationToken cancellationToken) => throw _exception;
	}

	private sealed class FakeClaudeUsageProcessFactory : IClaudeUsageProcessFactory
	{
		private readonly IClaudeUsageProcess _process;

		public FakeClaudeUsageProcessFactory(IClaudeUsageProcess process)
		{
			_process = process;
		}

		public IClaudeUsageProcess Start(ProcessStartInfo startInfo) => _process;
	}

	private sealed class FakeClaudeUsageProcess : IClaudeUsageProcess
	{
		private readonly TaskCompletionSource<string>? _standardOutputAfterKill;
		private readonly TaskCompletionSource<string>? _standardErrorAfterKill;

		public FakeClaudeUsageProcess(
			string? standardOutputAfterKill = null,
			string? standardErrorAfterKill = null)
		{
			_standardOutputAfterKill = standardOutputAfterKill is null
				? null
				: new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			_standardErrorAfterKill = standardErrorAfterKill is null
				? null
				: new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			StandardOutput = _standardOutputAfterKill is null
				? new StringReader(string.Empty)
				: new BlockingTextReader(_standardOutputAfterKill.Task);
			StandardError = _standardErrorAfterKill is null
				? new StringReader(string.Empty)
				: new BlockingTextReader(_standardErrorAfterKill.Task);
			StandardOutputAfterKill = standardOutputAfterKill;
			StandardErrorAfterKill = standardErrorAfterKill;
		}

		private string? StandardOutputAfterKill { get; }
		private string? StandardErrorAfterKill { get; }

		public TextReader StandardOutput { get; }
		public TextReader StandardError { get; }
		public int ExitCode => 0;
		public bool HasExited => false;
		public bool KilledEntireProcessTree { get; private set; }
		public bool Disposed { get; private set; }

		public Task WaitForExitAsync(CancellationToken cancellationToken) =>
			Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

		public void Kill(bool entireProcessTree)
		{
			KilledEntireProcessTree = entireProcessTree;
			_standardOutputAfterKill?.TrySetResult(StandardOutputAfterKill!);
			_standardErrorAfterKill?.TrySetResult(StandardErrorAfterKill!);
		}

		public void Dispose() => Disposed = true;
	}

	private sealed class BlockingTextReader : TextReader
	{
		private readonly Task<string> _readTask;

		public BlockingTextReader(Task<string> readTask)
		{
			_readTask = readTask;
		}

		public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) =>
			_readTask.WaitAsync(cancellationToken);
	}
}
