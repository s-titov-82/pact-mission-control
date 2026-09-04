using Pact.Infrastructure.Storage;
using Pact.ProfileTool;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
	try
	{
		if (arguments is ["hold-lease", var root, var readyFile])
		{
			return await HoldLeaseAsync(
				NormalizeAbsolutePath(root, "dataRoot"),
				NormalizeAbsolutePath(readyFile, "readyFile"));
		}

		(var source, var destination, var replace) = ParseArguments(arguments);
		Console.WriteLine($"Source: {source}");
		Console.WriteLine($"Destination: {destination}");
		await AppProfileSnapshotCopier.CopyAsync(
			source,
			destination,
			replace,
			CancellationToken.None);
		Console.WriteLine("Profile snapshot completed.");
		return 0;
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine(ex.Message);
		return 1;
	}
}

static async Task<int> HoldLeaseAsync(string root, string readyFile)
{
	if (!AppDataProcessLease.TryAcquire(root, out var lease))
	{
		throw new IOException($"Profile is already in use: {root}");
	}

	using (lease)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(readyFile)!);
		await File.WriteAllTextAsync(readyFile, "ready");
		await Console.In.ReadToEndAsync();
	}

	return 0;
}

static (string Source, string Destination, bool Replace) ParseArguments(string[] arguments)
{
	string? source = null;
	string? destination = null;
	var replace = false;

	for (var index = 0; index < arguments.Length; index++)
	{
		switch (arguments[index])
		{
			case "--source" when source is null:
				source = ReadAbsolutePath(arguments, ref index, "--source");
				break;
			case "--destination" when destination is null:
				destination = ReadAbsolutePath(arguments, ref index, "--destination");
				break;
			case "--replace" when !replace:
				replace = true;
				break;
			default:
				throw new ArgumentException(
					"Usage: --source <absolute-path> --destination <absolute-path> [--replace]");
		}
	}

	if (source is null || destination is null)
	{
		throw new ArgumentException(
			"Usage: --source <absolute-path> --destination <absolute-path> [--replace]");
	}

	return (source, destination, replace);
}

static string ReadAbsolutePath(string[] arguments, ref int index, string option)
{
	if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
	{
		throw new ArgumentException($"{option} requires an absolute path.");
	}

	return NormalizeAbsolutePath(arguments[index], option);
}

static string NormalizeAbsolutePath(string path, string option)
{
	if (string.IsNullOrWhiteSpace(path))
	{
		throw new ArgumentException($"{option} requires an absolute path.");
	}

	if (!Path.IsPathFullyQualified(path))
	{
		throw new ArgumentException($"{option} requires an absolute path.");
	}

	return Path.GetFullPath(path);
}
