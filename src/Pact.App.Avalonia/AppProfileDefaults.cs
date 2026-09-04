using System.Reflection;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia;

internal static class AppProfileDefaults
{
	internal const string DataDirectoryName = "Pact";
	internal const string ProfileName = "stable-avalonia";
	internal static string ProductTitle { get; } =
		typeof(AppProfileDefaults).Assembly
			.GetCustomAttribute<AssemblyProductAttribute>()?.Product
		?? throw new InvalidOperationException("Application product metadata is missing.");

	internal static string ReadyWindowTitle => ProductTitle;

	internal static AppDataProfile Resolve(string[] args) => AppDataProfileResolver.Resolve(
		args,
		defaultDirectoryName: DataDirectoryName,
		profileName: ProfileName);

	internal static string StartupFailedWindowTitle(string message) =>
		$"{ProductTitle} - Startup failed: {message}";

	internal static string DataRootInUseMessage(AppDataProfile profile) =>
		$"{ProductTitle} data root is already in use:{Environment.NewLine}{profile.RootDirectory}";
}
