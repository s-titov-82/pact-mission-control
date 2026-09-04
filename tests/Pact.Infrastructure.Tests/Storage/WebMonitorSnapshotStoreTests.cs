using System.Text.Json;
using Pact.Core.Web.Monitoring;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class WebMonitorSnapshotStoreTests : IDisposable
{
	private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _root => _temporaryDirectory.Path;

	[Test]
	public async Task SaveAsync_and_LoadAsync_round_trip_unread_snapshot_without_staging_files()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);
		var snapshot = CreateSnapshot("web-1", unread: true);

		await store.SaveAsync(snapshot, CancellationToken.None);

		(await store.LoadAsync(snapshot.WebPageId, CancellationToken.None)).ShouldBe(snapshot);
		Directory.EnumerateFiles(paths.AtomicTempDirectory).ShouldBeEmpty();
	}

	[Test]
	public async Task LoadAsync_returns_no_baseline_when_snapshot_is_exclusively_locked()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);
		await store.SaveAsync(
			CreateSnapshot("web-1", unread: true),
			CancellationToken.None);
		var path = Path.Combine(
			paths.WebMonitorSnapshotsDirectory,
			"web-1.json");
		await using var snapshotLock = File.Open(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.None);

		var snapshot =
			await store.LoadAsync("web-1", CancellationToken.None);

		snapshot.ShouldBeNull();
		File.Exists(path).ShouldBeTrue();
	}

	[Test]
	public async Task LoadAsync_does_not_swallow_cancellation()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Should.ThrowAsync<OperationCanceledException>(
			() => store.LoadAsync("missing", cancellation.Token));
	}

	[Test]
	public async Task SweepAsync_retains_existing_snapshot_and_deletes_orphan_malformed_and_mismatched_records()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);
		await store.SaveAsync(CreateSnapshot("web-1", unread: true), CancellationToken.None);
		await store.SaveAsync(CreateSnapshot("orphan", unread: false), CancellationToken.None);
		Directory.CreateDirectory(paths.WebMonitorSnapshotsDirectory);
		await File.WriteAllTextAsync(Path.Combine(paths.WebMonitorSnapshotsDirectory, "malformed.json"), "{");
		await File.WriteAllTextAsync(
			Path.Combine(paths.WebMonitorSnapshotsDirectory, "mismatched.json"),
								 /*lang=json,strict*/
								 """
            {"webPageId":"other","url":"https://example.test/","ruleId":"rule-1","ruleFingerprint":"fingerprint","activity":true,"revision":"42","unread":true,"observedAt":"2026-07-24T10:00:00+00:00"}
            """);

		await store.SweepAsync(new HashSet<string>(["web-1"]), CancellationToken.None);

		File.Exists(Path.Combine(paths.WebMonitorSnapshotsDirectory, "web-1.json")).ShouldBeTrue();
		File.Exists(Path.Combine(paths.WebMonitorSnapshotsDirectory, "orphan.json")).ShouldBeFalse();
		File.Exists(Path.Combine(paths.WebMonitorSnapshotsDirectory, "malformed.json")).ShouldBeFalse();
		File.Exists(Path.Combine(paths.WebMonitorSnapshotsDirectory, "mismatched.json")).ShouldBeFalse();
	}

	[Test]
	public async Task DeleteAsync_removes_the_snapshot()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);
		await store.SaveAsync(CreateSnapshot("web-1", unread: false), CancellationToken.None);

		await store.DeleteAsync("web-1", CancellationToken.None);

		(await store.LoadAsync("web-1", CancellationToken.None)).ShouldBeNull();
	}

	[Test]
	public async Task LoadAsync_deletes_json_with_missing_required_fields()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);
		var path = Path.Combine(paths.WebMonitorSnapshotsDirectory, "web-1.json");
		Directory.CreateDirectory(paths.WebMonitorSnapshotsDirectory);
		await File.WriteAllTextAsync(path, /*lang=json,strict*/ "{\"webPageId\":\"web-1\"}");

		(await store.LoadAsync("web-1", CancellationToken.None)).ShouldBeNull();
		File.Exists(path).ShouldBeFalse();
	}

	[Test]
	public async Task LoadAsync_deletes_malformed_json()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);
		var path = Path.Combine(paths.WebMonitorSnapshotsDirectory, "web-1.json");
		Directory.CreateDirectory(paths.WebMonitorSnapshotsDirectory);
		await File.WriteAllTextAsync(path, "{");

		(await store.LoadAsync("web-1", CancellationToken.None)).ShouldBeNull();
		File.Exists(path).ShouldBeFalse();
	}

	[TestCase("not-an-absolute-url", "2026-07-24T10:00:00+00:00")]
	[TestCase("https://example.test/builds/42#details", "2026-07-24T10:00:00+00:00")]
	[TestCase("https://example.test/builds/42", "0001-01-01T00:00:00+00:00")]
	public async Task LoadAsync_deletes_json_with_invalid_snapshot_semantics(string url, string observedAt)
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);
		var path = Path.Combine(paths.WebMonitorSnapshotsDirectory, "web-1.json");
		Directory.CreateDirectory(paths.WebMonitorSnapshotsDirectory);
		await File.WriteAllTextAsync(
			path,
			JsonSerializer.Serialize(CreateSnapshot("web-1", unread: false) with
			{
				Url = url,
				ObservedAt = DateTimeOffset.Parse(observedAt)
			}, SnapshotJsonOptions));

		(await store.LoadAsync("web-1", CancellationToken.None)).ShouldBeNull();
		File.Exists(path).ShouldBeFalse();
	}

	[Test]
	public async Task SweepAsync_deletes_json_with_invalid_snapshot_semantics()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);
		var path = Path.Combine(paths.WebMonitorSnapshotsDirectory, "web-1.json");
		Directory.CreateDirectory(paths.WebMonitorSnapshotsDirectory);
		await File.WriteAllTextAsync(
			path,
			JsonSerializer.Serialize(
				CreateSnapshot("web-1", unread: false) with { Url = "relative" },
				SnapshotJsonOptions));

		await store.SweepAsync(new HashSet<string>(["web-1"]), CancellationToken.None);

		File.Exists(path).ShouldBeFalse();
	}

	[Test]
	public async Task SweepAsync_deletes_json_with_missing_required_fields()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);
		var path = Path.Combine(paths.WebMonitorSnapshotsDirectory, "web-1.json");
		Directory.CreateDirectory(paths.WebMonitorSnapshotsDirectory);
		await File.WriteAllTextAsync(path, /*lang=json,strict*/ "{\"webPageId\":\"web-1\"}");

		await store.SweepAsync(new HashSet<string>(["web-1"]), CancellationToken.None);

		File.Exists(path).ShouldBeFalse();
	}

	[Test]
	public async Task SaveAsync_uses_distinct_files_for_case_distinct_web_page_ids()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);
		var lower = CreateSnapshot("web-1", unread: false);
		var upper = CreateSnapshot("WEB-1", unread: true) with { Revision = "43" };

		await store.SaveAsync(lower, CancellationToken.None);
		await store.SaveAsync(upper, CancellationToken.None);

		(await store.LoadAsync("web-1", CancellationToken.None)).ShouldBe(lower);
		(await store.LoadAsync("WEB-1", CancellationToken.None)).ShouldBe(upper);
		File.Exists(Path.Combine(paths.WebMonitorSnapshotsDirectory, "web-1.json")).ShouldBeTrue();
		Directory.EnumerateFiles(paths.WebMonitorSnapshotsDirectory)
			.Select(Path.GetFileName)
			.ShouldNotContain("WEB-1.json");

		await store.DeleteAsync("WEB-1", CancellationToken.None);

		(await store.LoadAsync("web-1", CancellationToken.None)).ShouldBe(lower);
		(await store.LoadAsync("WEB-1", CancellationToken.None)).ShouldBeNull();
	}

	[Test]
	public async Task SaveAsync_hashes_reserved_windows_file_names()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);
		var snapshot = CreateSnapshot("CON", unread: true);

		await store.SaveAsync(snapshot, CancellationToken.None);

		(await store.LoadAsync("CON", CancellationToken.None)).ShouldBe(snapshot);
		File.Exists(Path.Combine(paths.WebMonitorSnapshotsDirectory, "CON.json")).ShouldBeFalse();
	}

	[Test]
	public async Task SaveAsync_rejects_web_page_ids_that_escape_the_snapshot_directory()
	{
		AppPaths paths = new(_root);
		DataRootHousekeeping.Prepare(paths);
		WebMonitorSnapshotStore store = new(paths);

		await Should.ThrowAsync<ArgumentException>(() =>
			store.SaveAsync(CreateSnapshot("..\\outside", unread: false), CancellationToken.None));

		Directory.EnumerateFiles(paths.RetainedTempDirectory, "outside.json", SearchOption.AllDirectories)
			.ShouldBeEmpty();
	}

	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}

	private static WebMonitorSnapshot CreateSnapshot(string webPageId, bool unread) => new(
		webPageId,
		"https://example.test/builds/42",
		"rule-1",
		"fingerprint",
		Activity: true,
		Revision: "42",
		Unread: unread,
		ObservedAt: new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero));
}
