using Pact.Core.Remarks;
using Pact.Infrastructure.Remarks;

namespace Pact.Infrastructure.Tests.Remarks;

public sealed class RemarkFileStoreTests
{
	[Test]
	public async Task Threads_and_the_next_number_survive_a_save_and_load()
	{
		using var root = TemporaryDirectory.Create();
		RemarkFileStore store = new(root.Path);
		RemarkAnchor anchor = new("src/X.cs", RemarkSide.Old, 7, "gone();", ["a"], ["b", "c"], null, null);
		RemarkThread thread = new(
			"C2",
			anchor,
			[
				new RemarkMessage("m1", RemarkAuthor.User, "why?", DateTimeOffset.Parse("2026-10-27T10:00:00+03:00"), RemarkDelivery.Sent, "s1"),
				new RemarkMessage("m2", RemarkAuthor.Agent, "because", DateTimeOffset.Parse("2026-10-27T10:05:00+03:00"), RemarkDelivery.Sent, null)
			],
			IsResolved: true);

		await store.SaveAsync(new RemarkFile(3, [thread]), CancellationToken.None);
		var loaded = await new RemarkFileStore(root.Path).LoadAsync(CancellationToken.None);

		loaded.NextNumber.ShouldBe(3);
		loaded.SetAsideFile.ShouldBeNull();
		var back = loaded.Threads.ShouldHaveSingleItem();
		back.Id.ShouldBe("C2");
		back.IsResolved.ShouldBeTrue();
		back.Anchor.ShouldBe(anchor, new AnchorComparer());
		back.Messages.ShouldBe(thread.Messages);
	}

	[Test]
	public async Task A_missing_file_loads_empty_with_number_one()
	{
		using var root = TemporaryDirectory.Create();

		var loaded = await new RemarkFileStore(root.Path).LoadAsync(CancellationToken.None);

		loaded.NextNumber.ShouldBe(1);
		loaded.Threads.ShouldBeEmpty();
	}

	[Test]
	public async Task Saving_writes_the_gitignore_of_the_pact_folder()
	{
		using var root = TemporaryDirectory.Create();

		await new RemarkFileStore(root.Path).SaveAsync(new RemarkFile(1, []), CancellationToken.None);

		(await File.ReadAllTextAsync(Path.Combine(root.Path, ".pact", ".gitignore"))).ShouldBe("*\n");
		File.Exists(Path.Combine(root.Path, ".pact", "remarks", "remarks.json")).ShouldBeTrue();
	}

	[Test]
	public async Task An_unreadable_file_is_set_aside_not_lost()
	{
		using var root = TemporaryDirectory.Create();
		var folder = Path.Combine(root.Path, ".pact", "remarks");
		Directory.CreateDirectory(folder);
		await File.WriteAllTextAsync(Path.Combine(folder, "remarks.json"), "{broken");

		var loaded = await new RemarkFileStore(root.Path).LoadAsync(CancellationToken.None);

		loaded.Threads.ShouldBeEmpty();
		loaded.NextNumber.ShouldBe(1);
		loaded.SetAsideFile.ShouldNotBeNull();
		Path.GetFileName(loaded.SetAsideFile).ShouldStartWith("remarks.json.broken-");
		(await File.ReadAllTextAsync(loaded.SetAsideFile)).ShouldBe("{broken");
		File.Exists(Path.Combine(folder, "remarks.json")).ShouldBeFalse();
	}

	[TestCase("""{"version":1,"nextNumber":2,"threads":[null]}""")]
	[TestCase("""{"version":1,"nextNumber":2,"threads":[{"id":"C1","anchor":{"path":"a.cs","side":"new","line":1,"lineText":"x","before":[],"after":[]},"isResolved":false}]}""")]
	[TestCase("""{"version":1,"nextNumber":2,"threads":[{"id":"C1","anchor":{"path":"a.cs","side":"new","line":1,"lineText":"x","before":[],"after":[]},"messages":[null],"isResolved":false}]}""")]
	[TestCase("""{"version":1,"nextNumber":2,"threads":[{"id":"C1","messages":[],"isResolved":false}]}""")]
	[TestCase("""{"version":1,"nextNumber":2,"threads":[{"id":"","anchor":{"path":"a.cs","side":"old","line":1,"lineText":"x","before":[],"after":[]},"messages":[],"isResolved":false}]}""")]
	[TestCase("""{"version":1,"nextNumber":2,"threads":[{"id":"X1","anchor":{"path":"a.cs","side":"old","line":1,"lineText":"x","before":[],"after":[]},"messages":[],"isResolved":false}]}""")]
	public async Task Threads_with_missing_parts_are_set_aside(string json)
	{
		using var root = TemporaryDirectory.Create();
		var folder = Path.Combine(root.Path, ".pact", "remarks");
		Directory.CreateDirectory(folder);
		await File.WriteAllTextAsync(Path.Combine(folder, "remarks.json"), json);

		var loaded = await new RemarkFileStore(root.Path).LoadAsync(CancellationToken.None);

		loaded.Threads.ShouldBeEmpty();
		loaded.SetAsideFile.ShouldNotBeNull();
		(await File.ReadAllTextAsync(loaded.SetAsideFile)).ShouldBe(json);
	}

	[Test]
	public async Task A_second_unreadable_file_in_the_same_second_keeps_the_first()
	{
		using var root = TemporaryDirectory.Create();
		ManualTimeProvider time = new(DateTimeOffset.Parse("2026-10-27T12:00:00+00:00"));
		var folder = Path.Combine(root.Path, ".pact", "remarks");
		Directory.CreateDirectory(folder);
		var taken = Path.Combine(folder, $"remarks.json.broken-{time.GetLocalNow():yyyyMMddHHmmss}");
		await File.WriteAllTextAsync(taken, "first");
		await File.WriteAllTextAsync(Path.Combine(folder, "remarks.json"), "{second");

		var loaded = await new RemarkFileStore(root.Path, time).LoadAsync(CancellationToken.None);

		loaded.SetAsideFile.ShouldNotBe(taken);
		(await File.ReadAllTextAsync(taken)).ShouldBe("first");
		(await File.ReadAllTextAsync(loaded.SetAsideFile!)).ShouldBe("{second");
	}

	[Test]
	public async Task A_snapshot_is_stored_once_by_its_hash()
	{
		using var root = TemporaryDirectory.Create();
		RemarkFileStore store = new(root.Path);

		var first = await store.SaveSnapshotAsync("a\r\nb\n", CancellationToken.None);
		var second = await store.SaveSnapshotAsync("a\r\nb\n", CancellationToken.None);

		second.ShouldBe(first);
		first.ShouldMatch("^[0-9a-f]{64}$");
		Directory.GetFiles(Path.Combine(root.Path, ".pact", "remarks", "snapshots")).Length.ShouldBe(1);
		(await store.LoadSnapshotLinesAsync(first, CancellationToken.None)).ShouldBe(["a", "b"]);
		(await store.LoadSnapshotLinesAsync(new string('0', 64), CancellationToken.None)).ShouldBeNull();
	}

	// Records compare lists by reference; the anchor's lists are compared by content here.
	private sealed class AnchorComparer : IEqualityComparer<RemarkAnchor>
	{
		public bool Equals(RemarkAnchor? x, RemarkAnchor? y) =>
			x is not null && y is not null
			&& x with { Before = [], After = [] } == y with { Before = [], After = [] }
			&& x.Before.SequenceEqual(y.Before)
			&& x.After.SequenceEqual(y.After);

		public int GetHashCode(RemarkAnchor obj) => obj.Line;
	}
}
