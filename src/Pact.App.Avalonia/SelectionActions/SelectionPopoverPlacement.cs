using Avalonia;

namespace Pact.App.Avalonia.SelectionActions;

internal readonly record struct SelectionPopoverPlacement(
	Rect Bounds,
	double ActionsHeight,
	double TargetsHeight,
	bool OpensLeft);

internal static class SelectionPopoverPlacementCalculator
{
	internal static SelectionPopoverPlacement Calculate(
		Point anchor,
		Size paneSize,
		double popupWidth,
		double desiredActionsHeight,
		double desiredTargetsHeight,
		double dividerHeight,
		double gap = 8,
		double margin = 8)
	{
		anchor = new Point(
			Math.Clamp(anchor.X, margin, Math.Max(margin, paneSize.Width - margin)),
			Math.Clamp(anchor.Y, margin, Math.Max(margin, paneSize.Height - margin)));

		var topSpace = Math.Max(0, anchor.Y - margin);
		var bottomSpace = Math.Max(0, paneSize.Height - anchor.Y - margin);
		var actionsHeight = Math.Min(desiredActionsHeight, topSpace);
		var renderedDividerHeight = Math.Min(dividerHeight, bottomSpace);
		var targetsHeight = Math.Min(desiredTargetsHeight, Math.Max(0, bottomSpace - renderedDividerHeight));
		var rightX = anchor.X + gap;
		var opensLeft = rightX + popupWidth > paneSize.Width - margin;
		var x = opensLeft ? anchor.X - gap - popupWidth : rightX;
		x = Math.Clamp(x, margin, Math.Max(margin, paneSize.Width - margin - popupWidth));
		var y = anchor.Y - actionsHeight;

		return new SelectionPopoverPlacement(
			new Rect(x, y, popupWidth, actionsHeight + renderedDividerHeight + targetsHeight),
			actionsHeight,
			targetsHeight,
			opensLeft);
	}
}
