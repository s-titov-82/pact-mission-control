using System.ComponentModel;
using System.Text.Json.Nodes;
using Pact.Core.Web.Monitoring;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Reports one isolated current-tab rule evaluation without changing persisted or live monitor state.
/// </summary>
/// <param name="UrlMatched">Whether the current document URL matched the edited rule.</param>
/// <param name="Activity">The normalized activity value, or null when unavailable.</param>
/// <param name="Revision">The normalized revision value, or null when unavailable.</param>
/// <param name="Error">A validation or evaluation error, or null after a successful evaluation.</param>
public sealed record WebMonitorTestResult(
	bool UrlMatched,
	bool? Activity,
	string? Revision,
	string? Error);

/// <summary>
/// Edits web-monitor-rules.json through the node-preserving Settings pipeline and supports
/// one-shot evaluation of the selected rule against the current loaded web tab.
/// </summary>
public sealed class WebMonitoringRulesSectionViewModel :
	FileSectionViewModel<WebMonitorRuleItemViewModel>
{
	private readonly Func<WebMonitorRule, CancellationToken, Task<WebMonitorTestResult>>
		_testCurrentTabAsync;
	private CancellationTokenSource? _testCancellation;
	private long _testGeneration;

	/// <summary>
	/// Creates the file-backed rules section with an isolated current-tab evaluation delegate.
	/// </summary>
	public WebMonitoringRulesSectionViewModel(
		SettingsFileStore store,
		Func<WebMonitorRule, CancellationToken, Task<WebMonitorTestResult>>
			testCurrentTabAsync)
		: base(
			store,
			SettingsSection.WebMonitoringRules,
			"Web monitoring rules",
			"Declarative URL and DOM extractor rules for activity and unread web-tab indicators.",
			"web-monitor-rules.json")
	{
		ArgumentNullException.ThrowIfNull(testCurrentTabAsync);
		_testCurrentTabAsync = testCurrentTabAsync;
		PropertyChanged += OnSectionPropertyChanged;
	}

	/// <summary>Gets the latest one-shot test summary; testing never changes section dirty state.</summary>
	public string? TestResultMessage
	{
		get;
		private set => SetField(ref field, value);
	}

	/// <summary>
	/// Gets whether the selected rule currently has an outstanding current-tab evaluation.
	/// </summary>
	public bool IsTestInProgress
	{
		get;
		private set
		{
			if (SetField(ref field, value))
			{
				OnPropertyChanged(nameof(CanTestSelectedItem));
			}
		}
	}

	/// <summary>
	/// Gets whether the selected rule can start a current-tab evaluation without reentry.
	/// </summary>
	public bool CanTestSelectedItem =>
		!IsTestInProgress && SelectedItem is WebMonitorRuleItemViewModel;

	/// <summary>
	/// Validates and evaluates the selected rule once without writing JSON or replacing live rules.
	/// </summary>
	public async Task TestSelectedItemAsync(CancellationToken cancellationToken)
	{
		if (IsTestInProgress)
		{
			return;
		}

		if (SelectedItem is not WebMonitorRuleItemViewModel item)
		{
			TestResultMessage = "Select a recognized web monitoring rule to test.";
			return;
		}

		var generation = ++_testGeneration;
		using var linkedCancellation =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_testCancellation = linkedCancellation;
		IsTestInProgress = true;
		TestResultMessage = null;
		try
		{
			if (!TryValidateItem(item, out var rule, out var error))
			{
				if (IsCurrentTest(generation, item))
				{
					TestResultMessage = error;
				}

				return;
			}

			var result =
				await _testCurrentTabAsync(rule!, linkedCancellation.Token);
			if (IsCurrentTest(generation, item))
			{
				TestResultMessage = FormatResult(result);
			}
		}
		catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			if (IsCurrentTest(generation, item))
			{
				TestResultMessage = exception.Message;
			}
		}
		finally
		{
			if (_testGeneration == generation)
			{
				_testCancellation = null;
				IsTestInProgress = false;
			}
		}
	}

	/// <inheritdoc />
	public override async Task LoadAsync(CancellationToken cancellationToken)
	{
		CancelCurrentTest();
		await base.LoadAsync(cancellationToken);
		TestResultMessage = null;
	}

	/// <summary>
	/// Cancels the outstanding current-tab evaluation and invalidates any eventual stale result.
	/// </summary>
	public void CancelCurrentTest()
	{
		++_testGeneration;
		var cancellation = _testCancellation;
		_testCancellation = null;
		cancellation?.Cancel();
		IsTestInProgress = false;
		TestResultMessage = null;
	}

	/// <inheritdoc />
	protected override WebMonitorRuleItemViewModel? TryCreateItem(JsonObject node) =>
		WebMonitorRuleItemViewModel.HasSupportedShape(node)
			? new WebMonitorRuleItemViewModel(node)
			: null;

	/// <inheritdoc />
	protected override WebMonitorRuleItemViewModel CreateNewItem(JsonObject node) => new(node);

	/// <inheritdoc />
	protected override string? Validate()
	{
		var rules =
			Items.OfType<WebMonitorRuleItemViewModel>().ToList();

		foreach (var item in rules)
		{
			if (!TryValidateItem(item, out _, out var error))
			{
				return error;
			}
		}

		var uniqueIdCount = rules
			.Select(rule => rule.Id)
			.Distinct(StringComparer.Ordinal)
			.Count();
		return uniqueIdCount == rules.Count
			? null
			: "Web monitoring rule ids must be unique.";
	}

	private static bool TryValidateItem(
		WebMonitorRuleItemViewModel item,
		out WebMonitorRule? rule,
		out string? error)
	{
		if (!item.TryCreateRule(out var parsedRule, out error))
		{
			rule = null;
			return false;
		}

		var validation =
			WebMonitorRuleCompiler.Validate(parsedRule);
		if (!validation.IsValid)
		{
			rule = null;
			error = $"Web monitoring rule '{DisplayId(item.Id)}': {validation.Errors[0]}";
			return false;
		}

		rule = parsedRule;
		error = null;
		return true;
	}

	private static string FormatResult(WebMonitorTestResult result)
	{
		if (!string.IsNullOrWhiteSpace(result.Error))
		{
			return result.Error;
		}

		if (!result.UrlMatched)
		{
			return "Current tab URL did not match this rule.";
		}

		var activity = result.Activity?.ToString().ToLowerInvariant() ?? "unknown";
		var revision = result.Revision ?? "unknown";
		return $"Current tab URL matched. Activity: {activity}. Revision: {revision}.";
	}

	private static string DisplayId(string id) =>
		string.IsNullOrWhiteSpace(id) ? "(blank id)" : id;

	private bool IsCurrentTest(long generation, WebMonitorRuleItemViewModel item) =>
		_testGeneration == generation && ReferenceEquals(SelectedItem, item);

	private void OnSectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(SelectedItem))
		{
			return;
		}

		CancelCurrentTest();
		OnPropertyChanged(nameof(CanTestSelectedItem));
	}
}
