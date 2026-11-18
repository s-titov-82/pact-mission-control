namespace Pact.Core.Boards;

/// <summary>Serializes read/validate/write transactions for each project board.</summary>
public interface IProjectBoardStore
{
	/// <summary>Reads the current disk state, without creating an absent file.</summary>
	Task<BoardSnapshot> LoadAsync(string projectRootPath, CancellationToken cancellationToken);
	/// <summary>Applies a command to freshly read data and publishes only a saved result.</summary>
	Task<BoardMutationResult> ExecuteAsync(string projectRootPath, BoardCommand command, BoardActor actor, CancellationToken cancellationToken);
}
