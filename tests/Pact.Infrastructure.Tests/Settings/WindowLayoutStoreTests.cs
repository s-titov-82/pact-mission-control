using System.Text.Json;

namespace Pact.Infrastructure.Tests.Settings;

public sealed class WindowLayoutStoreTests
{
	[Test]
	public void Load_returns_null_when_file_is_missing()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		WindowLayoutStore store = new(Path.Combine(root, "window-layout.json"));

		var layout = store.Load();

		layout.ShouldBeNull();
	}

	[Test]
	public async Task SaveAsync_round_trips_window_and_column_dimensions()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var path = Path.Combine(root, "window-layout.json");
		WindowLayoutStore store = new(path);
		AppWindowLayout expected = new(
			Left: 120,
			Top: 80,
			Width: 1440,
			Height: 900,
			WindowState: "maximized",
			LeftColumnWidth: 310,
			RightColumnWidth: 380);

		await store.SaveAsync(expected, CancellationToken.None);
		var actual = store.Load();

		actual.ShouldBe(expected);
	}

	[Test]
	public void Load_ignores_unusable_dimensions()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var path = Path.Combine(root, "window-layout.json");
		Directory.CreateDirectory(root);
		File.WriteAllText(
			path,
			JsonSerializer.Serialize(
				new AppWindowLayout(Left: 10, Top: 10, Width: 100, Height: 100, WindowState: "normal", LeftColumnWidth: 20, RightColumnWidth: 20),
				SettingsFileStore.JsonOptions));
		WindowLayoutStore store = new(path);

		var layout = store.Load();

		layout.ShouldBeNull();
	}

	[Test]
	public void Load_returns_null_when_file_is_corrupt()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var path = Path.Combine(root, "window-layout.json");
		Directory.CreateDirectory(root);
		File.WriteAllText(path, "{not json");
		WindowLayoutStore store = new(path);

		var layout = store.Load();

		layout.ShouldBeNull();
	}
}
