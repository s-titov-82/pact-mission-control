using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia;

/// <summary>Contains the complete validated process command line parsed exactly once.</summary>
internal sealed record AppLaunchOptions(
	AppDataProfile Profile,
	bool PassDataRoot,
	string? EngineProbeOutputPath,
	string? SoftRestartId,
	string? SoftRestartProbeOutputPath)
{
	private const string DataRootArgument = "--data-root";
	private const string EngineProbeArgument = "--engine-probe-output";
	private const string SoftRestartIdArgument = "--soft-restart-id";
	private const string SoftRestartProbeArgument = "--soft-restart-probe-output";

	public static AppLaunchOptions Parse(IReadOnlyList<string> args)
	{
		ArgumentNullException.ThrowIfNull(args);
		string? dataRoot = null;
		string? engineProbe = null;
		string? restartId = null;
		string? restartProbe = null;
		var passDataRoot = false;
		for (var index = 0; index < args.Count; index++)
		{
			var argument = args[index];
			if (string.Equals(argument, DataRootArgument, StringComparison.Ordinal))
			{
				SetOnce(ref dataRoot, ReadValue(args, ref index, DataRootArgument), DataRootArgument);
				passDataRoot = true;
			}
			else if (argument.StartsWith(DataRootArgument + "=", StringComparison.Ordinal))
			{
				SetOnce(ref dataRoot, argument[(DataRootArgument.Length + 1)..], DataRootArgument);
				passDataRoot = true;
			}
			else if (string.Equals(argument, EngineProbeArgument, StringComparison.Ordinal))
			{
				SetOnce(ref engineProbe, ReadValue(args, ref index, EngineProbeArgument), EngineProbeArgument);
			}
			else if (string.Equals(argument, SoftRestartIdArgument, StringComparison.Ordinal))
			{
				SetOnce(ref restartId, ReadValue(args, ref index, SoftRestartIdArgument), SoftRestartIdArgument);
			}
			else if (string.Equals(argument, SoftRestartProbeArgument, StringComparison.Ordinal))
			{
				SetOnce(ref restartProbe, ReadValue(args, ref index, SoftRestartProbeArgument), SoftRestartProbeArgument);
			}
			else
			{
				throw new ArgumentException($"Unknown Pact argument: {argument}.", nameof(args));
			}
		}

		var profile = AppProfileDefaults.ResolveProfile(dataRoot);
		engineProbe = NormalizeAbsolutePath(engineProbe, EngineProbeArgument);
		restartProbe = NormalizeAbsolutePath(restartProbe, SoftRestartProbeArgument);
		var tempDirectory = new AppPaths(profile.RootDirectory).TempDirectory;
		if (engineProbe is not null
			&& !IsStrictDescendant(engineProbe, tempDirectory))
		{
			throw new ArgumentException(
				$"{EngineProbeArgument} must be below the selected data root's Temp directory.",
				nameof(args));
		}
		if (restartProbe is not null && !IsStrictDescendant(restartProbe, tempDirectory))
		{
			throw new ArgumentException(
				$"{SoftRestartProbeArgument} must be below the selected data root's Temp directory.",
				nameof(args));
		}
		if (restartId is not null && !IsRestartId(restartId))
		{
			throw new ArgumentException(
				$"{SoftRestartIdArgument} requires 64 lowercase hexadecimal characters.",
				nameof(args));
		}
		return new AppLaunchOptions(
			profile,
			passDataRoot,
			engineProbe,
			restartId,
			restartProbe);
	}

	private static string ReadValue(
		IReadOnlyList<string> args,
		ref int index,
		string argument)
	{
		if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
		{
			throw new ArgumentException($"{argument} requires a value.", nameof(args));
		}
		return args[index];
	}

	private static void SetOnce(ref string? target, string value, string argument)
	{
		if (target is not null)
		{
			throw new ArgumentException($"{argument} may be specified only once.");
		}
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new ArgumentException($"{argument} requires a value.");
		}
		target = value;
	}

	private static string? NormalizeAbsolutePath(string? value, string argument)
	{
		if (value is null)
		{
			return null;
		}
		if (!Path.IsPathFullyQualified(value))
		{
			throw new ArgumentException($"{argument} requires an absolute path.");
		}
		return Path.GetFullPath(value);
	}

	private static bool IsRestartId(string value) =>
		value.Length == 64
		&& value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

	private static bool IsStrictDescendant(string candidate, string parent)
	{
		var relative = Path.GetRelativePath(parent, candidate);
		return !Path.IsPathFullyQualified(relative)
			&& relative is not "." and not ".."
			&& !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
			&& !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
	}
}
