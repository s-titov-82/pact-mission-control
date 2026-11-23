using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.App.Avalonia.Views.Settings;

/// <summary>Gives each selected reviewer its own editor so two-way controls never rebind across profiles.</summary>
public sealed class ReviewerEditorTemplate : IDataTemplate
{
	/// <summary>The reviewer form built for each selection.</summary>
	public IDataTemplate? Form { get; set; }

	/// <inheritdoc />
	public bool Match(object? data) => data is ReviewProfileItemViewModel;

	/// <inheritdoc />
	public Control? Build(object? param)
	{
		var editor = Form?.Build(param);
		editor?.DataContext = param;
		return editor;
	}
}
