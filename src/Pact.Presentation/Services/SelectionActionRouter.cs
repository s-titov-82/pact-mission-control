using Pact.Core.Prompting;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Services;

/// <summary>
/// Renders the chosen selection action and delivers it to a session or to project notes.
/// </summary>
public sealed class SelectionActionRouter
{
	private readonly MainWindowViewModel _viewModel;
	private readonly PromptTemplateRenderer _renderer;
	private readonly Func<SessionViewModel, string, bool, CancellationToken, Task> _sendAsync;

	/// <summary>
	/// Creates a router.
	/// </summary>
	/// <param name="viewModel">Window state supplying the current choice and project lookup.</param>
	/// <param name="renderer">Renderer for template placeholders.</param>
	/// <param name="sendAsync">
	/// Delivers text to a session; the boolean argument requests auto-submit.
	/// </param>
	public SelectionActionRouter(
		MainWindowViewModel viewModel,
		PromptTemplateRenderer renderer,
		Func<SessionViewModel, string, bool, CancellationToken, Task> sendAsync)
	{
		_viewModel = viewModel;
		_renderer = renderer;
		_sendAsync = sendAsync;
	}

	/// <summary>
	/// Renders the text an action will deliver.
	/// </summary>
	/// <param name="choice">Chosen action, or <see langword="null"/> for a plain send.</param>
	/// <param name="selectionText">Captured terminal selection.</param>
	/// <param name="targetSession">
	/// Destination session, or <see langword="null"/> when targeting notes. With no session
	/// there is no project or task context, so only <c>{selectedText}</c> is substituted.
	/// </param>
	/// <returns>
	/// The rendered text, or the raw selection when no template applies.
	/// </returns>
	public string BuildText(SelectionActionChoiceViewModel? choice, string selectionText, SessionViewModel? targetSession)
	{
		if (choice?.Template is not PromptTemplateRecord template)
		{
			return selectionText;
		}

		if (targetSession is null)
		{
			return template.Body.Replace("{selectedText}", selectionText, StringComparison.Ordinal);
		}

		var workspace = _viewModel.Workspaces.FirstOrDefault(item => item.Sessions.Contains(targetSession));
		return _renderer.Render(template.Body, new Dictionary<string, string>
		{
			["project"] = workspace?.Name ?? string.Empty,
			["task"] = targetSession.Record.Title,
			["selectedText"] = selectionText,
			["otherSessionSummary"] = string.Empty
		});
	}

	/// <summary>
	/// Renders the current action and sends it to <paramref name="target"/>, auto-submitting
	/// when the template requests it.
	/// </summary>
	/// <exception cref="InvalidOperationException">
	/// The template's action type cannot target this session's agent — for example a prompt
	/// aimed at a plain shell.
	/// </exception>
	public async Task SendToSessionAsync(SessionViewModel target, string selectionText, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(target);

		var template = _viewModel.SelectedSelectionAction?.Template;
		if (template is not null && !PromptActionPolicy.CanTarget(template.EffectiveType, target.Record.Kind))
		{
			throw new InvalidOperationException("Selection action cannot target this session.");
		}

		await _sendAsync(
			target,
			BuildText(_viewModel.SelectedSelectionAction, selectionText, target),
			PromptActionPolicy.ShouldSubmit(template),
			cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Renders the current action and appends it to the project's notes, which never overwrites
	/// existing notes.
	/// </summary>
	public async Task SendToNotesAsync(
		string projectId,
		string selectionText,
		CancellationToken cancellationToken)
	{
		var text = BuildText(_viewModel.SelectedSelectionAction, selectionText, targetSession: null);
		await _viewModel.AppendToProjectNotesAsync(
			projectId,
			text,
			cancellationToken).ConfigureAwait(false);
	}
}
