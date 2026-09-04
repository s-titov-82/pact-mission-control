using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class SessionViewModelTests
{
	[Test]
	public void ApplyTerminalStatus_projects_indicator_and_engine_busy_timestamp()
	{
		SessionViewModel session = new(CreateSessionRecord());
		var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
		List<string?> changedProperties = [];
		session.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		session.ApplyTerminalStatus(TerminalTabIndicator.Busy, startedAt, "Thinking");

		session.Indicator.ShouldBe(TerminalTabIndicator.Busy);
		session.BusySince.ShouldBe(startedAt);
		session.StatusDescription.ShouldBe("Thinking");
		changedProperties.ShouldContain(nameof(SessionViewModel.Indicator));
		changedProperties.ShouldContain(nameof(SessionViewModel.BusySince));
		changedProperties.ShouldContain(nameof(SessionViewModel.StatusDescription));
	}

	[Test]
	public void ApplyTerminalStatus_notifies_description_when_indicator_is_unchanged()
	{
		SessionViewModel session = new(CreateSessionRecord());
		session.ApplyTerminalStatus(TerminalTabIndicator.Busy, DateTimeOffset.UtcNow, "Thinking");
		List<string?> changedProperties = [];
		session.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		session.ApplyTerminalStatus(TerminalTabIndicator.Busy, null, "Working");

		session.StatusDescription.ShouldBe("Working");
		changedProperties.ShouldBe([nameof(SessionViewModel.StatusDescription)]);
	}

	[Test]
	public void Title_uses_session_title()
	{
		SessionViewModel session = new(CreateSessionRecord() with
		{
			Title = "Reviewer"
		});

		session.Title.ShouldBe("Reviewer");
	}

	[Test]
	public void TerminalKind_uses_lowercase_session_kind()
	{
		SessionViewModel session = new(CreateSessionRecord() with
		{
			Kind = AgentKind.Pwsh
		});

		session.TerminalKind.ShouldBe("pwsh");
	}

	[Test]
	public void Working_directory_line_is_hidden_when_it_matches_project_root()
	{
		SessionViewModel session = new(
			CreateSessionRecord() with { WorkingDirectory = @"D:\Work\Project" },
			@"D:\Work\Project\");

		session.ShowWorkingDirectory.ShouldBeFalse();
		session.WorkingDirectoryText.ShouldBe(string.Empty);
	}

	[Test]
	public void Working_directory_line_is_visible_when_it_differs_from_project_root()
	{
		SessionViewModel session = new(
			CreateSessionRecord() with { WorkingDirectory = @"D:\Work\Project\subdir" },
			@"D:\Work\Project");

		session.ShowWorkingDirectory.ShouldBeTrue();
		session.WorkingDirectoryText.ShouldBe(@"D:\Work\Project\subdir");
	}

	[Test]
	public void Current_terminal_selection_only_updates_presentation_state()
	{
		SessionViewModel session = new(CreateSessionRecord());
		session.ApplyTerminalStatus(TerminalTabIndicator.Unread, null, string.Empty);

		session.SetCurrentTerminal(true);

		session.IsCurrentTerminal.ShouldBeTrue();
		session.Indicator.ShouldBe(TerminalTabIndicator.Unread);
	}

	[Test]
	public void LockForScenario_sets_lock_state_and_notifies_dependents()
	{
		SessionViewModel session = new(CreateSessionRecord());
		List<string?> changedProperties = [];
		session.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		session.LockForScenario("run-1");

		session.LockedByScenarioRunId.ShouldBe("run-1");
		session.IsLockedByScenario.ShouldBeTrue();
		changedProperties.ShouldContain(nameof(SessionViewModel.LockedByScenarioRunId));
		changedProperties.ShouldContain(nameof(SessionViewModel.IsLockedByScenario));
	}

	[Test]
	public void UnlockFromScenario_clears_lock_state_and_notifies_dependents()
	{
		SessionViewModel session = new(CreateSessionRecord());
		session.LockForScenario("run-1");
		List<string?> changedProperties = [];
		session.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		session.UnlockFromScenario();

		session.LockedByScenarioRunId.ShouldBeNull();
		session.IsLockedByScenario.ShouldBeFalse();
		changedProperties.ShouldContain(nameof(SessionViewModel.LockedByScenarioRunId));
		changedProperties.ShouldContain(nameof(SessionViewModel.IsLockedByScenario));
	}

	[Test]
	[TestCase(AgentKind.Claude, true)]
	[TestCase(AgentKind.Codex, true)]
	[TestCase(AgentKind.Pwsh, false)]
	[TestCase(AgentKind.Hermes, false)]
	[TestCase(AgentKind.Custom, false)]
	public void CanResetAgentSession_true_only_for_resumable_agents(AgentKind kind, bool expected)
	{
		SessionViewModel session = new(CreateSessionRecord() with
		{
			Kind = kind
		});

		session.CanResetAgentSession.ShouldBe(expected);
	}

	private static SessionRecord CreateSessionRecord()
	{
		var now = DateTimeOffset.UtcNow;
		return new SessionRecord(
			"session-1",
			AgentKind.Codex,
			"Codex session",
			@"D:\Work",
			"codex",
			null,
			SessionStatus.Running,
			now,
			now);
	}
}
