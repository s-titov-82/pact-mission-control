namespace Pact.Core.Terminal;

/// <summary>
/// A live pseudo-console hosting one child process. One backend instance serves exactly one
/// session for its lifetime; it is not reusable after <see cref="StopAsync"/>.
/// </summary>
public interface ITerminalBackend : IAsyncDisposable
{
	/// <summary>
	/// Launches the child process and attaches the pseudo-console.
	/// </summary>
	/// <exception cref="InvalidOperationException">A process was already started.</exception>
	Task<TerminalSession> StartAsync(TerminalStartOptions options, CancellationToken cancellationToken);

	/// <summary>
	/// Writes raw bytes to the child's input. Callers pass encoded terminal input — including
	/// VT or win32-input-mode key events — so no encoding or newline translation happens here.
	/// </summary>
	Task WriteAsync(byte[] input, CancellationToken cancellationToken);

	/// <summary>
	/// Resizes the pseudo-console, prompting the child to repaint at the new dimensions.
	/// </summary>
	Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken);

	/// <summary>
	/// Streams raw output chunks until the child exits or the token is cancelled. Chunks are
	/// arbitrary byte boundaries and may split multi-byte characters or escape sequences, so
	/// consumers must buffer rather than decode each chunk independently.
	/// </summary>
	IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Ends the child process and releases the pseudo-console. Safe to call when the process
	/// has already exited.
	/// </summary>
	Task StopAsync(CancellationToken cancellationToken);
}