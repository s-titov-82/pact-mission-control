using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;

namespace Pact.Infrastructure.Storage;

/// <summary>
/// Exclusive claim on one data root, so two Pact processes cannot share a profile and corrupt
/// its JSON state or WebView2 user-data folder. Held for the lifetime of the process and
/// released on <see cref="Dispose"/>.
/// </summary>
public sealed class AppDataProcessLease : IDisposable
{
	private static readonly System.Threading.Lock OwnedNamesSync = new();
	private static readonly HashSet<string> OwnedNames = new(StringComparer.Ordinal);

	private readonly string _name;
	private readonly MutexOwnerThread _owner;
	private int _disposed;

	private AppDataProcessLease(string name, MutexOwnerThread owner)
	{
		_name = name;
		_owner = owner;
	}

	/// <summary>
	/// Attempts to claim <paramref name="rootDirectory"/> for this process.
	/// </summary>
	/// <param name="rootDirectory">Data root to claim.</param>
	/// <param name="lease">
	/// The acquired lease, or <see langword="null"/> when the root is already held. Dispose it
	/// to release the claim.
	/// </param>
	/// <returns>
	/// <see langword="false"/> when another process — or another lease in this process — already
	/// holds the root. Callers must treat this as a normal "profile busy" outcome and refuse to
	/// start, not as an error to retry.
	/// </returns>
	public static bool TryAcquire(
		string rootDirectory,
		out AppDataProcessLease? lease)
	{
		var name = GetMutexName(rootDirectory);
		lock (OwnedNamesSync)
		{
			if (!OwnedNames.Add(name))
			{
				lease = null;
				return false;
			}
		}

		MutexOwnerThread? owner = null;
		try
		{
			owner = new MutexOwnerThread(name);
			owner.StartAndWaitForAcquisition();
			if (!owner.Acquired)
			{
				owner.Dispose();
				lock (OwnedNamesSync)
				{
					OwnedNames.Remove(name);
				}

				lease = null;
				return false;
			}

			lease = new AppDataProcessLease(name, owner);
			return true;
		}
		catch
		{
			try
			{
				owner?.Dispose();
			}
			finally
			{
				lock (OwnedNamesSync)
				{
					OwnedNames.Remove(name);
				}
			}

			throw;
		}
	}

	/// <summary>
	/// Builds the mutex name identifying a data root.
	/// </summary>
	/// <returns>
	/// A cross-session mutex name derived from a hash of the normalized path. Normalization
	/// (full path, no trailing separator, upper-cased on Windows) ensures the same directory
	/// written differently still collides, which is what makes the lease effective.
	/// </returns>
	public static string GetMutexName(string rootDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

		var normalized = Path.TrimEndingDirectorySeparator(
			Path.GetFullPath(rootDirectory));
		if (OperatingSystem.IsWindows())
		{
			normalized = normalized.ToUpperInvariant();
		}

		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
		var hashPrefix = Convert.ToHexString(hash)[..32].ToLowerInvariant();
		return $"Global\\Pact.DataRoot.{hashPrefix}";
	}

	/// <summary>
	/// Releases the claim, letting another process open the root. Safe to call more than once.
	/// </summary>
	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		try
		{
			_owner.Dispose();
		}
		finally
		{
			lock (OwnedNamesSync)
			{
				OwnedNames.Remove(_name);
			}
		}
	}

	private sealed class MutexOwnerThread : IDisposable
	{
		private readonly string _name;
		private readonly ManualResetEventSlim _initialized = new();
		private readonly ManualResetEventSlim _releaseRequested = new();
		private readonly Thread _thread;
		private ExceptionDispatchInfo? _initializationFailure;
		private ExceptionDispatchInfo? _releaseFailure;
		private int _started;
		private int _disposed;

		public MutexOwnerThread(string name)
		{
			_name = name;
			_thread = new Thread(Run)
			{
				IsBackground = true,
				Name = "Pact data-root lease"
			};
		}

		public bool Acquired { get; private set; }

		public void StartAndWaitForAcquisition()
		{
			_thread.Start();
			Interlocked.Exchange(ref _started, 1);
			try
			{
				_initialized.Wait();
			}
			catch
			{
				_releaseRequested.Set();
				_thread.Join();
				throw;
			}

			if (_initializationFailure is not null)
			{
				_thread.Join();
				_initializationFailure.Throw();
			}
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
			{
				return;
			}

			try
			{
				if (Volatile.Read(ref _started) != 0)
				{
					_releaseRequested.Set();
					_thread.Join();
					_releaseFailure?.Throw();
				}
			}
			finally
			{
				_initialized.Dispose();
				_releaseRequested.Dispose();
			}
		}

		private void Run()
		{
			Mutex? mutex = null;
			var ownsMutex = false;
			try
			{
				mutex = new Mutex(initiallyOwned: false, _name);
				try
				{
					ownsMutex = mutex.WaitOne(TimeSpan.Zero);
				}
				catch (AbandonedMutexException)
				{
					ownsMutex = true;
				}

				Acquired = ownsMutex;
			}
			catch (Exception exception)
			{
				_initializationFailure = ExceptionDispatchInfo.Capture(exception);
			}
			finally
			{
				_initialized.Set();
			}

			try
			{
				if (ownsMutex)
				{
					_releaseRequested.Wait();
					mutex!.ReleaseMutex();
				}
			}
			catch (Exception exception)
			{
				_releaseFailure = ExceptionDispatchInfo.Capture(exception);
			}
			finally
			{
				mutex?.Dispose();
			}
		}
	}
}