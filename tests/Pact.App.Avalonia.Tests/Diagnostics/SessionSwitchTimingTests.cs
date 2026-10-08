using System.Globalization;
using Pact.App.Avalonia.Diagnostics;

namespace Pact.App.Avalonia.Tests.Diagnostics;

public sealed class SessionSwitchTimingTests
{
	[Test]
	public void DescribeListsEveryClosedStageInOrderWithItsDuration()
	{
		SessionSwitchTiming timing = new();

		timing.Mark("note");
		Thread.Sleep(20);
		timing.Mark("show");

		var stages = timing.Describe().Split(' ');
		stages.Length.ShouldBe(2);
		stages[0].ShouldStartWith("note=");
		stages[1].ShouldStartWith("show=");
		int.Parse(stages[1]["show=".Length..], CultureInfo.InvariantCulture).ShouldBeGreaterThanOrEqualTo(15);
		timing.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(15));
	}
}
