using System.Reflection;
using Pact.Core.Presentation;

namespace Pact.Core.Tests.Presentation;

public sealed class WebPageHostContractTests
{
	[Test]
	public void Contracts_do_not_expose_ui_framework_types()
	{
		Type[] contractTypes = [typeof(IWebPageHost), typeof(IWebPageHostFactory)];
		string[] forbidden = ["System.Windows", "Microsoft.Web.WebView2", "Avalonia", "WebViewControl"];

		var exposedTypes = contractTypes.SelectMany(type =>
			type.GetMembers().SelectMany(GetReferencedTypes));

		exposedTypes.ShouldNotContain(type =>
			forbidden.Any(prefix =>
				(type.FullName ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal)));
	}

	private static IEnumerable<Type> GetReferencedTypes(MemberInfo member) => member switch
	{
		PropertyInfo property => [property.PropertyType],
		EventInfo @event => [@event.EventHandlerType!],
		MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
			.Append(method.ReturnType),
		_ => []
	};
}