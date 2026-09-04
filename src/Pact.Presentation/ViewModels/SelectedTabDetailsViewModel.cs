using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Pact.Core.Sessions;
using Pact.Presentation.Services.WebMonitoring;

namespace Pact.Presentation.ViewModels;

/// <summary>One labelled fact shown for the selected terminal or web tab.</summary>
public sealed class SelectedTabDetailRowViewModel : INotifyPropertyChanged
{
	private string _value;
	private string? _toolTip;

	/// <summary>Creates a display fact whose value can be refreshed without replacing its UI row.</summary>
	public SelectedTabDetailRowViewModel(string label, string value, string? toolTip = null)
	{
		Label = label;
		_value = value;
		_toolTip = toolTip;
	}

	/// <summary>Stable label used to retain this row across diagnostic refreshes.</summary>
	public string Label { get; }

	/// <summary>Latest formatted value for the fact.</summary>
	public string Value => _value;

	/// <summary>Optional untrimmed value shown when the pointer rests over the row.</summary>
	public string? ToolTip => _toolTip;

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	internal void Apply(SelectedTabDetailRowViewModel snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (!string.Equals(Label, snapshot.Label, StringComparison.Ordinal))
		{
			throw new ArgumentException("A detail row can only apply a snapshot with the same label.", nameof(snapshot));
		}

		SetValue(ref _value, snapshot.Value, nameof(Value));
		SetValue(ref _toolTip, snapshot.ToolTip, nameof(ToolTip));
	}

	private void SetValue<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return;
		}

		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}

/// <summary>Event-driven diagnostic facts for the currently selected tab.</summary>
public sealed class SelectedTabDetailsViewModel : INotifyPropertyChanged
{
	private readonly ObservableCollection<SelectedTabDetailRowViewModel> _rows;
	private string _heading;
	private string _title;

	/// <summary>Creates a diagnostic snapshot that can be updated in place while its tab stays selected.</summary>
	public SelectedTabDetailsViewModel(
		string heading,
		string title,
		IReadOnlyList<SelectedTabDetailRowViewModel> rows)
	{
		_heading = heading;
		_title = title;
		_rows = new ObservableCollection<SelectedTabDetailRowViewModel>(rows);
		Rows = new ReadOnlyObservableCollection<SelectedTabDetailRowViewModel>(_rows);
	}

	/// <summary>Kind of selected item represented by the panel.</summary>
	public string Heading => _heading;

	/// <summary>Current title of the selected item.</summary>
	public string Title => _title;

	/// <summary>Live facts whose unchanged row identities survive polling refreshes.</summary>
	public ReadOnlyObservableCollection<SelectedTabDetailRowViewModel> Rows { get; }

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Applies a newer snapshot while retaining unchanged row instances for active selection.</summary>
	public void UpdateFrom(SelectedTabDetailsViewModel snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		SetValue(ref _heading, snapshot.Heading, nameof(Heading));
		SetValue(ref _title, snapshot.Title, nameof(Title));
		if (_rows.Count == snapshot.Rows.Count
			&& _rows.Select(row => row.Label).SequenceEqual(
				snapshot.Rows.Select(row => row.Label),
				StringComparer.Ordinal))
		{
			for (var index = 0; index < _rows.Count; index++)
			{
				_rows[index].Apply(snapshot.Rows[index]);
			}

			return;
		}

		_rows.Clear();
		foreach (var row in snapshot.Rows)
		{
			_rows.Add(row);
		}
	}

	private void SetValue<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return;
		}

		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}

/// <summary>
/// Projects terminal-classifier, web-monitor, and optional process-tree evidence into display-only
/// facts without exposing or persisting a raw terminal-screen snapshot.
/// </summary>
public static class SelectedTabDetailsFactory
{
	/// <summary>Creates details for a terminal session and its latest runtime snapshots.</summary>
	public static SelectedTabDetailsViewModel Create(
		SessionViewModel session,
		TerminalClassifierDiagnostics? diagnostics,
		ProcessTreeMetricsViewModel? metrics = null)
	{
		ArgumentNullException.ThrowIfNull(session);

		var rows = new List<SelectedTabDetailRowViewModel>
		{
			new("Agent", session.Record.Kind.ToString()),
			new("Lifecycle", diagnostics?.LifecycleStatus.ToString() ?? session.Status),
			new("Classifier", FormatClassifier(diagnostics)),
			new("Indicator", diagnostics?.Indicator.ToString() ?? session.Indicator.ToString()),
			new("Composer", FormatComposer(diagnostics?.PromptIsEmpty)),
			new("Composer evidence", FormatComposerEvidence(diagnostics?.PromptEvidence)),
			new("Input request", FormatInputRequest(diagnostics)),
			new("Activity", FormatActivity(diagnostics)),
			new("Viewport", FormatViewport(diagnostics)),
			new("Working directory", session.Record.WorkingDirectory, session.Record.WorkingDirectory),
			new("Scenario", FormatScenario(session.LockedByScenarioRunId)),
			new("Classified", FormatTimestamp(diagnostics?.LastClassificationAt))
		};
		AppendProcessMetrics(rows, metrics);

		return new SelectedTabDetailsViewModel("Selected terminal", session.Title, rows);
	}

