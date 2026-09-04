using Pact.Core.Projects;
using Pact.Core.Prompting;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.Services;

public sealed class SelectionActionRouterTests
{
	[Test]
	public void BuildText_RawChoice_ReturnsSelectionVerbatim()
	{
		var router = CreateRouter();
		router.BuildText(SelectionActionChoiceViewModel.Raw, "selected", null).ShouldBe("selected");
	}

	[Test]
	public void BuildText_TemplateChoice_SubstitutesSelectedText()
	{
		var router = CreateRouter();
		PromptTemplateRecord template = new("id", "Quote", "Review: {selectedText}", false);
		router.BuildText(SelectionActionChoiceViewModel.ForTemplate(template), "selected", null).ShouldBe("Review: selected");
	}

	private static SelectionActionRouter CreateRouter()
	{
		MainWindowViewModel viewModel = new(new EmptyProjectStore(), new EmptyNotesStore());
		return new SelectionActionRouter(viewModel, new PromptTemplateRenderer(), (_, _, _, _) => Task.CompletedTask);
	}

	private sealed class EmptyProjectStore : IProjectStore
	{
		private ProjectsDocument _document = new(1, []);
		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_document);
		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken) { _document = document; return Task.CompletedTask; }
		public Task<ProjectsDocument> UpdateAsync(Func<ProjectsDocument, ProjectsDocument> update, CancellationToken cancellationToken)
		{ _document = update(_document); return Task.FromResult(_document); }
	}

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
		public Task SaveAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task AppendAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
	}
}