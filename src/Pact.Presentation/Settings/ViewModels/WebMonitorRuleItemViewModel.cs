using System.Globalization;
using System.Text.Json.Nodes;
using Pact.Core.Web.Monitoring;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Edits one optional activity or revision extractor while retaining its original JSON node.
/// </summary>
public sealed class WebMonitorExtractorItemViewModel : SettingsObservableObject
{
	private string _selector;
	private WebMonitorValueSource? _source;
	private string? _rawSource;
	private string _attributeName;
	private string _matchPattern;
	private string _captureGroupText;

	internal WebMonitorExtractorItemViewModel(JsonObject node)
	{
		ArgumentNullException.ThrowIfNull(node);

		Node = node;
		_selector = (string?)node["selector"] ?? string.Empty;
		_rawSource = (string?)node["source"];
		_source = ParseSource(_rawSource);
		_attributeName = (string?)node["attributeName"] ?? string.Empty;
		_matchPattern = (string?)node["matchPattern"] ?? string.Empty;
		_captureGroupText = node["captureGroup"] is JsonValue captureGroup
			&& captureGroup.TryGetValue(out int parsedCaptureGroup)
				? parsedCaptureGroup.ToString(CultureInfo.InvariantCulture)
				: string.Empty;
	}

	/// <summary>Raised when an editable extractor field changes.</summary>
	public event EventHandler? Changed;

