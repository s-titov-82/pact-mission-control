namespace Pact.Infrastructure.Tests.Storage;

internal static class SnapshotFixture
{
	public static async Task WriteSourceAsync(string root)
	{
		Directory.CreateDirectory(root);
		Directory.CreateDirectory(Path.Combine(root, "Settings"));

		string[] jsonFiles =
		[
			"projects.json",
			"shell-profiles.json",
			"prompt-templates.json",
			"web-link-templates.json",
			"scenarios.json",
			"git-helpers.json",
			"recent-directories.json",
			"window-layout.json"
		];
		foreach (var fileName in jsonFiles)
		{
			await File.WriteAllTextAsync(
				Path.Combine(root, "Settings", fileName),
				$"{{\"file\":\"{fileName}\"}}");
		}

		await WriteAsync(root, "Settings", "Notes", "note.md", "note");
		await WriteAsync(root, "WebView", "cache.bin", "cache");
		await WriteAsync(root, "Logs", "pact.log", "log");
		await WriteAsync(root, "Temp", "temporary.tmp", "temporary");
	}

	private static async Task WriteAsync(
		string root,
		params string[] pathAndContent)
	{
		var path = Path.Combine([root, .. pathAndContent[..^1]]);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(path, pathAndContent[^1]);
	}
}