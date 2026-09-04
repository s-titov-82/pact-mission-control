using System.Diagnostics;

internal sealed class ProfileToolLeaseHolder : IAsyncDisposable
{
	private readonly Process _process;
	private int _stopped;

	private ProfileToolLeaseHolder(Process process)
	{
		_process = process;
	}

	internal static async Task<ProfileToolLeaseHolder> StartAsync(
		string dataRoot,
		string readyFile,
		TimeSpan timeout)
	{
		ProcessStartInfo startInfo = new()
		{
			FileName = Path.Combine(AppContext.BaseDirectory, "Pact.ProfileTool.exe"),
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add("hold-lease");
		startInfo.ArgumentList.Add(dataRoot);
		startInfo.ArgumentList.Add(readyFile);
		var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start Pact.ProfileTool.");
		ProfileToolLeaseHolder holder = new(process);

		try
		{
			await holder.WaitForReadyFileAsync(readyFile, timeout);
			return holder;
		}
		catch
		{
			await holder.CrashAsync();
			await holder.DisposeAsync();
			throw;
		}
	}

	internal async Task ReleaseAsync()
	{
		if (Interlocked.Exchange(ref _stopped, 1) != 0)
		{
			return;
		}

		_process.StandardInput.Close();
		await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
		_process.ExitCode.ShouldBe(0);
	}

	internal async Task CrashAsync()
	{
		if (Interlocked.Exchange(ref _stopped, 1) != 0)
		{
			return;
		}

		if (!_process.HasExited)
		{
			_process.Kill(entireProcessTree: true);
		}

		await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
	}

	public async ValueTask DisposeAsync()
	{
		try
		{
			await ReleaseAsync();
		}
		finally
		{
			_process.Dispose();
		}
	}

	private async Task WaitForReadyFileAsync(string path, TimeSpan timeout)
	{
		using CancellationTokenSource cancellation = new(timeout);
		while (!File.Exists(path))
		{
			if (_process.HasExited)
			{
				var error = await _process.StandardError.ReadToEndAsync(
					cancellation.Token);
				throw new AssertionException(
					$"Lease holder exited with code {_process.ExitCode}: {error}");
			}

			await Task.Delay(TimeSpan.FromMilliseconds(25), cancellation.Token);
		}
	}
}