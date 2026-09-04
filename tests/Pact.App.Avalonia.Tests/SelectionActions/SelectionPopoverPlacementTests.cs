using Avalonia;
using Pact.App.Avalonia.SelectionActions;

namespace Pact.App.Avalonia.Tests.SelectionActions;

public sealed class SelectionPopoverPlacementTests
{
	[Test]
	public void Calculate_opens_right_and_aligns_divider_with_anchor()
	{
		var result = SelectionPopoverPlacementCalculator.Calculate(
			new Point(400, 300),
			new Size(900, 700),
			popupWidth: 320,
			desiredActionsHeight: 180,
			desiredTargetsHeight: 220,
			dividerHeight: 1);

		result.OpensLeft.ShouldBeFalse();
		result.Bounds.X.ShouldBe(408);
		result.Bounds.Y.ShouldBe(120);
		(result.Bounds.Y + result.ActionsHeight).ShouldBe(300);
	}

	[Test]
	public void Calculate_opens_left_when_right_edge_would_overflow()
	{
		var result = SelectionPopoverPlacementCalculator.Calculate(
			new Point(700, 300),
			new Size(900, 700),
			popupWidth: 320,
			desiredActionsHeight: 180,
			desiredTargetsHeight: 220,
			dividerHeight: 1);

		result.OpensLeft.ShouldBeTrue();
		result.Bounds.X.ShouldBe(372);
		result.Bounds.Y.ShouldBe(120);
	}

	[Test]
	public void Calculate_constrains_actions_height_to_space_above_anchor()
	{
		var result = SelectionPopoverPlacementCalculator.Calculate(
			new Point(400, 60),
			new Size(900, 700),
			popupWidth: 320,
			desiredActionsHeight: 180,
			desiredTargetsHeight: 220,
			dividerHeight: 1);

		result.ActionsHeight.ShouldBe(52);
		result.TargetsHeight.ShouldBe(220);
		result.Bounds.Y.ShouldBe(8);
		result.Bounds.Height.ShouldBe(273);
		result.Bounds.Bottom.ShouldBe(281);
		(result.Bounds.Y + result.ActionsHeight).ShouldBe(60);
	}

	[Test]
	public void Calculate_constrains_targets_height_to_space_below_anchor()
	{
		var result = SelectionPopoverPlacementCalculator.Calculate(
			new Point(400, 650),
			new Size(900, 700),
			popupWidth: 320,
			desiredActionsHeight: 180,
			desiredTargetsHeight: 220,
			dividerHeight: 1);

		result.ActionsHeight.ShouldBe(180);
		result.TargetsHeight.ShouldBe(41);
		result.Bounds.Y.ShouldBe(470);
		result.Bounds.Height.ShouldBe(222);
		result.Bounds.Bottom.ShouldBe(692);
		(result.Bounds.Y + result.ActionsHeight).ShouldBe(650);
	}

	[Test]
	public void Calculate_clamps_wider_popup_to_left_margin()
	{
		var result = SelectionPopoverPlacementCalculator.Calculate(
			new Point(200, 300),
			new Size(400, 700),
			popupWidth: 500,
			desiredActionsHeight: 180,
			desiredTargetsHeight: 220,
			dividerHeight: 1);

		result.OpensLeft.ShouldBeTrue();
		result.Bounds.X.ShouldBe(8);
	}

	[Test]
	public void Calculate_clamps_out_of_pane_anchor_before_placement()
	{
		var result = SelectionPopoverPlacementCalculator.Calculate(
			new Point(-20, 800),
			new Size(900, 700),
			popupWidth: 320,
			desiredActionsHeight: 180,
			desiredTargetsHeight: 220,
			dividerHeight: 1);

		result.OpensLeft.ShouldBeFalse();
		result.Bounds.X.ShouldBe(16);
		result.Bounds.Y.ShouldBe(512);
		result.ActionsHeight.ShouldBe(180);
		result.TargetsHeight.ShouldBe(0);
		result.Bounds.Bottom.ShouldBe(692);
		(result.Bounds.Y + result.ActionsHeight).ShouldBe(692);
	}
}
