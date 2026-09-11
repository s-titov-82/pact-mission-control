using System.Text.Json;
using Pact.App.Avalonia.Controllers;

namespace Pact.App.Avalonia.Diagnostics;

internal sealed class SoftRestartProbeRunner
{
	private readonly AppLaunchOptions _options;

	public SoftRestartProbeRunner(AppLaunchOptions options)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
	}

	/// <summary>
	/// Starts the diagnostic handoff on the first process, or writes bounded restoration
	/// evidence in the relaunched process. A null result means graceful restart must begin.
	/// </summary>
	public async Task<int?> RunAsync(
		AvaloniaMainShellController controller,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(controller);
		if (_options.SoftRestartProbeOutputPath is not { } outputPath)
		{
			return 0;
		}

		if (_options.SoftRestartId is null)
		{
			var result = controller.SoftRestartCoordinator is { } restartCoordinator
				? await restartCoordinator.RequestRestartOnlyAsync(cancellationToken)
				: new SoftRestartRequestResult(false, [], "soft-restart-unavailable");
			if (result.Started)
			{
				return null;
			}

			await SoftRestartProbeEvidenceWriter.WriteAsync(
				outputPath,
				new SoftRestartProbeEvidence(
					RestartId: null,
					RestoredTerminalIds: [],
					ColdStartFallbacks: [],
					RestoredWebPageIds: [],
					Failures: result.Blockers.Select(item => item.Id)
						.Append(result.Error ?? "soft-restart-blocked")
						.ToArray(),
					OrchestratorRestored: false,
					SelectionRestored: false,
					UpdateFailureCategory: null),
				cancellationToken);
			return 1;
		}

		var summary = controller.LastRestorationSummary;
		if (summary is null)
		{
			await SoftRestartProbeEvidenceWriter.WriteAsync(
				outputPath,
				new SoftRestartProbeEvidence(
					_options.SoftRestartId,
					[],
					[],
					[],
					["ticket-not-consumed"],
					false,
					false,
					null),
				cancellationToken);
			return 1;
		}

		await SoftRestartProbeEvidenceWriter.WriteAsync(
			outputPath,
			new SoftRestartProbeEvidence(
				_options.SoftRestartId,
				summary.RestoredTerminalIds,
				summary.ColdStartFallbacks,
				summary.RestoredWebPageIds,
				summary.Failures,
				summary.OrchestratorRestored,
				summary.SelectionRestored,
				summary.UpdateFailureCategory),
			cancellationToken);
		return summary.Failures.Count == 0 ? 0 : 1;
	}
}

internal static class SoftRestartProbeEvidenceWriter
{
	private const int MaximumItemsPerCategory = 256;
	private const int MaximumItemLength = 160;

	public static async Task WriteAsync(
		string outputPath,
		SoftRestartProbeEvidence evidence,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
		ArgumentNullException.ThrowIfNull(evidence);
		var bounded = evidence with
		{
			RestoredTerminalIds = Bound(evidence.RestoredTerminalIds),
			ColdStartFallbacks = Bound(evidence.ColdStartFallbacks),
			RestoredWebPageIds = Bound(evidence.RestoredWebPageIds),
			Failures = Bound(evidence.Failures)
		};
		Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
		var temporaryPath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
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
					bounded,
					cancellationToken: cancellationToken);
				await stream.FlushAsync(cancellationToken);
			}
			File.Move(temporaryPath, outputPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}

	private static string[] Bound(IEnumerable<string> values) => values
		.Take(MaximumItemsPerCategory)
		.Select(value => value.Length <= MaximumItemLength
			? value
			: value[..MaximumItemLength])
		.ToArray();
}

internal sealed record SoftRestartProbeEvidence(
	string? RestartId,
	IReadOnlyList<string> RestoredTerminalIds,
	IReadOnlyList<string> ColdStartFallbacks,
	IReadOnlyList<string> RestoredWebPageIds,
	IReadOnlyList<string> Failures,
	bool OrchestratorRestored,
	bool SelectionRestored,
	string? UpdateFailureCategory);
