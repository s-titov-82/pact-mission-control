using System.Collections.Concurrent;
using Pact.Infrastructure.Remarks;

namespace Pact.Presentation.Services.Remarks;

/// <summary>Hands out one <see cref="RemarkBook"/> per project root, shared by the UI and the agent tools.</summary>
public sealed class RemarkBooks : IDisposable
{
	private readonly ConcurrentDictionary<string, RemarkBook> _books = new(StringComparer.OrdinalIgnoreCase);
	private readonly TimeProvider _time;

	/// <summary>Creates the registry.</summary>
	public RemarkBooks(TimeProvider time)
	{
		_time = time ?? throw new ArgumentNullException(nameof(time));
	}

	/// <summary>The book of a project root.</summary>
	public RemarkBook For(string projectRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		var root = Path.GetFullPath(projectRoot);
		return _books.GetOrAdd(root, key => new RemarkBook(key, new RemarkFileStore(key), _time));
	}

	/// <inheritdoc />
	public void Dispose()
	{
		foreach (var book in _books.Values)
		{
			book.Dispose();
		}

		_books.Clear();
	}
}
