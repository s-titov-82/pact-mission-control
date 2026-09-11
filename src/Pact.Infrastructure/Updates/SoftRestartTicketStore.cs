using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pact.Core.Updates;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Updates;

/// <summary>
/// Persists one-time soft-restart tickets at deterministic id-derived paths without scanning.
/// </summary>
public sealed class SoftRestartTicketStore : ISoftRestartTicketStore
{
	private const int CurrentSchemaVersion = 1;
	private const int MaximumTicketBytes = 1024 * 1024;
	private const string TicketFileName = "update-resume.json";
	private readonly AppPaths _paths;
	private readonly UpdatePathPolicy _pathPolicy;
	private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

	/// <summary>Creates a store over the canonical data-root and update path policy.</summary>
	public SoftRestartTicketStore(AppPaths paths, UpdatePathPolicy pathPolicy)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
	}

	/// <summary>Creates a cryptographically random 32-byte id as 64 lowercase hex characters.</summary>
	public static string CreateRestartId() =>
		Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

	/// <inheritdoc />
	public async Task<string> WriteNewAsync(
		SoftRestartTicket ticket,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(ticket);
		try
		{
			ValidateTicket(ticket, ticket.RestartId, runningVersion: null, requirePending: true);
		}
		catch (InvalidDataException exception)
		{
			throw new ArgumentException(
				"The soft-restart ticket is not valid for this store.",
				nameof(ticket),
				exception);
		}
		var path = GetTicketPath(ticket.RestartId);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await WriteAtomicallyAsync(path, ticket, overwrite: false, cancellationToken)
			.ConfigureAwait(false);
		return path;
	}

	/// <inheritdoc />
	public async Task UpdateOutcomeAsync(
		string restartId,
		SoftRestartOutcome outcome,
		CancellationToken cancellationToken)
	{
		ValidateRestartId(restartId);
		ArgumentNullException.ThrowIfNull(outcome);
		var path = GetTicketPath(restartId);
		var ticket = await ReadStrictAsync(path, cancellationToken).ConfigureAwait(false)
			?? throw new FileNotFoundException("The soft-restart ticket does not exist.", path);
		ValidateTicket(ticket, restartId, runningVersion: null, requirePending: true);
		var updated = ticket with { Outcome = outcome };
		ValidateTicket(updated, restartId, runningVersion: null, requirePending: false);
		await WriteAtomicallyAsync(path, updated, overwrite: true, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<SoftRestartTicket?> ConsumeExactAsync(
		string restartId,
		StableReleaseVersion runningVersion,
		CancellationToken cancellationToken)
	{
		ValidateRestartId(restartId);
		var path = GetTicketPath(restartId);
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			var ticket = await ReadStrictAsync(path, cancellationToken).ConfigureAwait(false)
				?? throw new InvalidDataException("The soft-restart ticket is empty.");
			ValidateTicket(ticket, restartId, runningVersion, requirePending: false);
			File.Delete(path);
			TryDeleteEmptyDirectory(Path.GetDirectoryName(path)!);
			return ticket;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			Quarantine(path);
			return null;
		}
	}

	private static async Task<SoftRestartTicket?> ReadStrictAsync(
		string path,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(path))
		{
			return null;
		}
		if (new FileInfo(path).Length is <= 0 or > MaximumTicketBytes)
		{
			throw new InvalidDataException("The soft-restart ticket has an invalid size.");
		}
		await using FileStream stream = new(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 16384,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		return await JsonSerializer.DeserializeAsync<SoftRestartTicket>(
			stream,
			JsonOptions,
			cancellationToken).ConfigureAwait(false);
	}

	private void ValidateTicket(
		SoftRestartTicket ticket,
		string expectedRestartId,
		StableReleaseVersion? runningVersion,
		bool requirePending)
	{
		if (ticket.SchemaVersion != CurrentSchemaVersion)
		{
			throw new InvalidDataException("Unsupported soft-restart ticket schema.");
		}
		ValidateRestartId(ticket.RestartId);
		if (!string.Equals(ticket.RestartId, expectedRestartId, StringComparison.Ordinal))
		{
			throw new InvalidDataException("The embedded restart id does not match its path.");
		}
		if (ticket.SourceProcessId <= 0)
		{
			throw new InvalidDataException("The source process id must be positive.");
		}

		var executable = NormalizeAbsolute(ticket.ExecutablePath, nameof(ticket.ExecutablePath));
		var installation = NormalizeAbsolute(
			ticket.InstallationDirectory,
			nameof(ticket.InstallationDirectory));
		if (!string.Equals(
				Path.GetFileName(executable),
				"Pact.App.Avalonia.exe",
				StringComparison.OrdinalIgnoreCase)
			|| string.Equals(
				installation,
				Path.GetPathRoot(installation),
				StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(
				Path.GetDirectoryName(executable),
				installation,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("The Pact executable and installation directory disagree.");
		}

		var dataRoot = NormalizeAbsolute(ticket.DataRoot, nameof(ticket.DataRoot));
		if (!string.Equals(
				dataRoot,
				Path.GetFullPath(_paths.RootDirectory),
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("The ticket data root does not match its handoff root.");
		}

		ValidateCollections(ticket);
		ValidateModePaths(ticket);
		ValidateOutcome(ticket, runningVersion, requirePending);
	}

	private void ValidateModePaths(SoftRestartTicket ticket)
	{
		if (ticket.SoftRestartProbeOutputPath is { } probePath)
		{
			var normalizedProbe = NormalizeAbsolute(
				probePath,
				nameof(ticket.SoftRestartProbeOutputPath));
			if (ticket.Mode != SoftRestartMode.RestartOnly
				|| !IsStrictDescendant(normalizedProbe, _paths.TempDirectory))
			{
				throw new InvalidDataException("The diagnostic probe path is invalid for this mode.");
			}
		}

		switch (ticket.Mode)
		{
			case SoftRestartMode.RestartOnly:
				if (ticket.ExpectedTargetVersion is not null
					|| ticket.SetupPath is not null
					|| ticket.SetupSha256 is not null)
				{
					throw new InvalidDataException("RestartOnly must not carry update package fields.");
				}
				break;
			case SoftRestartMode.ApplyUpdate:
				if (ticket.ExpectedTargetVersion is not { } version
					|| ticket.SoftRestartProbeOutputPath is not null
					|| ticket.SetupPath is null
					|| ticket.SetupSha256 is null
					|| !IsLowerSha256(ticket.SetupSha256))
				{
					throw new InvalidDataException("ApplyUpdate requires exact package fields.");
				}
				var setup = _pathPolicy.EnsureOwnedPath(ticket.SetupPath);
				var expectedDirectory = _pathPolicy.GetPackageDirectory(version);
				var expectedName = $"pact-mission-control-{version}-win-x64-setup.exe";
				if (!string.Equals(
						Path.GetDirectoryName(setup),
						expectedDirectory,
						StringComparison.OrdinalIgnoreCase)
					|| !string.Equals(
						Path.GetFileName(setup),
						expectedName,
						StringComparison.Ordinal))
				{
					throw new InvalidDataException("The Setup path does not match the target version.");
				}
				break;
			default:
				throw new InvalidDataException("Unsupported soft-restart mode.");
		}
	}

	private static void ValidateOutcome(
		SoftRestartTicket ticket,
		StableReleaseVersion? runningVersion,
		bool requirePending)
	{
		if (requirePending)
		{
			if (ticket.Outcome.Kind != SoftRestartOutcomeKind.Pending
				|| ticket.Outcome.ErrorCategory is not null)
			{
				throw new InvalidDataException("A new ticket must be Pending.");
			}
			return;
		}

		if (ticket.Outcome.Kind == SoftRestartOutcomeKind.Pending)
		{
			throw new InvalidDataException("A Pending ticket cannot be consumed.");
		}
		if (ticket.Mode == SoftRestartMode.RestartOnly)
		{
			if (ticket.Outcome.Kind != SoftRestartOutcomeKind.Restarted
				|| ticket.Outcome.ErrorCategory is not null)
			{
				throw new InvalidDataException("RestartOnly has an invalid outcome.");
			}
			return;
		}

		if (ticket.Outcome.Kind == SoftRestartOutcomeKind.UpdateApplied)
		{
			if (ticket.Outcome.ErrorCategory is not null
				|| runningVersion is { } running
				&& running < ticket.ExpectedTargetVersion!.Value)
			{
				throw new InvalidDataException("The running version does not confirm the update.");
			}
			return;
		}
		if (ticket.Outcome.Kind != SoftRestartOutcomeKind.UpdateNotApplied
			|| !IsErrorCategory(ticket.Outcome.ErrorCategory))
		{
			throw new InvalidDataException("ApplyUpdate has an invalid failure outcome.");
		}
	}

	private static void ValidateCollections(SoftRestartTicket ticket)
	{
		ValidateIds(ticket.LiveTerminalIds, nameof(ticket.LiveTerminalIds));
		ValidateIds(ticket.LoadedWebPageIds, nameof(ticket.LoadedWebPageIds));
		ValidateIds(ticket.UnreadTerminalIds, nameof(ticket.UnreadTerminalIds));
		if (ticket.Selection is { } selection
			&& string.IsNullOrWhiteSpace(selection.ProjectId)
			&& string.IsNullOrWhiteSpace(selection.ItemId))
		{
			throw new InvalidDataException("A selected owner or item id is required.");
		}
	}

	private static void ValidateIds(IReadOnlyList<string>? ids, string name)
	{
		if (ids is null || ids.Count > 10_000
			|| ids.Any(string.IsNullOrWhiteSpace)
			|| ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
		{
			throw new InvalidDataException($"{name} is invalid.");
		}
	}

	private string GetTicketPath(string restartId)
	{
		ValidateRestartId(restartId);
		return _pathPolicy.EnsureOwnedPath(Path.Combine(
			_pathPolicy.GetHandoffDirectory(restartId),
			TicketFileName));
	}

	private static async Task WriteAtomicallyAsync(
		string path,
		SoftRestartTicket ticket,
		bool overwrite,
		CancellationToken cancellationToken)
	{
		var directory = Path.GetDirectoryName(path)!;
		var temporaryPath = Path.Combine(
			directory,
			$".{TicketFileName}.{Guid.NewGuid():N}.tmp");
		try
		{
			await using (FileStream stream = new(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 16384,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await JsonSerializer.SerializeAsync(
					stream,
					ticket,
					JsonOptions,
					cancellationToken).ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
			}
			File.Move(temporaryPath, path, overwrite);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}

	private static string NormalizeAbsolute(string path, string name)
	{
		if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
		{
			throw new ArgumentException($"{name} must be an absolute path.", name);
		}
		return Path.GetFullPath(path);
	}

	private static void ValidateRestartId(string restartId)
	{
		if (restartId is not { Length: 64 }
			|| !restartId.All(
				character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
		{
			throw new ArgumentException(
				"The restart id must be 64 lowercase hexadecimal characters.",
				nameof(restartId));
		}
	}

	private static bool IsLowerSha256(string value) =>
		value.Length == 64
		&& value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

	private static bool IsErrorCategory(string? value) =>
		value is { Length: > 0 and <= 64 }
		&& value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

	private static bool IsStrictDescendant(string candidate, string parent)
	{
		var relative = Path.GetRelativePath(parent, candidate);
		return !Path.IsPathFullyQualified(relative)
			&& relative is not "." and not ".."
			&& !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
			&& !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
	}

	private static void Quarantine(string path)
	{
		if (!File.Exists(path))
		{
			return;
		}
		var directory = Path.GetDirectoryName(path)!;
		var quarantine = Path.Combine(
			directory,
			$"update-resume.quarantined-{Guid.NewGuid():N}.json");
		File.Move(path, quarantine, overwrite: false);
	}

	private static void TryDeleteEmptyDirectory(string directory)
	{
		if (Directory.Exists(directory)
			&& !Directory.EnumerateFileSystemEntries(directory).Any())
		{
			Directory.Delete(directory);
		}
	}

	private static JsonSerializerOptions CreateJsonOptions()
	{
		JsonSerializerOptions options = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = false,
			UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
			ReadCommentHandling = JsonCommentHandling.Disallow,
			AllowTrailingCommas = false,
			WriteIndented = true,
			MaxDepth = 32
		};
		options.Converters.Add(new JsonStringEnumConverter(
			JsonNamingPolicy.CamelCase,
			allowIntegerValues: false));
		return options;
	}
}
