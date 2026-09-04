namespace Pact.Core.Platform;

/// <summary>
/// Hands a file or URL to the operating system's default handler, for the cases where Pact
/// deliberately delegates outside its own window.
/// </summary>
public interface IExternalLauncher
{
	/// <summary>
	/// Opens <paramref name="path"/> in the shell-registered application for its type.
	/// </summary>
	Task OpenFileAsync(string path);

	/// <summary>
	/// Opens <paramref name="uri"/> in the external default browser.
	/// </summary>
	/// <remarks>
	/// Implementations must reject non-HTTP(S) schemes: the URI can originate from rendered
	/// page content, so passing it to the shell unchecked would allow arbitrary protocol
	/// handlers to be invoked.
	/// </remarks>
	Task OpenHttpUriAsync(Uri uri);
}