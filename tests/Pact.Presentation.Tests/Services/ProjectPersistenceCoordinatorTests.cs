using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class ProjectPersistenceCoordinatorTests
{
	[Test]
	public async Task UpdateSessionAsync_persists_session_and_project_activity_atomically()
	{
		var createdAt = DateTimeOffset.UtcNow.AddHours(-1);
		var updatedAt = createdAt.AddMinutes(10);
		SessionRecord session = new(
			"session-1",
			AgentKind.Codex,
			"Before",
			@"D:\Work",
			"codex",
			null,
			SessionStatus.Stopped,
			createdAt,
			createdAt);
		ProjectRecord project = new(
			"project-1",
			"Project",
			@"D:\Work",
			createdAt,
			createdAt,
			null)
		{
			Sessions = [session]
		};
		InMemoryProjectStore store = new(
			ProjectsDocument.CreateDefault() with { Projects = [project] });
		ProjectPersistenceCoordinator coordinator = new(store);

		var result = await coordinator.UpdateSessionAsync(
			"session-1",
			record => record with { Title = "After", LastActiveAt = updatedAt },
			CancellationToken.None);

		result.ShouldNotBeNull();
		result.Session.Title.ShouldBe("After");
		result.Project.LastActiveAt.ShouldBe(updatedAt);
		var saved = store.Document.Projects.ShouldHaveSingleItem();
		saved.ShouldBe(result.Project);
		saved.Sessions.ShouldHaveSingleItem().ShouldBe(result.Session);
		store.UpdateCount.ShouldBe(1);
	}

	[Test]
	public async Task RemoveSessionAsync_replaces_only_the_owning_projects_active_item()
	{
		var now = DateTimeOffset.UtcNow;
		SessionRecord removed = new(
			"session-1",
			AgentKind.Codex,
			"Removed",
			@"D:\One",
			"codex",
			null,
			SessionStatus.Stopped,
			now,
			now);
		var replacement = removed with { Id = "session-2", Title = "Replacement" };
		ProjectRecord owner = new(
			"project-1",
			"One",
			@"D:\One",
			now,
			now,
			null)
		{
			ActiveItemId = removed.Id,
			Sessions = [removed, replacement]
		};
		ProjectRecord other = new(
			"project-2",
			"Two",
			@"D:\Two",
			now,
			now,
			null)
		{
			ActiveItemId = "other-session",
			Sessions = [removed with { Id = "other-session" }]
		};
		InMemoryProjectStore store = new(
			ProjectsDocument.CreateDefault() with { Projects = [owner, other] });
		ProjectStructurePersistenceCoordinator coordinator = new(store);

		var updated = await coordinator.RemoveSessionAsync(
			removed.Id,
			replacement.Id,
			CancellationToken.None);

		updated.ShouldHaveSingleItem().Id.ShouldBe(owner.Id);
		var savedOwner = store.Document.Projects.Single(project => project.Id == owner.Id);
		savedOwner.Sessions.ShouldHaveSingleItem().Id.ShouldBe(replacement.Id);
		savedOwner.ActiveItemId.ShouldBe(replacement.Id);
		store.Document.Projects.Single(project => project.Id == other.Id).ShouldBe(other);
	}

	private sealed class InMemoryProjectStore(ProjectsDocument document) : IProjectStore
	{
		public ProjectsDocument Document { get; private set; } = document;
		public int UpdateCount { get; private set; }

		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) =>
			Task.FromResult(Document);

		public Task SaveAsync(
			ProjectsDocument document,
			CancellationToken cancellationToken)
		{
			Document = document;
			return Task.CompletedTask;
		}

		public Task<ProjectsDocument> UpdateAsync(
			Func<ProjectsDocument, ProjectsDocument> update,
			CancellationToken cancellationToken)
		{
			UpdateCount++;
			Document = update(Document);
			return Task.FromResult(Document);
		}
	}
}
