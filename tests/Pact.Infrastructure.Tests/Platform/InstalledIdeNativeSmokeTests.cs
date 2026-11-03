using System.Buffers.Binary;
using Pact.Core.Platform;
using Pact.Infrastructure.Platform;

namespace Pact.Infrastructure.Tests.Platform;

[Platform("Win")]
public sealed class InstalledIdeNativeSmokeTests
{
	[Test, Explicit("Read-only workstation discovery; never starts an editor.")]
	public async Task Discover_installed_native_editors_and_png_icons_without_launching()
	{
		using var service = new WindowsInstalledIdeService(new PathLocator());
		var installed = await service.DiscoverAsync(true, CancellationToken.None);
		foreach (var ide in installed)
		{
			File.Exists(ide.ExecutablePath).ShouldBeTrue();
			Path.GetExtension(ide.ExecutablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
			if (ide.IconPng is { } png)
			{
				png.Take(8).ShouldBe(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
				var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
				var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
				width.ShouldBeGreaterThan(0u);
				height.ShouldBeGreaterThan(0u);
				TestContext.Out.WriteLine($"{ide.Name} | {ide.ExecutablePath} | PNG {png.Length} bytes {width}x{height}");
			}
			else
			{
				TestContext.Out.WriteLine($"{ide.Name} | {ide.ExecutablePath} | no icon");
			}
		}
		TestContext.Out.WriteLine($"Detected {installed.Count} native IDE installations; no launch invoked.");
	}

	private sealed class PathLocator : IExecutableLocator
	{
		public string? FindOnPath(string executableName)
		{
			foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
				.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				foreach (var extension in Path.HasExtension(executableName) ? new[] { string.Empty } : [".exe", ".cmd", ".bat", string.Empty])
				{
					var candidate = Path.Combine(directory.Trim('"'), executableName + extension);
					if (File.Exists(candidate))
					{
						return Path.GetFullPath(candidate);
					}
				}
			}
			return null;
		}
	}
}
