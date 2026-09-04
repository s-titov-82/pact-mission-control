using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Platform;
using Pact.App.Avalonia.Tests.Controllers;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.App.Avalonia.Views;
using Pact.Core.Agents;
using Pact.Core.Platform;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class MainWindowAttentionHeadlessTests
{
	[AvaloniaTest]
	public async Task MainWindowUsesThePublicProductName()
	{
		await using SessionFixture fixture = new();
		using MainWindow window = new(fixture.Controller);

		window.Title.ShouldBe("PACT:> Mission Control");
	}

	[Test]
	[TestCase(false)]
	[TestCase(true)]
	public void Foreground_probe_fallback_does_not_call_native_api_when_handle_is_missing(bool isActive)
	{
		var getForegroundCalled = false;
		var getAncestorCalled = false;

		var result = WindowForegroundProbe.EvaluateWindowsForeground(
			platform: null,
			isActive,
			getForegroundWindow: () =>
			{
				getForegroundCalled = true;
				return new IntPtr(1);
			},
			getAncestor: (_, _) =>
			{
				getAncestorCalled = true;
				return new IntPtr(1);
			});

		result.ShouldBe(isActive);
		getForegroundCalled.ShouldBeFalse();
		getAncestorCalled.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task Main_window_wires_snapshot_refresh_to_current_window_facts()
	{
		await using SessionFixture fixture = new();
		using MainWindow window = new(fixture.Controller);

		fixture.Controller.RefreshWindowFacts.ShouldNotBeNull();
		GC.KeepAlive(window);
	}

	[AvaloniaTest]
	public void Terminal_window_visibility_excludes_hidden_and_minimized_windows()
	{
		MainWindow.IsTerminalWindowVisible(true, WindowState.Normal).ShouldBeTrue();
		MainWindow.IsTerminalWindowVisible(true, WindowState.Maximized).ShouldBeTrue();
		MainWindow.IsTerminalWindowVisible(false, WindowState.Normal).ShouldBeFalse();
		MainWindow.IsTerminalWindowVisible(true, WindowState.Minimized).ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task Unread_completion_flashes_taskbar_while_window_is_inactive()
	{
		await using SessionFixture fixture = new();
		FakeUserAttention attention = new();
		using MainWindow window = new(fixture.Controller, userAttention: attention);

		var now = DateTimeOffset.UtcNow;
		fixture.Statuses.OnLifecycleChanged("session-1", SessionStatus.Running, now);
		fixture.Statuses.OnUserInput("session-1", "\r", now.AddSeconds(1));
		fixture.Statuses.OnScreenSnapshot("session-1", @"PS D:\> ", now.AddSeconds(2));

		(attention.RequestCount > 0).ShouldBeTrue();
		GC.KeepAlive(window);
	}

	[AvaloniaTest]
	public async Task Clearing_last_unread_completion_stops_taskbar_attention()
	{
		await using SessionFixture fixture = new();
		FakeUserAttention attention = new();
		using MainWindow window = new(fixture.Controller, userAttention: attention);

		var now = DateTimeOffset.UtcNow;
		fixture.Statuses.OnLifecycleChanged("session-1", SessionStatus.Running, now);
		fixture.Statuses.OnUserInput("session-1", "\r", now.AddSeconds(1));
		fixture.Statuses.OnScreenSnapshot("session-1", @"PS D:\> ", now.AddSeconds(2));
		fixture.Controller.ViewModel.HasUnreadCompletions.ShouldBeTrue();
		var clearCountBefore = attention.ClearCount;

		fixture.Statuses.OnLifecycleChanged("session-1", SessionStatus.Stopped, now.AddSeconds(3));

		fixture.Controller.ViewModel.HasUnreadCompletions.ShouldBeFalse();
		attention.ClearCount.ShouldBeGreaterThan(clearCountBefore);
		GC.KeepAlive(window);
	}

	[AvaloniaTest]
	public async Task Session_going_busy_without_completion_does_not_flash()
	{
		await using SessionFixture fixture = new();
		FakeUserAttention attention = new();
		using MainWindow window = new(fixture.Controller, userAttention: attention);

		var now = DateTimeOffset.UtcNow;
		fixture.Statuses.OnLifecycleChanged("session-1", SessionStatus.Running, now);
		fixture.Statuses.OnUserInput("session-1", "\r", now.AddSeconds(1));

		attention.RequestCount.ShouldBe(0);
		GC.KeepAlive(window);
	}

	private sealed class FakeUserAttention : IUserAttention
	{
		public int RequestCount { get; private set; }
		public int ClearCount { get; private set; }
		public void RequestAttention() => RequestCount++;
		public void ClearAttention() => ClearCount++;
	}

	private sealed class SessionFixture : IAsyncDisposable
	{
		private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
		private readonly ShellControllerTestBuilder _builder;
		private string _root => _temporaryDirectory.Path;

		public SessionFixture()
		{
			var now = DateTimeOffset.UtcNow;
			ProjectRecord project = new("project-1", "Project", _root, now, now, null);
			SessionRecord session = new(
				"session-1",
				AgentKind.Pwsh,
				"PowerShell",
				_root,
				"pwsh",
				null,
				SessionStatus.Stopped,
				now,
				now);
			Statuses = new TerminalTabStatusCoordinator(action => action());
			ViewModel = new MainWindowViewModel(new EmptyProjectStore(), new EmptyNotesStore(), Statuses);
			WorkspaceViewModel workspace = new(project);
			SessionViewModel sessionViewModel = new(session, _root);
			workspace.Sessions.Add(sessionViewModel);
			ViewModel.Sessions.Add(sessionViewModel);
			ViewModel.Workspaces.Add(workspace);
			AppPaths paths = new(_root);
			_builder = new ShellControllerTestBuilder(
				ViewModel,
				new SettingsFileStore(paths),
				paths,
				new FakeTerminalWebViewHost(),
				() => new FakeTerminalBackend());
			Controller = _builder.Build();
		}

		public MainWindowViewModel ViewModel { get; }
		public TerminalTabStatusCoordinator Statuses { get; }
		public AvaloniaMainShellController Controller { get; }

		public async ValueTask DisposeAsync()
		{
			await Controller.DisposeAsync();
			await _builder.DisposeAsync();
			await _temporaryDirectory.DisposeAsync();
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
