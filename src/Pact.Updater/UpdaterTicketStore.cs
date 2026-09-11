using System.Text.Json;
using System.Text.Json.Serialization;
using Pact.Core.Updates;

namespace Pact.Updater;

internal static class UpdaterTicketStore
{
	private const int MaximumTicketBytes = 1024 * 1024;
	private const string TicketFileName = "update-resume.json";
	private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

	public static Task<SoftRestartTicket> LoadPendingAsync(
		string ticketPath,
		CancellationToken cancellationToken) =>
		LoadAsync(ticketPath, requirePending: true, cancellationToken);

	public static Task<SoftRestartTicket> LoadTerminalAsync(
		string ticketPath,
		CancellationToken cancellationToken) =>
		LoadAsync(ticketPath, requirePending: false, cancellationToken);

	public static async Task RewriteOutcomeAsync(
		string ticketPath,
		SoftRestartOutcome outcome,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(outcome);
		var ticket = await LoadPendingAsync(ticketPath, cancellationToken).ConfigureAwait(false);
		var updated = ticket with { Outcome = outcome };
		Validate(updated, ticketPath, requirePending: false);
		var temporaryPath = Path.Combine(
			Path.GetDirectoryName(ticketPath)!,
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
					updated,
					JsonOptions,
					cancellationToken).ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
			}
			File.Move(temporaryPath, ticketPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}

	public static void DeleteExact(string ticketPath)
	{
		if (File.Exists(ticketPath))
		{
			File.Delete(ticketPath);
		}
	}

	private static async Task<SoftRestartTicket> LoadAsync(
		string ticketPath,
		bool requirePending,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(ticketPath) || !Path.IsPathFullyQualified(ticketPath))
		{
			throw new InvalidDataException("The ticket path must be absolute.");
		}
		var normalizedPath = Path.GetFullPath(ticketPath);
		if (!File.Exists(normalizedPath))
		{
			throw new FileNotFoundException("The restart ticket does not exist.", normalizedPath);
		}
		if (new FileInfo(normalizedPath).Length is <= 0 or > MaximumTicketBytes)
		{
			throw new InvalidDataException("The restart ticket has an invalid size.");
		}

		await using FileStream stream = new(
			normalizedPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 16384,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		var ticket = await JsonSerializer.DeserializeAsync<SoftRestartTicket>(
			stream,
			JsonOptions,
			cancellationToken).ConfigureAwait(false)
			?? throw new InvalidDataException("The restart ticket is empty.");
		Validate(ticket, normalizedPath, requirePending);
		return ticket;
	}

	private static void Validate(
		SoftRestartTicket ticket,
		string ticketPath,
		bool requirePending)
	{
		if (ticket.SchemaVersion != 1
			|| ticket.SourceProcessId <= 0
			|| ticket.Mode != SoftRestartMode.RestartOnly
			|| ticket.ExpectedTargetVersion is not null
			|| ticket.SetupPath is not null
			|| ticket.SetupSha256 is not null)
		{
			throw new InvalidDataException("The helper supports only a valid RestartOnly ticket.");
		}
		if (!IsRestartId(ticket.RestartId))
		{
			throw new InvalidDataException("The restart id is invalid.");
		}

		var dataRoot = NormalizeAbsolute(ticket.DataRoot, "data root");
		var expectedTicketPath = Path.Combine(
			dataRoot,
			"Temp",
			"Retained",
			"Updates",
			"Handoffs",
			ticket.RestartId,
			TicketFileName);
		if (!string.Equals(
				Path.GetFullPath(ticketPath),
				Path.GetFullPath(expectedTicketPath),
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("The ticket path is not its deterministic owned path.");
		}

		var executable = NormalizeAbsolute(ticket.ExecutablePath, "Pact executable");
		var installation = NormalizeAbsolute(ticket.InstallationDirectory, "installation directory");
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
			throw new InvalidDataException("The installed executable path is invalid.");
		}

		if (ticket.SoftRestartProbeOutputPath is { } probePath
			&& !IsStrictDescendant(
				NormalizeAbsolute(probePath, "diagnostic output"),
				Path.Combine(dataRoot, "Temp")))
		{
			throw new InvalidDataException("The diagnostic output path escapes Temp.");
		}
		if (ticket.LiveTerminalIds is null
			|| ticket.LoadedWebPageIds is null
			|| ticket.UnreadTerminalIds is null)
		{
			throw new InvalidDataException("The restart snapshot collections are missing.");
		}

		if (requirePending)
		{
			if (ticket.Outcome.Kind != SoftRestartOutcomeKind.Pending
				|| ticket.Outcome.ErrorCategory is not null)
			{
				throw new InvalidDataException("The helper requires a Pending ticket.");
			}
		}
		else if (ticket.Outcome.Kind != SoftRestartOutcomeKind.Restarted
			|| ticket.Outcome.ErrorCategory is not null)
		{
			throw new InvalidDataException("The RestartOnly ticket has no terminal outcome.");
		}
	}

	private static string NormalizeAbsolute(string path, string description)
	{
		if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
		{
			throw new InvalidDataException($"The {description} path must be absolute.");
		}
		return Path.GetFullPath(path);
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

	private static JsonSerializerOptions CreateJsonOptions()
	{
		JsonSerializerOptions options = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = false,
			UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
			ReadCommentHandling = JsonCommentHandling.Disallow,
			AllowTrailingCommas = false,
			MaxDepth = 32
		};
		options.Converters.Add(new JsonStringEnumConverter(
			JsonNamingPolicy.CamelCase,
			allowIntegerValues: false));
		return options;
	}
}
