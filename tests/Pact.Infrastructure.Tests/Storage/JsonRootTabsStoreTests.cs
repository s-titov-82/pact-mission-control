using System.Text.Json;
using Pact.Core.Agents;
using Pact.Core.RootTabs;
using Pact.Core.Sessions;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class JsonRootTabsStoreTests
{
	[Test]
	public async Task LoadAsync_returns_default_when_file_is_missing()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		JsonRootTabsStore store = new(temporaryDirectory.Path);

		var document = await store.LoadAsync(CancellationToken.None);

		document.ShouldBe(RootTabsRecord.CreateDefault());
	}

	[Test]
	public async Task LoadAsync_normalizes_transient_terminal_status()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var paths = new AppPaths(temporaryDirectory.Path);
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(
			paths.RootTabsPath,
			/*lang=json,strict*/
			"""
			{
			  "schemaVersion": 1,
			  "activeItemId": "root-session-1",
			  "sessions": [{
			    "id": "root-session-1",
			    "kind": "hermes",
			    "title": "Hermes",
			    "workingDirectory": "C:\\Workspaces\\example",
			    "startCommand": "hermes",
			    "resumeCommand": null,
			    "status": "running",
			    "createdAt": "2026-07-30T12:00:00Z",
			    "lastActiveAt": "2026-07-30T12:00:00Z"
			  }],
			  "webPages": [],
			  "pausedItemIds": []
			}
			""");

		JsonRootTabsStore store = new(paths);
		var document = await store.LoadAsync(CancellationToken.None);

		document.Sessions.Single().Status.ShouldBe(SessionStatus.Stopped);
	}

	[Test]
	public async Task UpdateAsync_preserves_unknown_root_and_item_properties()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var paths = new AppPaths(temporaryDirectory.Path);
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(
			paths.RootTabsPath,
			/*lang=json,strict*/
			"""
			{
			  "schemaVersion": 1,
			  "futureRoot": {"enabled": true},
			  "activeItemId": "root-session-1",
			  "sessions": [{
			    "id": "root-session-1",
			    "kind": "hermes",
			    "title": "Hermes",
			    "workingDirectory": "C:\\Workspaces\\example",
			    "startCommand": "hermes",
			    "resumeCommand": null,
			    "status": "stopped",
			    "createdAt": "2026-07-30T12:00:00Z",
			    "lastActiveAt": "2026-07-30T12:00:00Z",
			    "futureSession": 42
			  }],
			  "webPages": [],
			  "pausedItemIds": []
			}
			""");
		JsonRootTabsStore store = new(paths);

		await store.UpdateAsync(
			document => document with
			{
				Sessions =
				[
					document.Sessions.Single() with { Title = "General Hermes" }
				]
			},
			CancellationToken.None);

		using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(paths.RootTabsPath));
		saved.RootElement.GetProperty("futureRoot").GetProperty("enabled").GetBoolean().ShouldBeTrue();
		var session = saved.RootElement.GetProperty("sessions")[0];
		session.GetProperty("title").GetString().ShouldBe("General Hermes");
		session.GetProperty("futureSession").GetInt32().ShouldBe(42);
	}

	[Test]
	public async Task SaveAsync_preserves_unrecognized_items_with_non_string_ids()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var paths = new AppPaths(temporaryDirectory.Path);
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(
			paths.RootTabsPath,
			/*lang=json,strict*/
			"""
			{
			  "schemaVersion": 1,
			  "activeItemId": null,
			  "sessions": [{ "id": 42, "futureKind": "remote" }],
			  "webPages": [],
			  "pausedItemIds": []
			}
			""");
		JsonRootTabsStore store = new(paths);

		await store.SaveAsync(RootTabsRecord.CreateDefault(), CancellationToken.None);

		using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(paths.RootTabsPath));
		saved.RootElement.GetProperty("sessions")[0].GetProperty("id").GetInt32().ShouldBe(42);
	}

	[Test]
	public async Task UpdateAsync_serializes_concurrent_changes()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		JsonRootTabsStore store = new(temporaryDirectory.Path);
		await store.SaveAsync(RootTabsRecord.CreateDefault(), CancellationToken.None);

		var updates = Enumerable.Range(0, 12)
			.Select(index => store.UpdateAsync(
				document => document with
				{
					Sessions = document.Sessions.Concat([CreateSession(index)]).ToArray()
				},
				CancellationToken.None))
			.ToArray();

		await Task.WhenAll(updates);

		(await store.LoadAsync(CancellationToken.None)).Sessions.Count.ShouldBe(12);
	}

	[Test]
	public async Task LoadAsync_returns_default_without_overwriting_malformed_file()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var paths = new AppPaths(temporaryDirectory.Path);
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(paths.RootTabsPath, "{ malformed");
		JsonRootTabsStore store = new(paths);

		var document = await store.LoadAsync(CancellationToken.None);

		document.ShouldBe(RootTabsRecord.CreateDefault());
		(await File.ReadAllTextAsync(paths.RootTabsPath)).ShouldBe("{ malformed");
	}

	private static SessionRecord CreateSession(int index)
	{
		var now = DateTimeOffset.Parse("2026-07-30T12:00:00Z");
		return new SessionRecord(
			$"root-session-{index}",
			AgentKind.Hermes,
			$"Hermes {index}",
			@"C:\Workspaces\example",
			"hermes",
			null,
			SessionStatus.Stopped,
			now,
			now);
	}
}
