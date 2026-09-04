using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Tests.Controllers;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.App.Avalonia.Views;
using Pact.Core.Projects;
using Pact.Infrastructure.Storage;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class MainWindowLayoutHeadlessTests
{
	[AvaloniaTest]
	public async Task Window_applies_stored_layout_on_construction()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		try
		{
			WindowLayoutStore store = new(Path.Combine(root, "window-layout.json"));
			await store.SaveAsync(
				new AppWindowLayout(
					Left: 40,
					Top: 30,
					Width: 1500,
					Height: 850,
					WindowState: "normal",
					LeftColumnWidth: 340,
					RightColumnWidth: 320),
				CancellationToken.None);
			await using ControllerFixture fixture = new(root);

			using MainWindow window = new(fixture.Controller, windowLayoutStore: store);

			window.Position.ShouldBe(new PixelPoint(40, 30));
			window.Width.ShouldBe(1500);
			window.Height.ShouldBe(850);
			var rootGrid = window.FindControl<Grid>("RootGrid")!;
			rootGrid.ColumnDefinitions[0].Width.Value.ShouldBe(340);
			rootGrid.ColumnDefinitions[4].Width.Value.ShouldBe(320);
			window.WindowState.ShouldBe(WindowState.Normal);
		}
		finally
		{
			DeleteRoot(root);
		}
	}

	[AvaloniaTest]
	public async Task Window_restores_maximized_state()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		try
		{
			WindowLayoutStore store = new(Path.Combine(root, "window-layout.json"));
			await store.SaveAsync(
				new AppWindowLayout(40, 30, 1500, 850, "maximized", 340, 320),
				CancellationToken.None);
			await using ControllerFixture fixture = new(root);

			using MainWindow window = new(fixture.Controller, windowLayoutStore: store);

			window.WindowState.ShouldBe(WindowState.Maximized);
		}
		finally
		{
			DeleteRoot(root);
		}
	}

	[AvaloniaTest]
	public async Task CaptureWindowLayout_round_trips_the_applied_layout()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		try
		{
			WindowLayoutStore store = new(Path.Combine(root, "window-layout.json"));
			AppWindowLayout expected = new(
				Left: 40,
				Top: 30,
				Width: 1500,
				Height: 850,
				WindowState: "normal",
				LeftColumnWidth: 340,
				RightColumnWidth: 320);
			await store.SaveAsync(expected, CancellationToken.None);
			await using ControllerFixture fixture = new(root);
			using MainWindow window = new(fixture.Controller, windowLayoutStore: store);

			var captured = window.CaptureWindowLayout();

			captured.ShouldBe(expected);
		}
		finally
		{
			DeleteRoot(root);
		}
	}

	[AvaloniaTest]
	public async Task Window_ignores_stored_position_from_a_detached_monitor()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		try
		{
			WindowLayoutStore store = new(Path.Combine(root, "window-layout.json"));
			await store.SaveAsync(
				new AppWindowLayout(99999, 99999, 1500, 850, "normal", 340, 320),
				CancellationToken.None);
			await using ControllerFixture fixture = new(root);

			using MainWindow window = new(fixture.Controller, windowLayoutStore: store);

			window.Position.ShouldNotBe(new PixelPoint(99999, 99999));
			window.Width.ShouldBe(1500);
			window.Height.ShouldBe(850);
		}
		finally
		{
			DeleteRoot(root);
		}
	}

	[AvaloniaTest]
	public async Task CaptureWindowLayout_keeps_last_normal_geometry_when_window_is_maximized()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		try
		{
			WindowLayoutStore store = new(Path.Combine(root, "window-layout.json"));
			await store.SaveAsync(
				new AppWindowLayout(40, 30, 1500, 850, "normal", 340, 320),
				CancellationToken.None);
			await using ControllerFixture fixture = new(root);
			using MainWindow window = new(fixture.Controller, windowLayoutStore: store)
			{
				WindowState = WindowState.Maximized
			};
			var captured = window.CaptureWindowLayout();

			captured.ShouldBe(new AppWindowLayout(40, 30, 1500, 850, "maximized", 340, 320));
		}
		finally
		{
			DeleteRoot(root);
		}
	}

	private static void DeleteRoot(string root)
	{
		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private sealed class ControllerFixture : IAsyncDisposable
	{
		private readonly ShellControllerTestBuilder _builder;

		public ControllerFixture(string root)
		{
			AppPaths paths = new(root);
			MainWindowViewModel viewModel = new(new EmptyProjectStore(), new EmptyNotesStore());
			_builder = new ShellControllerTestBuilder(
				viewModel,
				new SettingsFileStore(paths),
				paths,
				new FakeTerminalWebViewHost(),
				() => new FakeTerminalBackend());
			Controller = _builder.Build();
		}

		public AvaloniaMainShellController Controller { get; }

		public async ValueTask DisposeAsync()
		{
			await Controller.DisposeAsync();
			await _builder.DisposeAsync();
		}
	}

	private sealed class EmptyProjectStore : IProjectStore
	{
		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) =>
			Task.FromResult(ProjectsDocument.CreateDefault());
		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<ProjectsDocument> UpdateAsync(Func<ProjectsDocument, ProjectsDocument> update, CancellationToken cancellationToken) =>
			Task.FromResult(update(ProjectsDocument.CreateDefault()));
	}

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
		public Task SaveAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task AppendAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
