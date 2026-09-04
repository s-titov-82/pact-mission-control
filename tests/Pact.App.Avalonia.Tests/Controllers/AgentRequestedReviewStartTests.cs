using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Avalonia.Headless.NUnit;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.Core.AgentControl;
using Pact.Core.Agents;
using Pact.Core.Platform;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Core.Workspaces;
using Pact.Infrastructure.Storage;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Controllers;

[SuppressMessage(
	"Reliability",
	"CA2000:Dispose objects before losing scope",
	Justification = "Backend ownership is transferred to the shell controller and disposed with it.")]
public sealed class AgentRequestedReviewStartTests
{
	[AvaloniaTest]
	public async Task Reviewer_that_never_becomes_ready_is_rolled_back()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = Path.Combine(temporaryDirectory.Path, "project");
		Directory.CreateDirectory(root);
		var now = DateTimeOffset.UtcNow;
		SessionRecord author = new(
			"author-session",
			AgentKind.Codex,
			"Author",
			root,
			"codex",
			null,
			SessionStatus.Running,
			now,
			now);
		ProjectRecord project = new(
			"project-1",
			"Project",
			root,
			now,
			now,
			Notes: null)
		{
			Status = WorkspaceStatus.Active,
			ActiveItemId = author.Id,
			Sessions = [author]
		};
		MainWindowViewModel viewModel = new(
			new InMemoryProjectStore(new ProjectsDocument(1, [project])),
			new EmptyNotesStore());
		AppPaths paths = new(temporaryDirectory.Path);
		Directory.CreateDirectory(paths.SettingsDirectory);
		ReviewProfile profile = new(
			"reviewer-profile",
			"Reviewer",
			AgentKind.Claude,
			"claude");
		await File.WriteAllTextAsync(
			paths.ReviewProfilesPath,
			JsonSerializer.Serialize(new[] { profile }, SettingsFileStore.JsonOptions));
		var definition = ScenarioDefinitionStore.LoadDefaultDefinitions()[0];
		ScenarioDefinitionStore scenarioStore =
			new(paths.ScenariosPath, paths.AtomicTempDirectory);
		await scenarioStore.SaveAsync([definition], CancellationToken.None);

		FakeTerminalBackend authorBackend = new();
		FakeTerminalBackend reviewerBackend = new();
		Queue<FakeTerminalBackend> backends = new([authorBackend, reviewerBackend]);
		FakeTerminalWebViewHost host = new();
		SettingsFileStore settings = new(paths);
		await using ShellControllerTestBuilder builder = new(
			viewModel,
			settings,
			paths,
			host,
			backends.Dequeue);
		await using var controller = builder
			.WithExecutableLocator(new FakeExecutableLocator())
			.Build();
		await controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		var start = controller.StartAgentRequestedReviewAsync(
			project.Id,
			author.Id,
			new RequestReviewRequest(
				definition.Id,
				profile.Id,
				"HEAD",
				MaxIterations: 1),
			TestContext.CurrentContext.CancellationToken);
		await reviewerBackend.StartStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var reviewer = viewModel.Workspaces
			.Single(workspace => workspace.Id == project.Id)
			.Sessions
			.Single(session => session.Title.StartsWith("Review ·", StringComparison.Ordinal));
		reviewerBackend.EmitOutput("reviewer starting");
		await reviewerBackend.FirstOutputProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
		host.RaiseScreenSnapshotReceived(
			reviewer.Record.Id,
			"Some options selectors\nEnter to select\n╭──╮\n│ > │\n╰──╯",
			stable: true);

		var outcome = await start.WaitAsync(TimeSpan.FromSeconds(5));

		outcome.RunId.ShouldBeNull();
		outcome.FailureMessage.ShouldNotBeNull()
			.ShouldContain("could not accept the review");
		viewModel.Workspaces
			.Single(workspace => workspace.Id == project.Id)
			.Sessions
			.ShouldNotContain(session =>
				session.Title.StartsWith("Review ·", StringComparison.Ordinal));
	}

	private sealed class FakeExecutableLocator : IExecutableLocator
	{
		public string? FindOnPath(string executableName) => $@"C:\bin\{executableName}.exe";
	}

	private sealed class InMemoryProjectStore(ProjectsDocument document) : IProjectStore
	{
		private ProjectsDocument _document = document;

		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) =>
			Task.FromResult(_document);

		public Task SaveAsync(
			ProjectsDocument document,
			CancellationToken cancellationToken)
		{
			_document = document;
			return Task.CompletedTask;
		}

		public Task<ProjectsDocument> UpdateAsync(
			Func<ProjectsDocument, ProjectsDocument> update,
			CancellationToken cancellationToken)
		{
			_document = update(_document);
			return Task.FromResult(_document);
		}
	}

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(
			string projectRootPath,
			CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);

		public Task SaveAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task AppendAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}
}
