using System.ComponentModel;

namespace Pact.Presentation.ViewModels;

/// <summary>A bounded history picker whose accepted revision always resolves to a full commit id.</summary>
public sealed class GitCommitPickerViewModel(string projectRoot, IGitCommitReader reader) : INotifyPropertyChanged, IDisposable
{
	private const int PageSize = 50;
	private bool _disposed;
	private int _version;
	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;
	/// <summary>History loaded from HEAD, newest first.</summary>
	public IReadOnlyList<GitCommitInfo> Commits { get; private set; } = [];
	/// <summary>The selected history row, used when no explicit revision was entered.</summary>
	public GitCommitInfo? SelectedCommit { get; set; }
	/// <summary>An optional SHA or ref to resolve at acceptance time.</summary>
	public string Revision { get; set; } = string.Empty;
	/// <summary>Whether a history or resolution request is active.</summary>
	public bool IsLoading { get; private set; }
	/// <summary>Whether another bounded history page may exist.</summary>
	public bool HasMore { get; private set; } = true;
	/// <summary>A failed Git request, without discarding already loaded history.</summary>
	public string? Error { get; private set; }
	/// <summary>Appends at most fifty older commits.</summary>
	public async Task LoadMoreAsync(CancellationToken cancellationToken)
	{
		if (_disposed || IsLoading || !HasMore)
		{ return; }
		var version = ++_version;
		IsLoading = true;
		Error = null;
		Raise();
		try
		{
			var page = await reader.ReadCommitsAsync(projectRoot, Commits.Count, PageSize, cancellationToken).ConfigureAwait(true);
			if (_disposed || version != _version || cancellationToken.IsCancellationRequested)
			{ return; }
			Commits = [.. Commits, .. page];
			HasMore = page.Count == PageSize;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			if (!_disposed && version == _version)
			{ Error = exception.Message; }
		}
		finally
		{
			if (!_disposed && version == _version)
			{ IsLoading = false; Raise(); }
		}
	}
	/// <summary>Resolves the entered ref or selected row to immutable commit metadata.</summary>
	public async Task<GitCommitInfo?> AcceptAsync(CancellationToken cancellationToken)
	{
		if (_disposed || IsLoading)
		{ return null; }
		var revision = string.IsNullOrWhiteSpace(Revision) ? SelectedCommit?.Id : Revision.Trim();
		if (revision is null)
		{ Error = "Select a commit or enter a SHA/ref."; Raise(); return null; }
		var version = ++_version;
		IsLoading = true;
		Error = null;
		Raise();
		try
		{
			var commit = await reader.ResolveCommitAsync(projectRoot, revision, cancellationToken).ConfigureAwait(true);
			if (_disposed || version != _version || cancellationToken.IsCancellationRequested)
			{ return null; }
			if (commit is null)
			{ Error = $"Commit not found: {revision}"; }
			return commit;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			if (!_disposed && version == _version)
			{ Error = exception.Message; }
			return null;
		}
		finally
		{
			if (!_disposed && version == _version)
			{ IsLoading = false; Raise(); }
		}
	}
	/// <inheritdoc />
	public void Dispose() { _disposed = true; _version++; }
	private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}