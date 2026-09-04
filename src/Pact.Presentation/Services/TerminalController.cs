using System.Diagnostics;
using System.Text;
using Pact.Core.Terminal;
using Pact.Infrastructure.Terminal;

namespace Pact.Presentation.Services;

/// <summary>
/// Drives one terminal backend: starts the process, pumps its output as decoded text, and
/// forwards input and resizes.
/// </summary>
/// <remarks>
/// Output bytes are decoded with a stateful UTF-8 decoder, so a multi-byte character split
/// across backend chunks is reassembled instead of producing replacement characters.
/// </remarks>
public sealed class TerminalController : IAsyncDisposable
{
	private readonly ITerminalBackend _backend;
	private readonly CancellationTokenSource _stop = new();
	private readonly Decoder _outputDecoder = Encoding.UTF8.GetDecoder();
	private readonly SemaphoreSlim _resizeGate = new(1, 1);

	private Task? _pumpTask;
	private long _resizeRequestVersion;
	private int _columns;
	private int _rows;
	private bool _started;
	private bool _stopping;
	private bool _disposed;

	/// <summary>Creates a controller over a new ConPTY backend.</summary>
	public TerminalController()
	{
		_backend = new ConPtyTerminalBackend();
	}

	/// <summary>
	/// Creates a controller over <paramref name="backend"/>, whose lifetime it takes over.
	/// </summary>
	public TerminalController(ITerminalBackend backend)
	{
		ArgumentNullException.ThrowIfNull(backend);

		_backend = backend;
	}

	/// <summary>Raised with decoded output text as it arrives from the backend.</summary>
	public event EventHandler<string>? OutputReceived;

	/// <summary>Raised once when the child process ends, however it ended.</summary>
	public event EventHandler? Exited;

	/// <summary>
	/// Runs immediately before a valid non-empty input is offered to the backend. Every
	/// subscriber is awaited in registration order, so terminal-display state is prepared
	/// before output can race back.
	/// </summary>
	public event Func<string, Task>? InputWriting;

	/// <summary>Raised after input was accepted by the backend.</summary>
	public event EventHandler<string>? InputWritten;

	/// <summary>Raised when the pseudo-console has been resized.</summary>
	public event EventHandler<TerminalViewportChangedEventArgs>? ViewportChanged;

	/// <summary>
	/// Whether the controller can still accept input: started, not stopping, and not disposed.
	/// </summary>
	public bool IsActive => !_disposed && _started && !_stopping && !_stop.IsCancellationRequested;

	/// <summary>Gets the process id reported by the started terminal backend, when available.</summary>
	public int? ProcessId { get; private set; }

	/// <summary>Starts the process at the backend's default size.</summary>
	public async Task StartAsync(string commandLine, string workingDirectory) => await StartAsync(commandLine, workingDirectory, 120, 36).ConfigureAwait(false);

	/// <summary>
	/// Starts the process at a given size and begins pumping output.
	/// </summary>
	/// <exception cref="InvalidOperationException">The controller was already started.</exception>
	public async Task StartAsync(
		string commandLine,
		string workingDirectory,
		int columns,
		int rows) =>
		await StartAsync(
			commandLine,
			workingDirectory,
			columns,
			rows,
			environmentVariables: null).ConfigureAwait(false);

	/// <summary>
	/// Starts the process with launch-specific environment variables layered over inheritance.
	/// </summary>
	/// <exception cref="InvalidOperationException">The controller was already started.</exception>
	public async Task StartAsync(
		string commandLine,
		string workingDirectory,
		int columns,
		int rows,
		IReadOnlyDictionary<string, string>? environmentVariables)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		var session = await _backend.StartAsync(
			new TerminalStartOptions(
				commandLine,
				workingDirectory,
				columns,
				rows,
				environmentVariables),
			_stop.Token).ConfigureAwait(false);

