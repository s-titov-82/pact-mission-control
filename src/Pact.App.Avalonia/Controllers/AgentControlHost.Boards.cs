using Pact.Core.Boards;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

internal sealed partial class AgentControlHost
{
	public async Task<BoardSnapshot> ReadProjectBoardAsync(string projectId, CancellationToken cancellationToken)
	{
		BoardSnapshot? snapshot = null;
		await _uiDispatcher.InvokeAsync(async () =>
		{
			var project = FindBoardOwner(projectId);
			snapshot = await _viewModel.GetOrCreateProjectBoard(project.RootPath).LoadAsync(cancellationToken);
		});
		return snapshot!;
	}

	public async Task<BoardMutationResult> MutateProjectBoardAsync(string projectId, BoardCommand command, BoardActor actor, CancellationToken cancellationToken)
	{
		BoardMutationResult? result = null;
		await _uiDispatcher.InvokeAsync(async () =>
		{
			var project = FindBoardOwner(projectId);
			var session = project.Sessions.FirstOrDefault(session => session.Record.Id == actor.SessionId)
				?? throw new KeyNotFoundException("The calling session no longer belongs to this project.");
			if (command is UpdateCard { Patch.AssignedSessionId: { } assignment } update)
			{
				var target = assignment.Value is null ? null : project.Sessions.FirstOrDefault(candidate => candidate.Record.Id == assignment.Value)
					?? throw new ArgumentException("The assigned session must belong to this project.");
				command = update with { Patch = update.Patch with { AssignedSessionName = new(target?.Title) } };
			}
			result = await _viewModel.GetOrCreateProjectBoard(project.RootPath).ExecuteAsync(command, new(session.Record.Id, session.Title), cancellationToken);
		});
		return result!;
	}

	private WorkspaceViewModel FindBoardOwner(string projectId) => _viewModel.Workspaces.FirstOrDefault(project => project.Id == projectId)
		?? throw new KeyNotFoundException("The project is not running.");
}
