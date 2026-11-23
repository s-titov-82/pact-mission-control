using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;

namespace Pact.App.Avalonia.Views;

/// <summary>Raises Click only when the user confirms removal on the same focused row.</summary>
internal sealed class InlineDeleteButton : Button
{
	private object? _originalContent;
	private object? _originalToolTip;
	private string? _originalName;
	private bool _armed;

	/// <summary>Creates a removal action that resets on focus loss or row reuse.</summary>
	public InlineDeleteButton()
	{
		LostFocus += (_, _) => Cancel();
		DataContextChanged += (_, _) => Cancel();
		DetachedFromVisualTree += (_, _) => Cancel();
	}

	protected override Type StyleKeyOverride => typeof(Button);

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		if (e.ClickCount > 1 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
		{
			// Confirmation requires a separate gesture. A double-click may arm the action once.
			e.Handled = true;
			return;
		}

		base.OnPointerPressed(e);
	}

	protected override void OnClick()
	{
		if (_armed)
		{
			Cancel();
			base.OnClick();
			return;
		}

		Focus();
		_armed = true;
		_originalContent = Content;
		_originalToolTip = ToolTip.GetTip(this);
		_originalName = AutomationProperties.GetName(this);
		Classes.Remove("compact-icon");
		Classes.Add("confirm-delete");
		Content = "Confirm delete";
		ToolTip.SetTip(this, "Confirm delete");
		AutomationProperties.SetName(this, "Confirm delete");
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (_armed && e.Key == Key.Escape)
		{
			Cancel();
			e.Handled = true;
			return;
		}

		base.OnKeyDown(e);
	}

	private void Cancel()
	{
		if (!_armed)
		{
			return;
		}

		_armed = false;
		Content = _originalContent;
		ToolTip.SetTip(this, _originalToolTip);
		AutomationProperties.SetName(this, _originalName);
		Classes.Remove("confirm-delete");
		Classes.Add("compact-icon");
	}
}