	/// <summary>Creates details for a web page and its latest live-monitor snapshot.</summary>
	public static SelectedTabDetailsViewModel Create(
		WebPageViewModel page,
		WebMonitorDiagnostics? diagnostics,
		WebViewProcessMetricsViewModel? metrics = null,
		bool externalMetricsEnabled = false)
	{
		ArgumentNullException.ThrowIfNull(page);

		var rows = new List<SelectedTabDetailRowViewModel>
		{
			new("Address", page.ResumeUrl, page.ResumeUrl),
			new("Browser", page.IsLoading ? "Loading" : page.IsBrowserLoaded ? "Loaded" : "Paused"),
			new("Monitor", (diagnostics?.Status ?? page.MonitorStatus).ToString()),
			new("Rule", FormatRule(diagnostics)),
			new("Activity", FormatBoolean(diagnostics?.Activity)),
			new("Revision", diagnostics?.Revision ?? "—"),
			new("Unread", (diagnostics?.Unread ?? page.HasMonitorUnread) ? "Yes" : "No"),
			new("Observed", FormatTimestamp(diagnostics?.ObservedAt)),
			new("Polling", FormatPolling(diagnostics)),
			new("Navigation", diagnostics?.Navigating == true || page.IsLoading ? "Navigating" : "Stable")
		};

		if (!string.IsNullOrWhiteSpace(diagnostics?.LastError))
		{
			rows.Add(new("Last error", diagnostics.LastError, diagnostics.LastError));
		}
		AppendWebProcessMetrics(rows, page, metrics, externalMetricsEnabled);

		return new SelectedTabDetailsViewModel("Selected web tab", page.Title, rows);
	}

	private static void AppendWebProcessMetrics(
		List<SelectedTabDetailRowViewModel> rows,
		WebPageViewModel page,
		WebViewProcessMetricsViewModel? metrics,
		bool enabled)
	{
		if (!enabled)
		{
			return;
		}

		if (!page.IsBrowserLoaded)
		{
			rows.Add(new("External metrics", "Not loaded"));
			return;
		}

		if (metrics is null)
		{
			rows.Add(new("External metrics", "Sampling…"));
			return;
		}

		if (!metrics.IsAvailable)
		{
			rows.Add(new(
				"External metrics",
				$"Unavailable — {metrics.Error}",
				metrics.Error));
			return;
		}

		if (!metrics.PageAttributionAvailable)
		{
			AppendProcessGroup(rows, "Runtime", "WebView2 runtime", metrics.SharedRuntime);
			rows.Add(new("Metrics sampled", FormatTimestamp(metrics.SampledAt)));
			return;
		}

		AppendProcessGroup(rows, "Page", "Page renderers", metrics.PageRenderers);
		AppendProcessGroup(rows, "Shared", "Shared runtime", metrics.SharedRuntime);
		rows.Add(new("Metrics sampled", FormatTimestamp(metrics.SampledAt)));
	}

	private static void AppendProcessGroup(
		List<SelectedTabDetailRowViewModel> rows,
		string metricPrefix,
		string processLabel,
		ProcessMetricsGroupViewModel metrics)
	{
		var processUnit = metrics.ProcessCount == 1 ? "process" : "processes";
		rows.Add(new(
			processLabel,
			$"{metrics.ProcessCount.ToString(CultureInfo.InvariantCulture)} {processUnit}"));
		rows.Add(new(
			$"{metricPrefix} CPU",
			metrics.CpuPercent is { } cpu
				? $"{cpu.ToString("0.0", CultureInfo.InvariantCulture)}%"
				: "Sampling…"));
		rows.Add(new(
			$"{metricPrefix} working set",
			$"{(metrics.WorkingSetBytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture)} MiB"));
	}

	private static string FormatClassifier(TerminalClassifierDiagnostics? diagnostics)
	{
		if (diagnostics?.VerdictState is not { } state)
		{
			return "Unknown";
		}

		return string.IsNullOrWhiteSpace(diagnostics.VerdictDescription)
			? state.ToString()
			: $"{state} — {diagnostics.VerdictDescription}";
	}

