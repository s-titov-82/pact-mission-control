namespace Pact.App.Avalonia;

internal static class AppShutdownSequence
{
	public static async Task RunAsync(params Func<Task>[] steps)
	{
		List<Exception> failures = [];
		foreach (var step in steps)
		{
			try
			{ await step(); }
			catch (Exception exception) { failures.Add(exception); }
		}
		if (failures.Count > 0)
		{
			throw new AggregateException("Pact cleanup failed.", failures);
		}
	}
}