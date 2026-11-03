using Pact.Core.Projects;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Settings;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class ProjectLinksPersistenceTests
{
	[TestCase(false)]
	[TestCase(true)]
	public async Task Settings_save_racing_link_update_preserves_both_writes_on_disk(bool delayLink)
	{
		using var directory = TemporaryDirectory.Create();
		GatedStore store = new(new JsonProjectStore(directory.Path));
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(directory.Path, CancellationToken.None);
		store.DelayNextUpdate = true;
		Task first = delayLink
			? viewModel.AddProjectLinkAsync(workspace.Id, "MR 42", "https://example.com/42", "merge_request", CancellationToken.None)
			: viewModel.UpdateProjectSettingsAsync(workspace.Id, new ProjectSettingsEdit(Name: "Renamed", GitLabRepoId: "group/repo"), CancellationToken.None);
		await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		try
		{
			if (delayLink)
			{
				await viewModel.UpdateProjectSettingsAsync(workspace.Id, new ProjectSettingsEdit(Name: "Renamed", GitLabRepoId: "group/repo"), CancellationToken.None);
			}
			else
			{
				await viewModel.AddProjectLinkAsync(workspace.Id, "MR 42", "https://example.com/42", "merge_request", CancellationToken.None);
			}
		}
		finally
		{
			store.Release.TrySetResult();
		}

		await first;
		var saved = (await new JsonProjectStore(directory.Path).LoadAsync(CancellationToken.None)).Projects.Single();
		saved.Name.ShouldBe("Renamed");
		saved.GitLabRepoId.ShouldBe("group/repo");
		saved.Links.ShouldHaveSingleItem().Url.ShouldBe("https://example.com/42");
		workspace.Links.ShouldBe(saved.Links);
		workspace.Name.ShouldBe("Renamed");
		await viewModel.RemoveProjectLinkAsync(workspace.Id, "https://EXAMPLE.COM:443/42", CancellationToken.None);
		(await new JsonProjectStore(directory.Path).LoadAsync(CancellationToken.None)).Projects.Single().Links.ShouldBeEmpty();
	}

	[Test]
	public async Task Failed_save_does_not_publish_link_or_remove_existing_link()
	{
		using var directory = TemporaryDirectory.Create();
		GatedStore store = new(new JsonProjectStore(directory.Path));
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(directory.Path, CancellationToken.None);
		await viewModel.AddProjectLinkAsync(workspace.Id, "Issue", "https://example.com/issue", "issue", CancellationToken.None);
		store.FailUpdates = true;
		await Should.ThrowAsync<IOException>(() => viewModel.AddProjectLinkAsync(workspace.Id, "Build", "https://example.com/build", "ci", CancellationToken.None));
		await Should.ThrowAsync<IOException>(() => viewModel.RemoveProjectLinkAsync(workspace.Id, "https://example.com/issue", CancellationToken.None));
		workspace.Links.ShouldHaveSingleItem().Title.ShouldBe("Issue");
		(await store.LoadAsync(CancellationToken.None)).Projects.Single().Links.ShouldHaveSingleItem().Title.ShouldBe("Issue");
	}

	private sealed class GatedStore(IProjectStore inner) : IProjectStore
	{
		public bool DelayNextUpdate { get; set; }
		public bool FailUpdates { get; set; }
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) => inner.LoadAsync(cancellationToken);
		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken) => inner.SaveAsync(document, cancellationToken);
		public async Task<ProjectsDocument> UpdateAsync(Func<ProjectsDocument, ProjectsDocument> update, CancellationToken cancellationToken)
		{
			if (DelayNextUpdate)
			{
				DelayNextUpdate = false;
				Entered.TrySetResult();
				await Release.Task.WaitAsync(cancellationToken);
			}

			if (FailUpdates)
			{
				throw new IOException("Disk unavailable.");
			}

			return await inner.UpdateAsync(update, cancellationToken);
		}
	}
}
