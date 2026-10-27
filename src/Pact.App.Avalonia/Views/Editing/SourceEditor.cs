using AvaloniaEdit;
using AvaloniaEdit.TextMate;

namespace Pact.App.Avalonia.Views.Editing;

/// <summary>
/// The app's text editor: monospace source coloured by file type, with the caret and selection
/// read and set by offsets as on a text box.
/// </summary>
internal sealed class SourceEditor : TextEditor
{
	private readonly TextMate.Installation _syntax;

	public SourceEditor()
	{
		Options.EnableHyperlinks = false;
		Options.EnableEmailHyperlinks = false;
		_syntax = EditorSyntax.Install(this);
	}

	/// <inheritdoc />
	protected override Type StyleKeyOverride => typeof(TextEditor);

	/// <summary>The grammar colouring the text; <see langword="null"/> for plain text.</summary>
	public string? SyntaxScope { get; private set; }

	/// <summary>Colours the text as the named file's language, or not at all.</summary>
	public void SetSyntaxFor(string? path)
	{
		var scope = path is null ? null : EditorSyntax.ScopeFor(path);
		if (scope == SyntaxScope)
		{
			return;
		}

		SyntaxScope = scope;
		_syntax.SetGrammar(scope);
	}

	/// <summary>Caret offset in the text.</summary>
	public int CaretIndex
	{
		get => CaretOffset;
		set => CaretOffset = value;
	}

	/// <summary>Offset where the selection ends; setting it selects from <see cref="TextEditor.SelectionStart"/>.</summary>
	public int SelectionEnd
	{
		get => SelectionStart + SelectionLength;
		set
		{
			var start = SelectionStart;
			Select(Math.Min(start, value), Math.Abs(value - start));
		}
	}
}
