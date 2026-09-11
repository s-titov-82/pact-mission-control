namespace Pact.Updater.Tests;

public sealed class InstalledFileReleaseGateTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();

	public void Dispose() => _temporaryDirectory.Dispose();

	[TestCase("Pact.App.Avalonia.exe")]
	[TestCase(@"conpty\OpenConsole.exe")]
	public async Task Gate_completes_only_after_each_installed_image_is_released(string lockedRelativePath)
	{
		var paths = CreateInstalledFiles();
		var lockedPath = Path.Combine(_temporaryDirectory.Path, lockedRelativePath);
		using FileStream held = new(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
		TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
		InstalledFileReleaseGate gate = new(
			TimeSpan.FromMilliseconds(10),
			(_, _) => released.Task,
			locked =>
			{
				if (locked.Contains(lockedPath, StringComparer.OrdinalIgnoreCase))
				{
					blocked.TrySetResult();
				}
			});

		var wait = gate.WaitAsync(paths, TimeSpan.FromSeconds(5), CancellationToken.None);
		await blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
		held.Dispose();
		released.SetResult();

		(await wait).ShouldBe(new FileReleaseResult(true, []));
	}

	[TestCase("Pact.App.Avalonia.exe")]
	[TestCase(@"conpty\OpenConsole.exe")]
	public async Task Gate_timeout_reports_the_exact_locked_image(string lockedRelativePath)
	{
		var paths = CreateInstalledFiles();
		var lockedPath = Path.Combine(_temporaryDirectory.Path, lockedRelativePath);
		using FileStream held = new(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
		InstalledFileReleaseGate gate = new();

		var result = await gate.WaitAsync(paths, TimeSpan.Zero, CancellationToken.None);

		result.Released.ShouldBeFalse();
		result.LockedPaths.ShouldBe([lockedPath]);
	}

	[Test]
	public async Task Gate_requires_every_image_to_be_released_in_the_same_iteration()
	{
		var paths = CreateInstalledFiles();
		var iteration = 0;
		List<string> probes = [];
		InstalledFileReleaseGate gate = new(
			TimeSpan.FromMilliseconds(10),
			(_, _) =>
			{
				iteration++;
				return Task.CompletedTask;
			},
			blockedObserved: null,
			path =>
			{
				probes.Add(path);
				return iteration switch
				{
					0 => string.Equals(path, paths[1], StringComparison.OrdinalIgnoreCase),
					1 => string.Equals(path, paths[0], StringComparison.OrdinalIgnoreCase),
					_ => false
				};
			});

		var result = await gate.WaitAsync(
			paths,
			TimeSpan.FromSeconds(1),
			CancellationToken.None);

		result.ShouldBe(new FileReleaseResult(true, []));
		iteration.ShouldBe(2);
		probes.ShouldBe([paths[0], paths[1], paths[0], paths[1], paths[0], paths[1]]);
	}

	private string[] CreateInstalledFiles()
	{
		var pactPath = Path.Combine(_temporaryDirectory.Path, "Pact.App.Avalonia.exe");
		var conptyPath = Path.Combine(_temporaryDirectory.Path, "conpty", "OpenConsole.exe");
		Directory.CreateDirectory(Path.GetDirectoryName(conptyPath)!);
		File.WriteAllText(pactPath, "pact");
		File.WriteAllText(conptyPath, "conpty");
		return [pactPath, conptyPath];
	}
}
