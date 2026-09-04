using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pact.Core.Terminal;

namespace Pact.Infrastructure.Terminal;

/// <summary>
/// Windows ConPTY terminal backend, built on the vendored OpenConsole in
/// <c>third_party/conpty</c> rather than the inbox ConPTY.
/// </summary>
/// <remarks>
/// The bundled ConPTY is required, not a preference: the inbox ConPTY of Windows 11 22H2 does
/// not translate Win32 <c>ENABLE_MOUSE_INPUT</c> into VT mouse sequences, so agents that enable
/// the mouse that way (Codex) lose wheel scrolling. There is deliberately no kernel32 fallback —
/// a missing vendored library fails loudly instead of silently degrading.
/// </remarks>
public sealed class ConPtyTerminalBackend : ITerminalBackend
{
	private const int PipeBufferSize = 8192;
	private const uint StopWaitMilliseconds = 2000;
	private const uint TerminateWaitMilliseconds = 500;

	private readonly SemaphoreSlim _lifetimeLock = new(1, 1);
	private readonly SemaphoreSlim _writeLock = new(1, 1);

	private FileStream? _inputWriter;
	private FileStream? _outputReader;
	private IntPtr _pseudoConsole;
	private IntPtr _processHandle;
	private IntPtr _threadHandle;
	private int _processId;
	private bool _started;
	private bool _stopping;
	private bool _disposed;

