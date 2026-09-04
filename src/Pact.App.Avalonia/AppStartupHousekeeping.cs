using Pact.App.Avalonia.Diagnostics;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia;

internal static class AppStartupHousekeeping
{
	internal static void Run(AppPaths appPaths)
	{
		ArgumentNullException.ThrowIfNull(appPaths);
		DataRootHousekeeping.Prepare(appPaths);
		DataRootHousekeeping.ClearSessionTemp(appPaths);
		new RotatingAppLog(appPaths.LogsDirectory).ApplyRetention();
	}
}
