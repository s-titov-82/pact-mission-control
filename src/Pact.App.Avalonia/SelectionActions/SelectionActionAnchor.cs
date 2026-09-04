namespace Pact.App.Avalonia.SelectionActions;

internal enum SelectionActionSourceKind
{
	Terminal,
	Notes
}

internal readonly record struct SelectionActionAnchor(
	SelectionActionSourceKind Source,
	double X,
	double Y,
	bool IsAvailable);
