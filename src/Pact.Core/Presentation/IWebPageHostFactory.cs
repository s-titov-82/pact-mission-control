namespace Pact.Core.Presentation;

/// <summary>
/// Creates the platform web view backing one browser tab, keeping the presentation layer free
/// of any direct WebView2 dependency.
/// </summary>
public interface IWebPageHostFactory
{
	/// <summary>
	/// Creates a host for the page identified by <paramref name="id"/>. The caller owns the
	/// returned host and must dispose it when the tab closes; hosts are not pooled or reused.
	/// </summary>
	Task<IWebPageHost> CreateAsync(string id, CancellationToken cancellationToken);
}