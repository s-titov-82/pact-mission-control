using Pact.Infrastructure.Remarks;

namespace Pact.App.Avalonia.Tests.Views;

/// <summary>
/// Passes through to a real store, except that a held snapshot save waits until released, so a
/// test can act while a remark is being added.
/// </summary>
internal sealed class GatedRemarkStore(IRemarkStore inner) : IRemarkStore
{
	private TaskCompletionSource? _gate;
	private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Completes when a held save has started.</summary>
	public Task Entered => _entered.Task;

	/// <summary>Makes the next snapshot saves wait for <see cref="Release"/>.</summary>
	public void Hold()
	{
		_gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	public void Release() => _gate?.TrySetResult();

	public Task<RemarkFile> LoadAsync(CancellationToken cancellationToken) => inner.LoadAsync(cancellationToken);

	public Task SaveAsync(RemarkFile file, CancellationToken cancellationToken) => inner.SaveAsync(file, cancellationToken);

	public async Task<string> SaveSnapshotAsync(string text, CancellationToken cancellationToken)
	{
		_entered.TrySetResult();
		if (_gate is { } gate)
		{
			await gate.Task.ConfigureAwait(true);
		}

		return await inner.SaveSnapshotAsync(text, cancellationToken).ConfigureAwait(true);
	}

	public Task<IReadOnlyList<string>?> LoadSnapshotLinesAsync(string id, CancellationToken cancellationToken) =>
		inner.LoadSnapshotLinesAsync(id, cancellationToken);
}
