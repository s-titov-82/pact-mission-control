using System.Text;

namespace Pact.Infrastructure.Storage;

/// <summary>
/// Writes text files so an interrupted write cannot leave a truncated or partial file. Content
/// is staged to a temporary file and then swapped into place, which is why settings survive a
/// crash or power loss mid-save.
/// </summary>
public static class AtomicFileWriter
{
	/// <summary>
	/// Atomically writes UTF-8 text, staging the temporary file beside the destination.
	/// </summary>
	public static Task WriteTextAsync(string path, string content, CancellationToken cancellationToken) =>
		WriteTextAsync(path, content, stagingDirectory: null, cancellationToken);

	/// <summary>
	/// Atomically writes UTF-8 text, creating the destination directory if needed.
	/// </summary>
	/// <param name="path">Destination file.</param>
	/// <param name="content">Text to write.</param>
	/// <param name="stagingDirectory">
	/// Directory for the temporary file, or <see langword="null"/> to stage beside the
	/// destination. A staging directory must be on the same volume as the destination, since
	/// the final swap cannot be atomic across volumes.
	/// </param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <remarks>
	/// On failure the temporary file is removed and the original destination is left untouched;
	/// the original exception propagates rather than any cleanup failure.
	/// </remarks>
	public static Task WriteTextAsync(
		string path,
		string content,
		string? stagingDirectory,
		CancellationToken cancellationToken) =>
		WriteTextAsync(path, content, stagingDirectory, Encoding.UTF8, cancellationToken);

	internal static Task WriteTextAsync(
		string path,
		string content,
		string? stagingDirectory,
		Encoding encoding,
		CancellationToken cancellationToken) => WriteAsync(path, stagingDirectory,
			tempPath => File.WriteAllTextAsync(tempPath, content, encoding, cancellationToken));

	internal static Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken) =>
		WriteAsync(path, null, tempPath => File.WriteAllBytesAsync(tempPath, content, cancellationToken));

	private static async Task WriteAsync(string path, string? stagingDirectory, Func<string, Task> write)
	{
		string? tempPath = null;
		var completed = false;

		try
		{
			var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
			Directory.CreateDirectory(destinationDirectory);
			var tempDirectory = string.IsNullOrWhiteSpace(stagingDirectory)
				? destinationDirectory
				: Path.GetFullPath(stagingDirectory);
			Directory.CreateDirectory(tempDirectory);

			tempPath = Path.Combine(
				tempDirectory,
				$"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
			await write(tempPath);

			if (File.Exists(path))
			{
				File.Replace(tempPath, path, destinationBackupFileName: null);
			}
			else
			{
				File.Move(tempPath, path);
			}

			completed = true;
		}
		finally
		{
			if (!completed && tempPath is not null)
			{
				try
				{
					if (File.Exists(tempPath))
					{
						File.Delete(tempPath);
					}
				}
				catch
				{
					// Preserve the original write/replace/move exception.
				}
			}
		}
	}
}
