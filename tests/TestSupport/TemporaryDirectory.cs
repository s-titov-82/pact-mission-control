internal sealed class TemporaryDirectory : IDisposable, IAsyncDisposable
{
	private int _disposed;

	private TemporaryDirectory(string path)
	{
		Path = path;
	}

	internal string Path { get; }

	internal static TemporaryDirectory Create()
	{
		var path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			$"pact-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(path);
		return new TemporaryDirectory(path);
	}

	// Waited for on the caller's thread, so the retries below must not resume on its context: on a UI
	// thread they would wait for the very thread this blocks.
	public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0 || !Directory.Exists(Path))
		{
			return;
		}

		for (var attempt = 0; ; attempt++)
		{
			try
			{
				Directory.Delete(Path, recursive: true);
				return;
			}
			catch (IOException) when (attempt < 2)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
			}
			catch (UnauthorizedAccessException) when (attempt < 2)
			{
				ClearReadOnlyAttributes();
				await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
			}
		}
	}

	private void ClearReadOnlyAttributes()
	{
		foreach (var file in Directory.EnumerateFiles(
			Path,
			"*",
			SearchOption.AllDirectories))
		{
			var attributes = File.GetAttributes(file);
			if ((attributes & FileAttributes.ReadOnly) != 0)
			{
				File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
			}
		}
	}
}