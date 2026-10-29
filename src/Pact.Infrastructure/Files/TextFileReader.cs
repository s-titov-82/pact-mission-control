using System.Text;

namespace Pact.Infrastructure.Files;

/// <summary>Whether a file could be shown as text, and why not.</summary>
public enum TextFileState
{
	/// <summary>The file was read as text.</summary>
	Text,

	/// <summary>No file at the path, a folder, or a file that could not be read.</summary>
	Missing,

	/// <summary>Larger than <see cref="TextFileReader.MaxBytes"/>; not read.</summary>
	TooLarge,

	/// <summary>Holds a zero byte near its start.</summary>
	Binary
}

/// <summary>A file read for display: its text, or the reason there is none.</summary>
public sealed record TextFileContent(TextFileState State, string? Text);

/// <summary>Reads a file for display, refusing what would be unreadable or too heavy to show.</summary>
public static class TextFileReader
{
	/// <summary>Files larger than this are not read for display.</summary>
	public const long MaxBytes = 2 * 1024 * 1024;

	private const int BinaryProbeBytes = 8000;

	/// <summary>Reads <paramref name="path"/> as UTF-8 text, honouring a byte order mark.</summary>
	public static async Task<TextFileContent> ReadAsync(string path, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(path);
		try
		{
			FileInfo file = new(path);
			if (!file.Exists)
			{
				return new(TextFileState.Missing, null);
			}

			if (file.Length > MaxBytes)
			{
				return new(TextFileState.TooLarge, null);
			}

			var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
			if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, BinaryProbeBytes)) >= 0)
			{
				return new(TextFileState.Binary, null);
			}

			using MemoryStream stream = new(bytes);
			using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			return new(TextFileState.Text, await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new(TextFileState.Missing, null);
		}
	}
}
