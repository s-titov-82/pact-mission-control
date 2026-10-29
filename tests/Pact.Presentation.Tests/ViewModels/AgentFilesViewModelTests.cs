using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class AgentFilesViewModelTests
{
	private static readonly DateTimeOffset At = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
	private static readonly string[] Temp = [@"C:\Users\u\AppData\Local\Temp\"];

	[Test]
	public void Items_show_name_and_location_newest_first()
	{
		SessionViewModel session = new(Record(
			new AgentFileLink(@"D:\proj\docs\plan.md", "Plan", At),
			new AgentFileLink(@"C:\Users\u\AppData\Local\Temp\out.log", null, At)));
		using AgentFilesViewModel files = new(session, @"D:\proj", Temp, (_, _, _) => Task.CompletedTask);

		files.HasItems.ShouldBeTrue();
		files.Items.ShouldBe(
		[
			new AgentFileItem(@"D:\proj\docs\plan.md", "plan.md", "project", "Plan\n" + @"D:\proj\docs\plan.md"),
			new AgentFileItem(@"C:\Users\u\AppData\Local\Temp\out.log", "out.log", "Temp", @"C:\Users\u\AppData\Local\Temp\out.log")
		]);
	}

	[Test]
	public void Items_follow_a_new_record_of_the_session()
	{
		SessionViewModel session = new(Record());
		using AgentFilesViewModel files = new(session, null, Temp, (_, _, _) => Task.CompletedTask);
		List<string?> changed = [];
		files.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
		files.HasItems.ShouldBeFalse();

		session.UpdateRecord(session.Record with { FileLinks = [new AgentFileLink(@"C:\tools\a.txt", null, At)] });

		files.Items.Single().Location.ShouldBe("tools");
		files.HasItems.ShouldBeTrue();
		changed.ShouldContain(nameof(AgentFilesViewModel.Items));
		changed.ShouldContain(nameof(AgentFilesViewModel.HasItems));
	}

	[Test]
	public async Task Remove_calls_back_with_the_session_and_path()
	{
		SessionViewModel session = new(Record(new AgentFileLink(@"C:\tools\a.txt", null, At)));
		List<(string Session, string Path)> removed = [];
		using AgentFilesViewModel files = new(session, null, Temp, (id, path, _) =>
		{
			removed.Add((id, path));
			return Task.CompletedTask;
		});

		await files.RemoveAsync(files.Items.Single(), CancellationToken.None);

		removed.ShouldBe([("s1", @"C:\tools\a.txt")]);
	}

	[Test]
	public void Dispose_stops_following_the_session()
	{
		SessionViewModel session = new(Record());
		AgentFilesViewModel files = new(session, null, Temp, (_, _, _) => Task.CompletedTask);

		files.Dispose();
		session.UpdateRecord(session.Record with { FileLinks = [new AgentFileLink(@"C:\tools\a.txt", null, At)] });

		files.Items.ShouldBeEmpty();
	}

	private static SessionRecord Record(params AgentFileLink[] links) =>
		new("s1", AgentKind.Claude, "Claude", @"D:\proj", "claude", null, SessionStatus.Stopped, At, At) { FileLinks = links };
}
