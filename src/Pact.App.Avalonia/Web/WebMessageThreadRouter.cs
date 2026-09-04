namespace Pact.App.Avalonia.Web;

internal static class WebMessageThreadRouter
{
	public static void Route(bool hasUiThreadAccess, Action handle, Action<Action> post)
	{
		ArgumentNullException.ThrowIfNull(handle);
		ArgumentNullException.ThrowIfNull(post);

		if (hasUiThreadAccess)
		{
			handle();
			return;
		}

		post(handle);
	}
}