	/// <inheritdoc />
	/// <exception cref="Win32Exception">The pseudo-console or child process could not be created.</exception>
	public async Task<TerminalSession> StartAsync(TerminalStartOptions options, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);

		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException("ConPTY is only available on Windows.");
		}

		ValidateDimensions(options.Columns, options.Rows);
		ValidateCommandLine(options.CommandLine);

		await _lifetimeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ThrowIfDisposed();

			if (_started)
			{
				throw new InvalidOperationException("The terminal backend has already started a session.");
			}

			return StartCore(options);
		}
		finally
		{
			_lifetimeLock.Release();
		}
	}

	/// <inheritdoc />
	public async Task WriteAsync(byte[] input, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);

		if (input.Length == 0)
		{
			return;
		}

		await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ThrowIfDisposed();

			var writer = _inputWriter
				?? throw new InvalidOperationException("The terminal backend has not been started.");

			await writer.WriteAsync(input.AsMemory(0, input.Length), cancellationToken).ConfigureAwait(false);
			await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (IOException) when (_stopping || _disposed)
		{
		}
		catch (ObjectDisposedException) when (_stopping || _disposed)
		{
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <inheritdoc />
	/// <remarks>Resizing before the console exists is a no-op rather than an error.</remarks>
	public async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
	{
		ValidateDimensions(columns, rows);

		await _lifetimeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ThrowIfDisposed();

			if (_pseudoConsole == IntPtr.Zero)
			{
				throw new InvalidOperationException("The terminal backend has not been started.");
			}

			var result = WindowsPtyNative.ResizePseudoConsole(
				_pseudoConsole,
				new WindowsPtyNative.Coord(columns, rows));

			ThrowIfFailed(result, "ResizePseudoConsole");
		}
		finally
		{
			_lifetimeLock.Release();
		}
	}

	/// <inheritdoc />
	public async IAsyncEnumerable<byte[]> ReadOutputAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		FileStream reader;

		await _lifetimeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ThrowIfDisposed();
			reader = _outputReader
				?? throw new InvalidOperationException("The terminal backend has not been started.");
		}
		finally
		{
			_lifetimeLock.Release();
		}

		var buffer = new byte[PipeBufferSize];

		while (!cancellationToken.IsCancellationRequested)
		{
			int bytesRead;

			try
			{
				bytesRead = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				yield break;
			}
			catch (IOException)
			{
				yield break;
			}
			catch (ObjectDisposedException)
			{
				yield break;
			}

			if (bytesRead == 0)
			{
				yield break;
			}

			var chunk = new byte[bytesRead];
			Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
			yield return chunk;
		}
	}

	/// <inheritdoc />
	/// <remarks>
	/// Closing the pseudo-console lets the child exit on its own first; it is terminated only if
	/// it outlives the bounded wait, so agents get a chance to shut down cleanly.
	/// </remarks>
	public async Task StopAsync(CancellationToken cancellationToken)
	{
		await _lifetimeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await StopCoreAsync().ConfigureAwait(false);
		}
		finally
		{
			_lifetimeLock.Release();
		}
	}

	/// <inheritdoc />
	/// <remarks>Stops the child if it is still running, then releases the pipes and OS handles.</remarks>
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		await StopAsync(CancellationToken.None).ConfigureAwait(false);
		_disposed = true;
		_writeLock.Dispose();
		_lifetimeLock.Dispose();
	}

	private TerminalSession StartCore(TerminalStartOptions options)
	{
		SafeFileHandle? inputRead = null;
		SafeFileHandle? inputWrite = null;
		SafeFileHandle? outputRead = null;
		SafeFileHandle? outputWrite = null;
		FileStream? inputWriter = null;
		FileStream? outputReader = null;
		var pseudoConsole = IntPtr.Zero;
		WindowsPtyNative.ProcessInformation processInformation = default;

		try
		{
			CreatePipe(out inputRead, out inputWrite, "input");
			CreatePipe(out outputRead, out outputWrite, "output");

			var result = WindowsPtyNative.CreatePseudoConsole(
				new WindowsPtyNative.Coord(options.Columns, options.Rows),
				inputRead,
				outputWrite,
				0,
				out pseudoConsole);

			ThrowIfFailed(result, "CreatePseudoConsole");

			inputRead.Dispose();
			inputRead = null;
			outputWrite.Dispose();
			outputWrite = null;

			// These anonymous pipes are synchronous. StopCoreAsync disposes the streams to
			// unblock any pending reads or writes before waiting on the child process.
			inputWriter = new FileStream(inputWrite, FileAccess.Write, PipeBufferSize, isAsync: false);
			inputWrite = null;
			outputReader = new FileStream(outputRead, FileAccess.Read, PipeBufferSize, isAsync: false);
			outputRead = null;

			processInformation = CreateAttachedProcess(pseudoConsole, options);

			_inputWriter = inputWriter;
			inputWriter = null;
			_outputReader = outputReader;
			outputReader = null;
			_pseudoConsole = pseudoConsole;
			pseudoConsole = IntPtr.Zero;
			_processHandle = processInformation.hProcess;
			processInformation.hProcess = IntPtr.Zero;
			_threadHandle = processInformation.hThread;
			processInformation.hThread = IntPtr.Zero;
			_processId = checked((int)processInformation.dwProcessId);
			_started = true;
			_stopping = false;

			return new TerminalSession(
				Guid.NewGuid().ToString("N"),
				_processId,
				options.Columns,
				options.Rows);
		}
		finally
		{
			inputWriter?.Dispose();
			outputReader?.Dispose();
			inputRead?.Dispose();
			inputWrite?.Dispose();
			outputRead?.Dispose();
			outputWrite?.Dispose();
			ClosePseudoConsoleIfNeeded(ref pseudoConsole);
			CloseHandleIfNeeded(ref processInformation.hThread);
			CloseHandleIfNeeded(ref processInformation.hProcess);
		}
	}

	private static void CreatePipe(
		out SafeFileHandle readHandle,
		out SafeFileHandle writeHandle,
		string pipeName)
	{
		if (!WindowsPtyNative.CreatePipe(out readHandle, out writeHandle, IntPtr.Zero, 0)
			|| readHandle.IsInvalid
			|| writeHandle.IsInvalid)
		{
			readHandle?.Dispose();
			writeHandle?.Dispose();
			throw LastWin32Exception($"CreatePipe ({pipeName})");
		}
	}

	private static unsafe WindowsPtyNative.ProcessInformation CreateAttachedProcess(
		IntPtr pseudoConsole,
		TerminalStartOptions options)
	{
		nuint attributeListSize = 0;
		WindowsPtyNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

		if (attributeListSize == 0)
		{
			throw LastWin32Exception("InitializeProcThreadAttributeList sizing");
		}

		var attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
		var attributeListInitialized = false;
		var environmentBlock = IntPtr.Zero;

		try
		{
			if (!WindowsPtyNative.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
			{
				throw LastWin32Exception("InitializeProcThreadAttributeList");
			}

			attributeListInitialized = true;

			if (!WindowsPtyNative.UpdateProcThreadAttribute(
					attributeList,
					0,
					WindowsPtyNative.ProcThreadAttributePseudoConsole,
					pseudoConsole,
					(nuint)IntPtr.Size,
					IntPtr.Zero,
					IntPtr.Zero))
			{
				throw LastWin32Exception("UpdateProcThreadAttribute");
			}

			WindowsPtyNative.StartupInfoEx startupInfo = new()
			{
				StartupInfo = new WindowsPtyNative.StartupInfo
				{
					cb = checked((uint)Marshal.SizeOf<WindowsPtyNative.StartupInfoEx>()),
				},
				lpAttributeList = attributeList,
			};

			var creationFlags = WindowsPtyNative.ExtendedStartupInfoPresent;
			environmentBlock = CreateEnvironmentBlock(options.EnvironmentVariables);
			if (environmentBlock != IntPtr.Zero)
			{
				creationFlags |= WindowsPtyNative.CreateUnicodeEnvironment;
			}

			var commandLine = (options.CommandLine + '\0').ToCharArray();
			var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
				? Environment.CurrentDirectory
				: options.WorkingDirectory;
			var currentDirectory = (workingDirectory + '\0').ToCharArray();

			fixed (char* commandLinePointer = commandLine)
			fixed (char* currentDirectoryPointer = currentDirectory)
			{
				if (!WindowsPtyNative.CreateProcess(
						null,
						commandLinePointer,
						IntPtr.Zero,
						IntPtr.Zero,
						false,
						creationFlags,
						environmentBlock,
						currentDirectoryPointer,
						ref startupInfo,
						out var processInformation))
				{
					throw LastWin32Exception("CreateProcessW");
				}

				return processInformation;
			}
		}
		finally
		{
			if (attributeListInitialized)
			{
				WindowsPtyNative.DeleteProcThreadAttributeList(attributeList);
			}

			Marshal.FreeHGlobal(attributeList);

			if (environmentBlock != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(environmentBlock);
			}
		}
	}

	private static IntPtr CreateEnvironmentBlock(IReadOnlyDictionary<string, string>? environmentVariables)
	{
		if (environmentVariables is null || environmentVariables.Count == 0)
		{
			return IntPtr.Zero;
		}

		Dictionary<string, string> mergedEnvironment = new(StringComparer.OrdinalIgnoreCase);
		foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
		{
			if (entry.Key is string key && entry.Value is string value)
			{
				mergedEnvironment[key] = value;
			}
		}

		foreach ((var key, var value) in environmentVariables)
		{
			if (string.IsNullOrWhiteSpace(key) || key.Contains('=', StringComparison.Ordinal))
			{
				throw new ArgumentException(
					$"Environment variable names must be non-empty and cannot contain '='. Invalid name: '{key}'.",
					nameof(environmentVariables));
			}

			mergedEnvironment[key] = value;
		}

		var environmentBlock = string.Join(
			'\0',
			mergedEnvironment
				.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
				.Select(static entry => $"{entry.Key}={entry.Value}"));

		return Marshal.StringToHGlobalUni(environmentBlock + "\0\0");
	}

	private static void ValidateDimensions(int columns, int rows)
	{
		if (columns is <= 0 or > short.MaxValue)
		{
			throw new ArgumentOutOfRangeException(nameof(columns), columns, "Columns must be between 1 and 32767.");
		}

		if (rows is <= 0 or > short.MaxValue)
		{
			throw new ArgumentOutOfRangeException(nameof(rows), rows, "Rows must be between 1 and 32767.");
		}
	}

	private static void ValidateCommandLine(string commandLine)
	{
		if (string.IsNullOrWhiteSpace(commandLine))
		{
			throw new ArgumentException("Command line cannot be empty.", nameof(commandLine));
		}
	}

	private static void ThrowIfFailed(int hresult, string operation)
	{
		if (hresult >= 0)
		{
			return;
		}

		// CA2201 warns against reserved exception types, but this one is never thrown on its
		// own: it is the fallback inner exception for an HRESULT the runtime could not map,
		// and COMException is the type that actually carries an HRESULT.
#pragma warning disable CA2201
		var exception = Marshal.GetExceptionForHR(hresult)
			?? new COMException($"{operation} failed.", hresult);
#pragma warning restore CA2201

		throw new InvalidOperationException(
			$"{operation} failed with HRESULT 0x{hresult:X8}.",
			exception);
	}

	private static Win32Exception LastWin32Exception(string operation)
	{
		var errorCode = Marshal.GetLastPInvokeError();
		return new Win32Exception(errorCode, $"{operation} failed with Win32 error {errorCode}.");
	}

	private async Task StopCoreAsync()
	{
		if (!_started
			&& _pseudoConsole == IntPtr.Zero
			&& _processHandle == IntPtr.Zero
			&& _threadHandle == IntPtr.Zero
			&& _inputWriter is null
			&& _outputReader is null)
		{
			return;
		}

		_stopping = true;

		_inputWriter?.Dispose();
		_inputWriter = null;

		// Close the output pipe before closing HPCON. Older Windows builds can
		// block in ClosePseudoConsole while output is still open and undrained.
		_outputReader?.Dispose();
		_outputReader = null;

		ClosePseudoConsoleIfNeeded(ref _pseudoConsole);

		try
		{
			await RunBlockingProcessStopAsync(
					() => StopChildProcessIfNeeded(_processHandle))
				.ConfigureAwait(false);
		}
		finally
		{
			CloseHandleIfNeeded(ref _threadHandle);
			CloseHandleIfNeeded(ref _processHandle);

			_processId = 0;
			_started = false;
		}
	}

	internal static Task RunBlockingProcessStopAsync(Action stopProcess)
	{
		ArgumentNullException.ThrowIfNull(stopProcess);
		return Task.Run(stopProcess);
	}

	private static void StopChildProcessIfNeeded(IntPtr processHandle)
	{
		if (processHandle == IntPtr.Zero)
		{
			return;
		}

		var waitResult = WindowsPtyNative.WaitForSingleObject(processHandle, StopWaitMilliseconds);
		if (waitResult == WindowsPtyNative.WaitObject0 || !IsProcessStillActive(processHandle))
		{
			return;
		}

		WindowsPtyNative.TerminateProcess(processHandle, 1);
		WindowsPtyNative.WaitForSingleObject(processHandle, TerminateWaitMilliseconds);
	}

	private static bool IsProcessStillActive(IntPtr processHandle) => WindowsPtyNative.GetExitCodeProcess(processHandle, out var exitCode)
			&& exitCode == WindowsPtyNative.StillActive;

	private static void ClosePseudoConsoleIfNeeded(ref IntPtr pseudoConsole)
	{
		if (pseudoConsole == IntPtr.Zero)
		{
			return;
		}

		WindowsPtyNative.ClosePseudoConsole(pseudoConsole);
		pseudoConsole = IntPtr.Zero;
	}

	private static void CloseHandleIfNeeded(ref IntPtr handle)
	{
		if (handle == IntPtr.Zero)
		{
			return;
		}

		WindowsPtyNative.CloseHandle(handle);
		handle = IntPtr.Zero;
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}