	/// <summary>Gets or sets the CSS selector evaluated by the browser adapter.</summary>
	public string Selector
	{
		get => _selector;
		set
		{
			if (SetField(ref _selector, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>
	/// Gets or sets which supported value is read, or null when the persisted source is unknown.
	/// </summary>
	public WebMonitorValueSource? Source
	{
		get => _source;
		set
		{
			if (SetField(ref _source, value))
			{
				if (value is not null)
				{
					_rawSource = value.Value.ToString().ToLowerInvariant();
				}

				OnPropertyChanged(nameof(ShowAttributeName));
				OnPropertyChanged(nameof(ShowMatchPattern));
				OnPropertyChanged(nameof(ShowCaptureGroup));
				OnPropertyChanged(nameof(HasUnresolvedSource));
				OnPropertyChanged(nameof(SourceWarning));
				RaiseChanged();
			}
		}
	}

	/// <summary>Gets or sets the attribute read when <see cref="Source"/> is Attribute.</summary>
	public string AttributeName
	{
		get => _attributeName;
		set
		{
			if (SetField(ref _attributeName, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Gets or sets the optional portable regular expression applied to the extracted value.</summary>
	public string MatchPattern
	{
		get => _matchPattern;
		set
		{
			if (SetField(ref _matchPattern, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Gets or sets the optional numeric capture group as form-editable text.</summary>
	public string CaptureGroupText
	{
		get => _captureGroupText;
		set
		{
			if (SetField(ref _captureGroupText, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Gets whether the attribute-name field applies to the selected source.</summary>
	public bool ShowAttributeName => Source == WebMonitorValueSource.Attribute;

	/// <summary>Gets whether the match-pattern field applies to the selected source.</summary>
	public bool ShowMatchPattern =>
		Source is WebMonitorValueSource.Text or WebMonitorValueSource.Attribute;

	/// <summary>Gets whether the capture-group field applies to the selected source.</summary>
	public bool ShowCaptureGroup => ShowMatchPattern;

	/// <summary>Gets whether the persisted source string is not a supported enum value.</summary>
	public bool HasUnresolvedSource => Source is null;

	/// <summary>Gets guidance for replacing an unresolved persisted source string.</summary>
	public string? SourceWarning => HasUnresolvedSource
		? $"Saved source '{_rawSource ?? "(missing)"}' is unsupported. Choose a supported source."
		: null;

	internal JsonObject Node { get; }

	internal bool TryCreateExtractor(
		string label,
		out WebMonitorExtractor extractor,
		out string? error)
	{
		if (Source is not { } source)
		{
			extractor = default!;
			error = $"{label} source '{_rawSource ?? "(missing)"}' is unsupported.";
			return false;
		}

		int? captureGroup = null;
		if (!string.IsNullOrWhiteSpace(CaptureGroupText))
		{
			if (!int.TryParse(
					CaptureGroupText,
					NumberStyles.Integer,
					CultureInfo.InvariantCulture,
					out var parsedCaptureGroup))
			{
				extractor = default!;
				error = $"{label} capture group must be a whole number.";
				return false;
			}

			captureGroup = parsedCaptureGroup;
		}

		extractor = new WebMonitorExtractor(
			Selector,
			source,
			NullIfWhiteSpace(AttributeName),
			NullIfWhiteSpace(MatchPattern),
			captureGroup);
		error = null;
		return true;
	}

	internal void WriteTo()
	{
		Node["selector"] = Selector;
		Node["source"] = Source is { } source
			? source.ToString().ToLowerInvariant()
			: _rawSource;
		Node["attributeName"] = NullIfWhiteSpace(AttributeName);
		Node["matchPattern"] = NullIfWhiteSpace(MatchPattern);
		Node["captureGroup"] = int.TryParse(
			CaptureGroupText,
			NumberStyles.Integer,
			CultureInfo.InvariantCulture,
			out var captureGroup)
				? captureGroup
				: null;
	}

	private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

	private static WebMonitorValueSource? ParseSource(string? source)
	{
		if (source is null)
		{
			return WebMonitorValueSource.Text;
		}

		return Enum.TryParse(source, ignoreCase: true, out WebMonitorValueSource parsed)
			&& Enum.IsDefined(parsed)
				? parsed
				: null;
	}

	private static string? NullIfWhiteSpace(string value) =>
		string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Represents one editable web-monitoring rule tab backed by a node in web-monitor-rules.json.
/// </summary>
public sealed class WebMonitorRuleItemViewModel : SettingsItemViewModelBase
{
	private const string DisabledActivityPropertyName = "$pactDisabledActivity";
	private const string DisabledRevisionPropertyName = "$pactDisabledRevision";
	private readonly JsonObject _node;
	private string _id;
	private string _title;
	private bool _enabled;
	private string _urlPattern;
	private string _pollIntervalSecondsText;
	private bool _hasActivityExtractor;
	private bool _hasRevisionExtractor;
	private bool _preserveActivityExtractor;
	private bool _preserveRevisionExtractor;

	/// <summary>Creates an editable rule over <paramref name="node"/> without discarding unknown properties.</summary>
	public WebMonitorRuleItemViewModel(JsonObject node)
	{
		ArgumentNullException.ThrowIfNull(node);

		_node = node;
		_id = (string?)node["id"] ?? string.Empty;
		_title = (string?)node["title"] ?? string.Empty;
		_enabled = (bool?)node["enabled"] ?? false;
		_urlPattern = (string?)node["urlPattern"] ?? string.Empty;
		_pollIntervalSecondsText = node["pollIntervalSeconds"] is JsonValue interval
			&& interval.TryGetValue(out int parsedInterval)
				? parsedInterval.ToString(CultureInfo.InvariantCulture)
				: "30";

		var activeActivity = node["activity"] as JsonObject;
		var activeRevision = node["revision"] as JsonObject;
		var disabledActivity = node[DisabledActivityPropertyName] as JsonObject;
		var disabledRevision = node[DisabledRevisionPropertyName] as JsonObject;
		var activityNode = activeActivity ?? disabledActivity ?? new JsonObject();
		var revisionNode = activeRevision ?? disabledRevision ?? new JsonObject();
		_hasActivityExtractor = activeActivity is not null;
		_hasRevisionExtractor = activeRevision is not null;
		_preserveActivityExtractor = activeActivity is not null || disabledActivity is not null;
		_preserveRevisionExtractor = activeRevision is not null || disabledRevision is not null;

		ActivityExtractor = new WebMonitorExtractorItemViewModel(activityNode);
		RevisionExtractor = new WebMonitorExtractorItemViewModel(revisionNode);
		ActivityExtractor.Changed += OnExtractorChanged;
		RevisionExtractor.Changed += OnExtractorChanged;
	}

	internal static bool HasSupportedShape(JsonObject node) => HasRequiredString(node["id"])
			&& HasOptionalValue<string>(node["title"])
			&& HasOptionalValue<bool>(node["enabled"])
			&& HasOptionalValue<string>(node["urlPattern"])
			&& HasOptionalValue<int>(node["pollIntervalSeconds"])
			&& HasSupportedExtractorShape(node["activity"])
			&& HasSupportedExtractorShape(node["revision"]);

	/// <summary>Gets the original JSON node updated in place during section save.</summary>
	public override JsonObject Node => _node;

	/// <summary>Gets the value sources supported by an activity extractor.</summary>
	public static IReadOnlyList<WebMonitorValueSource> ActivitySourceOptions { get; } = [
		WebMonitorValueSource.Exists,
		WebMonitorValueSource.Count,
		WebMonitorValueSource.Text,
		WebMonitorValueSource.Attribute
	];

	/// <summary>Gets the value sources supported by a revision extractor.</summary>
	public static IReadOnlyList<WebMonitorValueSource> RevisionSourceOptions { get; } = [
		WebMonitorValueSource.Text,
		WebMonitorValueSource.Attribute
	];

	/// <summary>Gets or sets the stable rule identifier.</summary>
	public string Id
	{
		get => _id;
		set
		{
			if (SetField(ref _id, value))
			{
				OnPropertyChanged(nameof(TabHeader));
				RaiseChanged();
			}
		}
	}

	/// <summary>Gets or sets the user-facing rule title.</summary>
	public string Title
	{
		get => _title;
		set
		{
			if (SetField(ref _title, value))
			{
				OnPropertyChanged(nameof(TabHeader));
				RaiseChanged();
			}
		}
	}

	/// <summary>Gets or sets whether the rule participates in live monitoring.</summary>
	public bool Enabled
	{
		get => _enabled;
		set
		{
			if (SetField(ref _enabled, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Gets or sets the regular expression matched against normalized absolute URLs.</summary>
	public string UrlPattern
	{
		get => _urlPattern;
		set
		{
			if (SetField(ref _urlPattern, value))
			{
				OnPropertyChanged(nameof(HasChangeMeMarker));
				OnPropertyChanged(nameof(MarkerWarning));
				RaiseChanged();
			}
		}
	}

	/// <summary>Gets or sets the polling interval in seconds as form-editable text.</summary>
	public string PollIntervalSecondsText
	{
		get => _pollIntervalSecondsText;
		set
		{
			if (SetField(ref _pollIntervalSecondsText, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Gets or sets whether the rule includes its activity extractor.</summary>
	public bool HasActivityExtractor
	{
		get => _hasActivityExtractor;
		set
		{
			if (SetField(ref _hasActivityExtractor, value))
			{
				if (value)
				{
					_preserveActivityExtractor = true;
				}

				RaiseChanged();
			}
		}
	}

	/// <summary>Gets the editable activity extractor fields, retained while the extractor is disabled.</summary>
	public WebMonitorExtractorItemViewModel ActivityExtractor { get; }

	/// <summary>Gets or sets whether the rule includes its revision extractor.</summary>
	public bool HasRevisionExtractor
	{
		get => _hasRevisionExtractor;
		set
		{
			if (SetField(ref _hasRevisionExtractor, value))
			{
				if (value)
				{
					_preserveRevisionExtractor = true;
				}

				OnPropertyChanged(nameof(HasUnsupportedRevisionSource));
				OnPropertyChanged(nameof(RevisionSourceWarning));
				RaiseChanged();
			}
		}
	}

	/// <summary>Gets the editable revision extractor fields, retained while the extractor is disabled.</summary>
	public WebMonitorExtractorItemViewModel RevisionExtractor { get; }

	/// <summary>
	/// Gets whether a persisted revision source is outside the choices supported by the form.
	/// </summary>
	public bool HasUnsupportedRevisionSource =>
		HasRevisionExtractor
		&& (RevisionExtractor.Source is not { } source
			|| !RevisionSourceOptions.Contains(source));

	/// <summary>
	/// Gets guidance for correcting an unsupported persisted revision source without rewriting it.
	/// </summary>
	public string? RevisionSourceWarning => HasUnsupportedRevisionSource
		? RevisionExtractor.Source is null
			? RevisionExtractor.SourceWarning
			: $"Saved revision source '{RevisionExtractor.Source}' is unsupported. Choose Text or Attribute."
		: null;

	/// <summary>Gets whether the URL still contains a starter hostname marker.</summary>
	public bool HasChangeMeMarker =>
		UrlPattern.Contains("CHANGE-ME-", StringComparison.Ordinal);

	/// <summary>Gets the marker warning displayed by the Settings form, or null after customization.</summary>
	public string? MarkerWarning => HasChangeMeMarker
		? "Replace the CHANGE-ME- hostname marker before enabling this rule."
		: null;

	/// <summary>Gets the title/id fallback plus the standard dirty marker used by item tabs.</summary>
	public override string TabHeader
	{
		get
		{
			var name = !string.IsNullOrWhiteSpace(Title)
				? Title
				: !string.IsNullOrWhiteSpace(Id) ? Id : "(new rule)";
			return IsItemDirty ? $"{name} •" : name;
		}
	}

	internal bool TryCreateRule(out WebMonitorRule rule, out string? error)
	{
		if (!TryValidateDisabledBackup(
				DisabledActivityPropertyName,
				HasActivityExtractor,
				out error)
			|| !TryValidateDisabledBackup(
				DisabledRevisionPropertyName,
				HasRevisionExtractor,
				out error))
		{
			rule = default!;
			return false;
		}

		if (!int.TryParse(
				PollIntervalSecondsText,
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out var pollIntervalSeconds))
		{
			rule = default!;
			error = "Poll interval must be a whole number of seconds.";
			return false;
		}

		WebMonitorExtractor? activity = null;
		if (HasActivityExtractor
			&& !ActivityExtractor.TryCreateExtractor(
				"Activity extractor",
				out activity,
				out error))
		{
			rule = default!;
			return false;
		}

		WebMonitorExtractor? revision = null;
		if (HasRevisionExtractor
			&& !RevisionExtractor.TryCreateExtractor(
				"Revision extractor",
				out revision,
				out error))
		{
			rule = default!;
			return false;
		}

		rule = new WebMonitorRule(
			Id,
			Title,
			Enabled,
			UrlPattern,
			pollIntervalSeconds,
			activity,
			revision);
		error = null;
		return true;
	}

	internal override void WriteTo()
	{
		_node["id"] = Id;
		_node["title"] = Title;
		_node["enabled"] = Enabled;
		_node["urlPattern"] = UrlPattern;
		_node["pollIntervalSeconds"] = int.TryParse(
			PollIntervalSecondsText,
			NumberStyles.Integer,
			CultureInfo.InvariantCulture,
			out var pollIntervalSeconds)
				? pollIntervalSeconds
				: 0;

		WriteExtractor(
			"activity",
			DisabledActivityPropertyName,
			HasActivityExtractor,
			_preserveActivityExtractor,
			ActivityExtractor);
		WriteExtractor(
			"revision",
			DisabledRevisionPropertyName,
			HasRevisionExtractor,
			_preserveRevisionExtractor,
			RevisionExtractor);
	}

	private void WriteExtractor(
		string propertyName,
		string disabledPropertyName,
		bool included,
		bool preserveWhenDisabled,
		WebMonitorExtractorItemViewModel extractor)
	{
		if (!included)
		{
			_node[propertyName] = null;
			if (!preserveWhenDisabled)
			{
				return;
			}

			extractor.WriteTo();
			if (ReferenceEquals(_node[disabledPropertyName], extractor.Node))
			{
				return;
			}

			if (_node[disabledPropertyName] is JsonObject)
			{
				_node.Remove(disabledPropertyName);
			}

			_node[disabledPropertyName] = extractor.Node;
			return;
		}

		extractor.WriteTo();
		if (ReferenceEquals(_node[disabledPropertyName], extractor.Node))
		{
			_node.Remove(disabledPropertyName);
		}

		if (!ReferenceEquals(_node[propertyName], extractor.Node))
		{
			_node[propertyName] = extractor.Node;
		}

		if (_node[disabledPropertyName] is JsonObject)
		{
			_node.Remove(disabledPropertyName);
		}
	}

	private void OnExtractorChanged(object? sender, EventArgs e)
	{
		if (ReferenceEquals(sender, ActivityExtractor))
		{
			_preserveActivityExtractor = true;
		}
		else if (ReferenceEquals(sender, RevisionExtractor))
		{
			_preserveRevisionExtractor = true;
			OnPropertyChanged(nameof(HasUnsupportedRevisionSource));
			OnPropertyChanged(nameof(RevisionSourceWarning));
		}

		RaiseChanged();
	}

	private bool TryValidateDisabledBackup(
		string propertyName,
		bool extractorEnabled,
		out string? error)
	{
		if (!extractorEnabled
			&& _node[propertyName] is not null
			&& _node[propertyName] is not JsonObject)
		{
			error = $"{propertyName} must be a JSON object before this section can be saved.";
			return false;
		}

		error = null;
		return true;
	}

	private static bool HasSupportedExtractorShape(JsonNode? node) => node is null
			|| node is JsonObject extractor
			&& HasOptionalValue<string>(extractor["selector"])
			&& HasOptionalValue<string>(extractor["source"])
			&& HasOptionalValue<string>(extractor["attributeName"])
			&& HasOptionalValue<string>(extractor["matchPattern"])
			&& HasOptionalValue<int>(extractor["captureGroup"]);

	private static bool HasRequiredString(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? _);

	private static bool HasOptionalValue<T>(JsonNode? node) => node is null
			|| node is JsonValue value && value.TryGetValue(out T? _);
}