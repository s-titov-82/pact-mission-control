using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Diagnostics;

/// <summary>Compatibility facade for best-effort application diagnostics.</summary>
internal static class AppLog
{
	/// <summary>Appends an event to the bounded Logs directory below the supplied data root.</summary>
	public static Task AppendAsync(string rootDirectory, string phase, Exception? exception = null)
	{
		AppPaths paths = new(rootDirectory);
		return new RotatingAppLog(paths.LogsDirectory).AppendAsync(phase, exception);
	}
}