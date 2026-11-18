using System.Text.Json;
using Pact.Core.Boards;
using Pact.Core.Projects;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class ProjectBoardStoreTests : IDisposable
{
	private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
	private static readonly BoardActor Actor = new("agent", "Agent");
	private const string Project = @"D:\project";
	private AppPaths Paths => new(_directory.Path);
	private string BoardPath => Path.Combine(Paths.SettingsDirectory, "Boards", ProjectNoteFileKey.FromRootPath(Project) + ".json");
	private ProjectBoardStore Store() => new(Paths, TimeProvider.System);
	public void Dispose() => _directory.Dispose();

	[Test]
	public async Task Archive_larger_than_a_tool_response_loads_and_does_not_consume_active_limit()
	{
		var cards = Enumerable.Range(1, 210).Select(number => new BoardCard
		{
			Id = Guid.NewGuid().ToString("N"),
			Number = number,
			Title = "Archived",
			Description = new string('x', BoardLimits.Description),
			Status = BoardCardStatus.Done,
			IsArchived = true,
			CreatedAt = DateTimeOffset.UnixEpoch,
			UpdatedAt = DateTimeOffset.UnixEpoch
		}).ToArray();
		Directory.CreateDirectory(Paths.BoardsDirectory);
		await File.WriteAllTextAsync(BoardPath, JsonSerializer.Serialize(new BoardState { Cards = cards, NextCardNumber = 211 }));
		new FileInfo(BoardPath).Length.ShouldBeGreaterThan(1024 * 1024);
		(await Store().LoadAsync(Project, CancellationToken.None)).State.Cards.Count.ShouldBe(210);
		var created = await Execute(new CreateCard("Active", "", null));
		created.Outcome.ShouldBe(BoardMutationOutcome.Applied);
		created.Snapshot.State.Cards.Count(card => !card.IsArchived).ShouldBe(1);
		created.Snapshot.State.Cards.Count(card => card.IsArchived).ShouldBe(210);
	}

	[Test]
	public async Task Missing_file_is_empty_without_creating_file()
	{
		(await Store().LoadAsync(Project, CancellationToken.None)).State.Cards.ShouldBeEmpty();
		File.Exists(BoardPath).ShouldBeFalse();
	}

	[Test]
	public async Task Create_and_comment_round_trip_across_store_instances_and_path_spelling()
	{
		var created = await Execute(new CreateCard("Title", "Body", "https://jira.example/PROJ-1"));
		var added = await Execute(new AddComment(created.CardId!, "Result"));
		var read = await Store().LoadAsync(@"d:/PROJECT/", CancellationToken.None);
		read.State.Cards.Single().Title.ShouldBe("Title");
		read.State.Cards.Single().Comments.Single().Text.ShouldBe("Result");
		read.DiskRevision.ShouldBe(added.Snapshot.DiskRevision);
		await Execute(new DeleteComment(created.CardId!, added.CommentId!));
		(await Store().LoadAsync(Project, CancellationToken.None)).State.Cards.Single().Comments.ShouldBeEmpty();
	}

	[Test]
	public async Task Concurrent_take_next_only_one_wins()
	{
		var created = await Execute(new CreateCard("Ready", "", null));
		var card = created.Snapshot.State.Cards.Single();
		var ready = await Execute(new UpdateCard(card.Id, BoardRevisions.ForCard(card), new() { Status = new(BoardCardStatus.Ready) }));
		var command = new TakeNext(ready.Snapshot.OrderRevision);
		var outcomes = await Task.WhenAll(Execute(command), Execute(command));
		outcomes.Count(o => o.Outcome == BoardMutationOutcome.Applied).ShouldBe(1);
		outcomes.Count(o => o.Outcome == BoardMutationOutcome.Conflict).ShouldBe(1);
		(await Store().LoadAsync(Project, CancellationToken.None)).State.Cards.Single().Status.ShouldBe(BoardCardStatus.InProgress);
	}

	[Test]
	public async Task External_edit_refreshes_projection_and_rejects_old_card_token()
	{
		var created = await Execute(new CreateCard("Original", "", null));
		var original = created.Snapshot.State.Cards.Single();
		await File.WriteAllTextAsync(BoardPath, JsonSerializer.Serialize(created.Snapshot.State with { Cards = [original with { Title = "External" }] }));
		var result = await Execute(new UpdateCard(original.Id, BoardRevisions.ForCard(original), new() { Title = new("Stale") }));
		result.Outcome.ShouldBe(BoardMutationOutcome.Conflict);
		result.Snapshot.State.Cards.Single().Title.ShouldBe("External");
		result.Snapshot.DiskRevision.ShouldNotBe(created.Snapshot.DiskRevision);
	}

	[TestCase("{broken")]
	[TestCase("{\"SchemaVersion\":99}")]
	public async Task Malformed_or_unknown_schema_does_not_overwrite(string json)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(BoardPath)!);
		await File.WriteAllTextAsync(BoardPath, json);
		await Should.ThrowAsync<InvalidDataException>(() => Execute(new CreateCard("No", "", null)));
		(await File.ReadAllTextAsync(BoardPath)).ShouldBe(json);
	}

	[Test]
	public async Task Failed_replace_keeps_previous_bytes()
	{
		await Execute(new CreateCard("Original", "", null));
		var before = await File.ReadAllBytesAsync(BoardPath);
		using (var locked = new FileStream(BoardPath, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			await Should.ThrowAsync<IOException>(() => Execute(new CreateCard("Must fail", "", null)));
		}
		(await File.ReadAllBytesAsync(BoardPath)).ShouldBe(before);
		(await Store().LoadAsync(Project, CancellationToken.None)).State.Cards.Count.ShouldBe(1);
	}

	private Task<BoardMutationResult> Execute(BoardCommand command) => Store().ExecuteAsync(Project, command, Actor, CancellationToken.None);
}