		ProcessId = session.ProcessId;
		_columns = columns;
		_rows = rows;
		_started = true;
		_pumpTask = Task.Run(() => PumpOutputAsync(_stop.Token));
	}

	/// <summary>
	/// Writes input to the child process.
	/// </summary>
	/// <returns>
	/// <see langword="false"/> when the controller is not active or the input is empty, so
	/// callers can report the keystroke was ignored instead of assuming it landed.
	/// </returns>
	public async Task<bool> WriteInputAsync(string input)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (!IsActive || string.IsNullOrEmpty(input))
		{
			return false;
		}

		var bytes = Encoding.UTF8.GetBytes(input);
		await PrepareInputWriteAsync(input).ConfigureAwait(false);
		var written = await ObserveBackendActionAsync(
			_backend.WriteAsync(bytes, _stop.Token),
			"Terminal input write failed").ConfigureAwait(false);
		if (written)
		{
			InputWritten?.Invoke(this, input);
		}

		return written;
	}

	private async Task PrepareInputWriteAsync(string input)
	{
		var inputWriting = InputWriting;
		if (inputWriting is null)
		{
			return;
		}

		foreach (var subscriber in inputWriting.GetInvocationList())
		{
			var handler = (Func<string, Task>)subscriber;
			await handler(input).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Resizes the pseudo-console, raising <see cref="ViewportChanged"/> when the size actually
	/// changed. Repeating the current size is a no-op, avoiding a needless agent repaint.
	/// </summary>
	public async Task ResizeAsync(int columns, int rows)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (!IsActive)
		{
			return;
		}

		var requestVersion = Interlocked.Increment(ref _resizeRequestVersion);
		await _resizeGate.WaitAsync(_stop.Token).ConfigureAwait(false);
		try
		{
			if (requestVersion != Volatile.Read(ref _resizeRequestVersion)
				|| _columns == columns && _rows == rows)
			{
				return;
			}

			var resized = await ObserveBackendActionAsync(
				_backend.ResizeAsync(columns, rows, _stop.Token),
				"Terminal resize failed").ConfigureAwait(false);
			if (resized
				&& requestVersion == Volatile.Read(ref _resizeRequestVersion))
			{
				_columns = columns;
				_rows = rows;
				ViewportChanged?.Invoke(
					this,
					new TerminalViewportChangedEventArgs(columns, rows));
			}
		}
		finally
		{
			_resizeGate.Release();
		}
	}

	/// <summary>
	/// Stops the child process and ends the output pump. Safe to call more than once and when
	/// the process has already exited.
	/// </summary>
	public async Task StopAsync()
	{
		if (_disposed || _stopping)
		{
			return;
		}

		_stopping = true;

		await _resizeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
		_resizeGate.Release();
		await _backend.StopAsync(CancellationToken.None).ConfigureAwait(false);
		await ObservePumpAsync().ConfigureAwait(false);

		if (!_stop.IsCancellationRequested)
		{
			_stop.Cancel();
		}

		_started = false;
	}

	/// <summary>
	/// Stops the process if needed and disposes the backend. Safe to call more than once.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		try
		{
			await StopAsync().ConfigureAwait(false);
		}
		finally
		{
			try
			{
				await _backend.DisposeAsync().ConfigureAwait(false);
			}
			finally
			{
				_resizeGate.Dispose();
				_stop.Dispose();
				_disposed = true;
			}
		}
	}

	private async Task PumpOutputAsync(CancellationToken cancellationToken)
	{
		var exited = false;

		try
		{
			await foreach (var chunk in _backend.ReadOutputAsync(cancellationToken).ConfigureAwait(false))
			{
				var text = DecodeOutput(chunk);
				if (text.Length > 0)
				{
					OutputReceived?.Invoke(this, text);
				}
			}

			exited = !cancellationToken.IsCancellationRequested;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Terminal output pump failed: {ex}");
			exited = !cancellationToken.IsCancellationRequested;
		}
		finally
		{
			if (exited)
			{
				_started = false;
				Exited?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	private string DecodeOutput(byte[] bytes)
	{
		var charCount = _outputDecoder.GetCharCount(bytes, 0, bytes.Length, flush: false);
		var chars = new char[charCount];
		var charsWritten = _outputDecoder.GetChars(bytes, 0, bytes.Length, chars, 0, flush: false);
		return new string(chars, 0, charsWritten);
	}

	private async Task ObservePumpAsync()
	{
		var pumpTask = _pumpTask;
		if (pumpTask is null)
		{
			return;
		}

		try
		{
			await pumpTask.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (_stop.IsCancellationRequested)
		{
		}
		catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
		{
		}
	}

	private static async Task<bool> ObserveBackendActionAsync(Task action, string message)
	{
		try
		{
			await action.ConfigureAwait(false);
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (ObjectDisposedException)
		{
			return false;
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"{message}: {ex}");
			return false;
		}
	}
}
