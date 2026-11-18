using Pact.Core.Boards;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Services;

/// <summary>One retained project owner, shared by MCP and the board view.</summary>
public sealed class ProjectBoardWorkspace(IProjectBoardStore store, string rootPath) : IDisposable
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly List<BoardCardEditorViewModel> _editors = [];
	/// <summary>Last disk-confirmed snapshot.</summary>
	public BoardSnapshot? Snapshot { get; private set; }
	/// <summary>Raised after a load or committed change.</summary>
	public event EventHandler<BoardSnapshot>? SnapshotChanged;
	/// <summary>Reads fresh disk data.</summary>
	public async Task<BoardSnapshot> LoadAsync(CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			Publish(await store.LoadAsync(rootPath, cancellationToken));
			return Snapshot!;
		}
		finally { _gate.Release(); }
	}
	/// <summary>Applies an explicit UI or agent command.</summary>
	public async Task<BoardMutationResult> ExecuteAsync(BoardCommand command, BoardActor actor, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var result = await store.ExecuteAsync(rootPath, command, actor, cancellationToken);
			Publish(result.Snapshot);
			return result;
		}
		finally { _gate.Release(); }
	}

	/// <summary>Flushes retained editors before leaving or shutting down; unresolved text blocks departure.</summary>
	public async Task FlushAsync(CancellationToken cancellationToken, bool requireClean = true)
	{
		foreach (var editor in _editors.ToArray())
		{
			await editor.FlushAsync(cancellationToken);
			if (requireClean && editor.SaveStatus.State != DocumentSaveState.Clean)
			{
				throw new InvalidOperationException("The board has unsaved edits. Resolve the card's save error or conflict first.");
			}
		}
	}

	internal void Register(BoardCardEditorViewModel editor) => _editors.Add(editor);
	internal void Unregister(BoardCardEditorViewModel editor) => _editors.Remove(editor);

	/// <summary>Releases the owner after admission and pending work have been drained.</summary>
	public void Dispose()
	{
		foreach (var editor in _editors.ToArray())
		{ editor.Dispose(); }
		_gate.Dispose();
	}

	private void Publish(BoardSnapshot snapshot)
	{
		Snapshot = snapshot;
		SnapshotChanged?.Invoke(this, snapshot);
	}
}
