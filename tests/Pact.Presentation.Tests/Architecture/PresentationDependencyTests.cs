using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.Architecture;

public sealed class PresentationDependencyTests
{
	[Test]
	public void PresentationAssembly_HasNoUiFrameworkReferences()
	{
		string[] forbidden =
		[
			"PresentationFramework",
			"PresentationCore",
			"WindowsBase",
			"Avalonia",
			"Microsoft.Web.WebView2",
			"WebViewControl"
		];

		var references = typeof(MainWindowViewModel).Assembly
			.GetReferencedAssemblies()
			.Select(reference => reference.Name ?? string.Empty)
			.ToArray();

		references.ShouldNotContain(name =>
			forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
	}
}