namespace Pact.Updater;

internal sealed record UpdaterOptions(string TicketPath);

internal static class UpdaterCommandLine
{
	public static UpdaterOptions Parse(IReadOnlyList<string> args)
	{
		ArgumentNullException.ThrowIfNull(args);
		if (args.Count != 1
			|| string.IsNullOrWhiteSpace(args[0])
			|| !Path.IsPathFullyQualified(args[0]))
		{
			throw new ArgumentException(
				"Pact.Updater requires exactly one absolute restart-ticket path.",
				nameof(args));
		}

		return new UpdaterOptions(Path.GetFullPath(args[0]));
	}
}
