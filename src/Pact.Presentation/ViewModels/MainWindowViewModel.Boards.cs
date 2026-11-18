using Pact.Core.Boards;
using Pact.Core.Projects;
using Pact.Presentation.Services;

namespace Pact.Presentation.ViewModels;

public sealed partial class MainWindowViewModel
{
	private readonly IProjectBoardStore? _boardStore;
	private readonly Dictionary<string, ProjectBoardWorkspace> _boards = new(StringComparer.Ordinal);

	/// <summary>Whether durable board persistence was configured by the application host.</summary>
	public bool HasBoardStore => _boardStore is not null;

	/// <summary>Flushes all local card text before shutdown admission or changing project ownership.</summary>
	public async Task FlushAllBoardsAsync(CancellationToken cancellationToken)
	{
		foreach (var board in _boards.Values.ToArray())
		{
			await board.FlushAsync(cancellationToken);
		}
	}

	/// <summary>Disposes cached board owners after shutdown has drained their operations.</summary>
	public void DisposeBoards()
	{
		foreach (var board in _boards.Values)
		{
			board.Dispose();
		}
		_boards.Clear();
	}

	/// <summary>Returns the shared owner for a normalized project root, even before its Board tab opens.</summary>
	public ProjectBoardWorkspace GetOrCreateProjectBoard(string projectRootPath)
	{
		var key = ProjectNoteFileKey.FromRootPath(projectRootPath);
		if (!_boards.TryGetValue(key, out var board))
		{
			board = new ProjectBoardWorkspace(_boardStore
				?? throw new NotSupportedException("Board storage is not configured."), projectRootPath);
			_boards.Add(key, board);
		}
		return board;
	}
}
