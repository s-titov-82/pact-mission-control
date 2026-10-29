using Pact.Core.Sessions;

namespace Pact.Core.Tests.Sessions;

public sealed class AgentFileLinksTests
{
	private static readonly DateTimeOffset At = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

	[Test]
	public void Add_puts_the_new_link_first()
	{
		var links = AgentFileLinks.Add([Link(@"C:\x\a.md")], Link(@"C:\x\b.md"));

		links.Select(link => link.Path).ShouldBe([@"C:\x\b.md", @"C:\x\a.md"]);
	}

	[Test]
	public void Add_treats_the_same_file_spelled_differently_as_one_link()
	{
		var links = AgentFileLinks.Add(
			[Link(@"C:\x\a.md"), Link(@"C:\x\b.md")],
			new AgentFileLink("c:/X/A.md", "Plan", At));

		links.Count.ShouldBe(2);
		links[0].ShouldBe(new AgentFileLink("c:/X/A.md", "Plan", At));
		links[1].Path.ShouldBe(@"C:\x\b.md");
	}

	[Test]
	public void Add_drops_the_oldest_past_twenty()
	{
		IReadOnlyList<AgentFileLink> links = [];
		for (var index = 0; index < AgentFileLinks.MaxPerSession; index++)
		{
			links = AgentFileLinks.Add(links, Link($@"C:\x\{index}.txt"));
		}

		links = AgentFileLinks.Add(links, Link(@"C:\x\new.txt"));

		links.Count.ShouldBe(AgentFileLinks.MaxPerSession);
		links[0].Path.ShouldBe(@"C:\x\new.txt");
		links.ShouldNotContain(link => link.Path == @"C:\x\0.txt");
	}

	[Test]
	public void Remove_drops_the_link_with_that_path_in_any_spelling()
	{
		var links = AgentFileLinks.Remove([Link(@"C:\x\a.md"), Link(@"C:\x\b.md")], "c:/x/A.MD");

		links.Select(link => link.Path).ShouldBe([@"C:\x\b.md"]);
	}

	[Test]
	public void Remove_of_an_unknown_path_returns_the_same_list()
	{
		IReadOnlyList<AgentFileLink> links = [Link(@"C:\x\a.md")];

		AgentFileLinks.Remove(links, @"C:\x\z.md").ShouldBeSameAs(links);
	}

	private static AgentFileLink Link(string path) => new(path, null, At);
}
