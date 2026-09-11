using Pact.App.Avalonia.Diagnostics;

namespace Pact.App.Avalonia.Tests.Diagnostics;

public sealed class SoftRestartProbeProcessSnapshotReaderTests
{
	[Test]
	public void ReadOpenConsoleChildren_returns_only_unique_direct_matching_children()
	{
		SoftRestartProbeProcessSnapshotReader reader = new(() =>
		[
			new SoftRestartProbeProcessEntry(10, 42, "OpenConsole.exe"),
			new SoftRestartProbeProcessEntry(11, 42, "openconsole.EXE"),
			new SoftRestartProbeProcessEntry(12, 41, "OpenConsole.exe"),
			new SoftRestartProbeProcessEntry(13, 42, "conhost.exe"),
			new SoftRestartProbeProcessEntry(10, 42, "OpenConsole.exe")
		]);

		reader.ReadOpenConsoleChildren(42).ShouldBe([10, 11]);
	}

	[Test]
	public void ReadOpenConsoleChildren_rejects_nonpositive_parent_pid()
	{
		SoftRestartProbeProcessSnapshotReader reader = new(static () => []);

		Should.Throw<ArgumentOutOfRangeException>(() => reader.ReadOpenConsoleChildren(0));
	}
}