	private static string FormatComposer(bool? promptIsEmpty) => promptIsEmpty switch
	{
		true => "Empty",
		false => "Has text",
		_ => "Unknown"
	};

	private static string FormatComposerEvidence(TerminalPromptEvidence? evidence)
	{
		if (evidence is null)
		{
			return "Unavailable";
		}

		if (!evidence.PromptFound)
		{
			return "Prompt glyph not found";
		}

		if (!evidence.BoundaryFound)
		{
			return "Prompt found · separator missing";
		}

		var prefix = evidence.SeparatorSharesLogicalLine
			? "Prompt + wrapped separator"
			: "Prompt + separator";
		if (evidence.NonWhitespaceCharacterCount == 0)
		{
			return $"{prefix} · empty";
		}

		var unit = evidence.NonWhitespaceCharacterCount == 1 ? "character" : "characters";
		return $"{prefix} · {evidence.NonWhitespaceCharacterCount.ToString(CultureInfo.InvariantCulture)} non-whitespace {unit}";
	}

	private static string FormatInputRequest(TerminalClassifierDiagnostics? diagnostics)
	{
		if (diagnostics?.InputRequested != true)
		{
			return "No";
		}

		return string.IsNullOrWhiteSpace(diagnostics.StatusLine)
			? "Yes"
			: diagnostics.StatusLine;
	}

	private static string FormatActivity(TerminalClassifierDiagnostics? diagnostics)
	{
		if (diagnostics is null)
		{
			return "Unknown";
		}

		var state = diagnostics.ActivityInProgress ? "Busy" : "Idle";
		return $"{state} · epoch {diagnostics.ActivityEpoch.ToString(CultureInfo.InvariantCulture)}";
	}

	private static string FormatViewport(TerminalClassifierDiagnostics? diagnostics) =>
		diagnostics?.Columns is int columns && diagnostics.Rows is int rows
			? $"{columns.ToString(CultureInfo.InvariantCulture)} × {rows.ToString(CultureInfo.InvariantCulture)}"
			: "Unknown";

	private static string FormatScenario(string? runId)
	{
		if (string.IsNullOrWhiteSpace(runId))
		{
			return "Unlocked";
		}

		var shortId = runId.Length <= 8 ? runId : runId[..8];
		return $"Locked by {shortId}";
	}

	private static void AppendProcessMetrics(
		List<SelectedTabDetailRowViewModel> rows,
		ProcessTreeMetricsViewModel? metrics)
	{
		if (metrics is null)
		{
			return;
		}

		if (!metrics.IsAvailable)
		{
			rows.Add(new(
				"External metrics",
				$"Unavailable — {metrics.Error}",
				metrics.Error));
			return;
		}

		var processLabel = metrics.ProcessCount == 1 ? "process" : "processes";
		rows.Add(new(
			"Process tree",
			$"PID {metrics.RootProcessId.ToString(CultureInfo.InvariantCulture)} · "
				+ $"{metrics.ProcessCount.ToString(CultureInfo.InvariantCulture)} {processLabel}"));
		rows.Add(new(
			"CPU",
			metrics.CpuPercent is { } cpu
				? $"{cpu.ToString("0.0", CultureInfo.InvariantCulture)}%"
				: "Sampling…"));
		rows.Add(new(
			"Working set",
			$"{(metrics.WorkingSetBytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture)} MiB"));
		rows.Add(new("Metrics sampled", FormatTimestamp(metrics.SampledAt)));
	}

	private static string FormatRule(WebMonitorDiagnostics? diagnostics)
	{
		if (string.IsNullOrWhiteSpace(diagnostics?.RuleId))
		{
			return "No matching rule";
		}

		return string.IsNullOrWhiteSpace(diagnostics.RuleTitle)
			? diagnostics.RuleId
			: $"{diagnostics.RuleTitle} ({diagnostics.RuleId})";
	}

	private static string FormatBoolean(bool? value) => value switch
	{
		true => "True",
		false => "False",
		_ => "Unknown"
	};

	private static string FormatPolling(WebMonitorDiagnostics? diagnostics)
	{
		if (diagnostics is null)
		{
			return "Unknown";
		}

		return $"attempt {diagnostics.Attempt.ToString(CultureInfo.InvariantCulture)} · next {FormatTimestamp(diagnostics.NextAttemptAt)}";
	}

	private static string FormatTimestamp(DateTimeOffset? value) => value is null
		? "—"
		: value.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
