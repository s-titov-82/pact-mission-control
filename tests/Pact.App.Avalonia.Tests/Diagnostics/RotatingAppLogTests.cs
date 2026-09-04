using Pact.App.Avalonia.Diagnostics;

namespace Pact.App.Avalonia.Tests.Diagnostics;

public sealed class RotatingAppLogTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _root => _temporaryDirectory.Path;

	[Test]
	public async Task AppendAsync_appends_to_the_same_daily_segment()
	{
		DateTimeOffset now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
		RotatingAppLog log = new(_root, () => now, maxSegmentBytes: 4096);

		await log.AppendAsync("startup");
		await log.AppendAsync("ready");

		var path = Path.Combine(_root, "pact-2026-07-22.0.log");
		File.ReadLines(path).Count().ShouldBe(2);
	}

	[Test]
	public async Task AppendAsync_rolls_a_full_segment_and_expires_only_old_log_files()
	{
		DateTimeOffset now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
		Directory.CreateDirectory(_root);
		var fullPath = Path.Combine(_root, "pact-2026-07-22.0.log");
		var oldPath = Path.Combine(_root, "pact-2026-07-18.0.log");
		var recentPath = Path.Combine(_root, "pact-2026-07-21.0.log");
		var unrelatedPath = Path.Combine(_root, "keep.txt");
		var cancellationToken = TestContext.CurrentContext.CancellationToken;
		await File.WriteAllTextAsync(fullPath, new string('x', 32), cancellationToken);
		await File.WriteAllTextAsync(oldPath, "old", cancellationToken);
		await File.WriteAllTextAsync(recentPath, "recent", cancellationToken);
		await File.WriteAllTextAsync(unrelatedPath, "keep", cancellationToken);
		File.SetLastWriteTimeUtc(oldPath, now.UtcDateTime - TimeSpan.FromDays(4));
		File.SetLastWriteTimeUtc(recentPath, now.UtcDateTime - TimeSpan.FromDays(2));

		RotatingAppLog log = new(
			_root,
			() => now,
			maxSegmentBytes: 32,
			retention: TimeSpan.FromDays(3));
		await log.AppendAsync("startup");

		File.Exists(Path.Combine(_root, "pact-2026-07-22.1.log")).ShouldBeTrue();
		File.Exists(oldPath).ShouldBeFalse();
		File.Exists(recentPath).ShouldBeTrue();
		File.Exists(unrelatedPath).ShouldBeTrue();
	}

	[Test]
	public async Task AppendAsync_recreates_a_deleted_logs_directory()
	{
		RotatingAppLog log = new(_root);
		Directory.CreateDirectory(_root);
		Directory.Delete(_root);

		await log.AppendAsync("startup");

		Directory.GetFiles(_root, "pact-*.log").ShouldHaveSingleItem();
	}
	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}
}
