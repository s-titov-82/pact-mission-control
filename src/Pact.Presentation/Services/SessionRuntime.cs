using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Pact.Presentation.Services;

/// <summary>
/// Live, non-persisted state for one terminal session: its controller, event handler
/// registrations, and the stream-scoped helpers that must follow that session's output.
/// </summary>
/// <param name="sessionId">Session this runtime belongs to.</param>
public sealed class SessionRuntime(string sessionId)
{
	private const int RecentOutputMaxChars = 32_768;
	private readonly Lock _gate = new();
	private readonly StringBuilder _recentOutput = new();
	private TaskCompletionSource<bool>? _initialOutputCompletionSource;
	private TerminalController? _controller;
	private EventHandler<string>? _outputHandler;
	private EventHandler? _exitedHandler;
	private Func<string, Task>? _inputWritingHandler;
	private EventHandler<string>? _inputWrittenHandler;
	private EventHandler<TerminalViewportChangedEventArgs>? _viewportChangedHandler;

	/// <summary>Session this runtime belongs to.</summary>
	public string SessionId { get; } = sessionId;

	/// <summary>
	/// Atomically replaces the attached controller and its event registrations. A later
	/// conditional detach of an older controller cannot clear this replacement. The caller
	/// remains responsible for disposing the returned prior controller.
	/// </summary>
	public TerminalController? AttachController(
		TerminalController controller,
		EventHandler<string>? outputHandler = null,
		EventHandler? exitedHandler = null,
		Func<string, Task>? inputWritingHandler = null,
		EventHandler<string>? inputWrittenHandler = null,
		EventHandler<TerminalViewportChangedEventArgs>? viewportChangedHandler = null)
	{
		ArgumentNullException.ThrowIfNull(controller);
		lock (_gate)
		{
			var priorController = _controller;
			DetachHandlers();
			_controller = controller;
			_outputHandler = outputHandler;
			_exitedHandler = exitedHandler;
			_inputWritingHandler = inputWritingHandler;
			_inputWrittenHandler = inputWrittenHandler;
			_viewportChangedHandler = viewportChangedHandler;
			AttachHandlers();
			return priorController;
		}
	}

	/// <summary>Returns the controller currently attached to this session.</summary>
	public bool TryGetController([NotNullWhen(true)] out TerminalController? controller)
	{
		lock (_gate)
		{
			controller = _controller;
			return controller is not null;
		}
	}

	/// <summary>
	/// Detaches <paramref name="controller"/> only when it is still the current attachment.
	/// </summary>
	public bool DetachControllerIfSame(TerminalController controller)
	{
		ArgumentNullException.ThrowIfNull(controller);
		lock (_gate)
		{
			if (!ReferenceEquals(_controller, controller))
			{
				return false;
			}

			DetachHandlers();
			_controller = null;
			return true;
		}
	}

	/// <summary>Atomically detaches and returns the current controller, if any.</summary>
	public TerminalController? DetachController()
	{
		lock (_gate)
		{
			var controller = _controller;
			DetachHandlers();
			_controller = null;
			return controller;
		}
	}

	/// <summary>
	/// Whether the "input ignored" notice has already been shown, so a locked session warns the
	/// user once rather than on every keystroke.
	/// </summary>
	public bool InputIgnoredNoticeShown { get; set; }

	/// <summary>
	/// Filter for this session's output stream. Stateful across chunks, so it must not be shared
	/// with another session.
	/// </summary>
	public TerminalDisplayOutputFilter DisplayOutputFilter { get; } = new();

	/// <summary>
	/// Tracks whether this session's client enabled win32-input-mode, which changes how Enter
	/// and Esc must be encoded.
	/// </summary>
	public Win32InputModeTracker Win32InputMode { get; } = new();

	/// <summary>
	/// Completes once the session's first output has been rendered, letting startup wait for a
	/// painted terminal. <see langword="null"/> before <see cref="PrepareForControllerStart"/>.
	/// </summary>
	public Task? InitialOutputTask
	{
		get
		{
			lock (_gate)
			{
				return _initialOutputCompletionSource?.Task;
			}
		}
	}

	/// <summary>
	/// Arms <see cref="InitialOutputTask"/> for a launch. Call once per start; a restart arms a
	/// fresh task so the previous launch's completion is not reused.
	/// </summary>
	public void PrepareForControllerStart()
	{
		lock (_gate)
		{
			_initialOutputCompletionSource =
				new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		}
	}

	/// <summary>
	/// Signals that output has been rendered. Safe to call repeatedly and before arming.
	/// </summary>
	public void NotifyOutputRendered()
	{
		TaskCompletionSource<bool>? completionSource;
		lock (_gate)
		{
			completionSource = _initialOutputCompletionSource;
		}

		completionSource?.TrySetResult(true);
	}

	/// <summary>
	/// Appends to the rolling output buffer used for resume-command extraction, discarding the
	/// oldest text past the cap so a long-running session cannot grow it without bound.
	/// </summary>
	public void AppendRecentOutput(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return;
		}

		lock (_gate)
		{
			_recentOutput.Append(text);
			if (_recentOutput.Length > RecentOutputMaxChars)
			{
				_recentOutput.Remove(0, _recentOutput.Length - RecentOutputMaxChars);
			}
		}
	}

	/// <summary>Returns the buffered recent output.</summary>
	public string GetRecentOutput()
	{
		lock (_gate)
		{
			return _recentOutput.ToString();
		}
	}

	/// <summary>Clears the buffered recent output.</summary>
	public void ClearRecentOutput()
	{
		lock (_gate)
		{
			_recentOutput.Clear();
		}
	}

	private void AttachHandlers()
	{
		if (_controller is null)
		{
			return;
		}

		_controller.OutputReceived += _outputHandler;
		_controller.Exited += _exitedHandler;
		_controller.InputWriting += _inputWritingHandler;
		_controller.InputWritten += _inputWrittenHandler;
		_controller.ViewportChanged += _viewportChangedHandler;
	}

	private void DetachHandlers()
	{
		if (_controller is not null)
		{
			_controller.OutputReceived -= _outputHandler;
			_controller.Exited -= _exitedHandler;
			_controller.InputWriting -= _inputWritingHandler;
			_controller.InputWritten -= _inputWrittenHandler;
			_controller.ViewportChanged -= _viewportChangedHandler;
		}

		_outputHandler = null;
		_exitedHandler = null;
		_inputWritingHandler = null;
		_inputWrittenHandler = null;
		_viewportChangedHandler = null;
	}
}
