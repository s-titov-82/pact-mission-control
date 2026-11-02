using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.RootTabs;
using Pact.Core.Sessions;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class SessionBookmarkStoreTests
{
	[TestCase(false)]
	[TestCase(true)]
	public async Task Manual_bookmark_round_trips_true_then_false_without_unknown_node_merge_resurrecting_it(bool root)
	{
		using var directory = TemporaryDirectory.Create();
		var now = DateTimeOffset.UtcNow;
		SessionRecord session = new("s", AgentKind.Codex, "Agent", directory.Path, "codex", null, SessionStatus.Stopped, now, now);
		foreach (var marked in new[] { true, false })
		{
			if (root)
			{
				JsonRootTabsStore store = new(directory.Path);
				await store.SaveAsync(new RootTabsRecord(1, "s", [session with { IsMarkedForLater = marked }], [], []), CancellationToken.None);
				(await new JsonRootTabsStore(directory.Path).LoadAsync(CancellationToken.None)).Sessions.Single().IsMarkedForLater.ShouldBe(marked);
			}
			else
			{
				JsonProjectStore store = new(directory.Path);
				await store.SaveAsync(ProjectsDocument.CreateDefault() with
				{
					Projects = [new ProjectRecord("p", "Project", directory.Path, now, now, null)
					{
						Sessions = [session with { IsMarkedForLater = marked }]
					}]
				}, CancellationToken.None);
				(await new JsonProjectStore(directory.Path).LoadAsync(CancellationToken.None)).Projects.Single().Sessions.Single().IsMarkedForLater.ShouldBe(marked);
			}
		}
	}
}
