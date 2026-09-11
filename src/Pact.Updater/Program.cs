using Pact.Updater;

try
{
	var options = UpdaterCommandLine.Parse(args);
	UpdaterRunner runner = new(
		new ProcessLauncher(),
		TimeSpan.FromSeconds(30));
	return await runner.RunAsync(options, CancellationToken.None).ConfigureAwait(false);
}
catch (Exception exception) when (exception is ArgumentException
	or IOException
	or UnauthorizedAccessException)
{
	return 1;
}
