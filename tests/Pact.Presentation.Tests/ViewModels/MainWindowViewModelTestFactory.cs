using Pact.Core.RootTabs;
using Pact.Core.Projects;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

internal static class MainWindowViewModelTestFactory
{
	public static MainWindowViewModel Create(
		IProjectStore projectStore,
		TerminalTabStatusCoordinator? terminalTabStatuses = null,
		IRootTabsStore? rootTabsStore = null) =>
		new(
			projectStore,
			NoOpProjectNotesStore.Instance,
			terminalTabStatuses ?? new TerminalTabStatusCoordinator(action => action()),
			rootTabsStore);

	private sealed class NoOpProjectNotesStore : IProjectNotesStore
	{
		public static NoOpProjectNotesStore Instance { get; } = new();

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

internal sealed class InMemoryRootTabsStore(RootTabsRecord record) : IRootTabsStore
{
	private RootTabsRecord _record = record;

	public Task<RootTabsRecord> LoadAsync(CancellationToken cancellationToken) =>
		Task.FromResult(_record);

	public Task SaveAsync(RootTabsRecord record, CancellationToken cancellationToken)
	{
		_record = record.Normalize();
		return Task.CompletedTask;
	}

	public Task<RootTabsRecord> UpdateAsync(
		Func<RootTabsRecord, RootTabsRecord> mutate,
		CancellationToken cancellationToken)
	{
		_record = mutate(_record).Normalize();
		return Task.FromResult(_record);
	}
}

internal sealed class TestProjectStore(ProjectsDocument document) : IProjectStore
{
	private ProjectsDocument _document = document;

	public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) =>
		Task.FromResult(_document);

	public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken)
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
