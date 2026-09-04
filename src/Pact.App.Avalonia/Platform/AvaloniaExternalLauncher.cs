using System.Diagnostics;
using Pact.Core.Platform;

namespace Pact.App.Avalonia.Platform;

internal sealed class AvaloniaExternalLauncher : IExternalLauncher
{
	public Task OpenFileAsync(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		Start(path);
		return Task.CompletedTask;
	}

	public Task OpenHttpUriAsync(Uri uri)
	{
		ArgumentNullException.ThrowIfNull(uri);
		if (!uri.IsAbsoluteUri
			|| uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
		{
			throw new ArgumentException("Only absolute HTTP(S) URLs can be opened.", nameof(uri));
		}

		Start(uri.AbsoluteUri);
		return Task.CompletedTask;
	}

	private static void Start(string target) => _ = Process.Start(new ProcessStartInfo(target)
	{
		UseShellExecute = true
	});
}