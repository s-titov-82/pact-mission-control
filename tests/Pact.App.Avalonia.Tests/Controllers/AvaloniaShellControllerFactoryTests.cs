using Avalonia.Headless.NUnit;
using Microsoft.Extensions.DependencyInjection;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class AvaloniaShellControllerFactoryTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();

	[AvaloniaTest]
	public async Task Factory_creates_one_shell_with_DI_owned_coordinators()
	{
		AppPaths paths = new(_temporaryDirectory.Path);
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(
			paths.AgentControlSettingsPath,
			$$"""{"port":{{ShellControllerTestBuilder.FreePort()}}}""");
		await using var services = CompositionRoot.BuildServiceProvider(
			new AppDataProfile("factory-test", _temporaryDirectory.Path));
		var factory =
			services.GetRequiredService<AvaloniaShellControllerFactory>();
		FakeTerminalWebViewHost terminalHost = new();
		FakeWebPageHostFactory webPageHostFactory = new();

		await using var controller = factory.Create(
			terminalHost,
			webPageHostFactory);

		controller.ShouldNotBeNull();
		controller.GetUiTaskDispatcher().ShouldBeSameAs(
			services.GetRequiredService<IUiTaskDispatcher>());
		controller.GetEventTasks().ShouldBeSameAs(
			services.GetRequiredService<ObservedTaskGroup>());
		controller.WebPageHostFactory.ShouldBeSameAs(webPageHostFactory);
		factory.WindowServices.ShouldBeSameAs(
			services.GetRequiredService<AvaloniaWindowServices>());
	}

	public void Dispose() => _temporaryDirectory.Dispose();
}
