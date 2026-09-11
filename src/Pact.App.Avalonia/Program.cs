using System.Runtime.InteropServices;
using Avalonia;
using Pact.App.Avalonia.Diagnostics;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia;

internal static partial class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		var options = AppLaunchOptions.Parse(args);
		var profile = options.Profile;
		var probeRunner = EngineProbeRunner.TryCreate(
			options.EngineProbeOutputPath,
			profile);
		if (!AppDataProcessLease.TryAcquire(profile.RootDirectory, out var lease))
		{
			var message = AppProfileDefaults.DataRootInUseMessage(profile);
			_ = MessageBoxW(IntPtr.Zero, message, AppProfileDefaults.ProductTitle, 0x30);
			return 2;
		}

		AppBootstrap bootstrap = new(options, lease!, probeRunner);
		App.Bootstrap = bootstrap;
		try
		{
			return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
		}
		catch (Exception exception)
		{
			AppLog.AppendAsync(profile.RootDirectory, "Startup failed", exception)
				.GetAwaiter().GetResult();
			return 1;
		}
		finally
		{
			try
			{
				bootstrap.Dispose();
			}
			catch (Exception exception)
			{
				AppLog.AppendAsync(profile.RootDirectory, "Shutdown failed", exception)
					.GetAwaiter().GetResult();
			}
		}
	}

	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
		.UsePlatformDetect()
		.LogToTrace();

	[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
