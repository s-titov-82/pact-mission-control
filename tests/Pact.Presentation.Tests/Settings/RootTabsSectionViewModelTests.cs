using Pact.Core.Agents;
using Pact.Core.RootTabs;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Presentation.Settings;
using Pact.Presentation.Settings.ViewModels;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public sealed class RootTabsSectionViewModelTests
{
	[Test]
	public async Task Load_and_save_edit_root_terminal_and_web_page()
	{
		var now = DateTimeOffset.UtcNow;
		var session = new SessionRecord(
			"root-session",
			AgentKind.Hermes,
			"Hermes",
			Path.GetTempPath(),
			"hermes",
			null,
			SessionStatus.Stopped,
			now,
			now);
		var webPage = new WebPageRecord(
			"root-web",
			"Jira",
			"https://jira.example.test",
			"https://jira.example.test",
			now,
			now);
		RootTabsViewModel rootTabs = new(new RootTabsRecord(
			1,
			session.Id,
			[session],
			[webPage],
			[]));
		RecordingRootTabsEditor editor = new();
		RootTabsSectionViewModel section = new(
			() => rootTabs,
			editor,
			@"C:\settings\root-tabs.json");

		await section.LoadAsync(CancellationToken.None);
		var sessionItem = section.Items.OfType<SessionSettingsItemViewModel>().Single();
		var webItem = section.Items.OfType<RootWebPageSettingsItemViewModel>().Single();
		sessionItem.ShowWorkingDirectorySetting.ShouldBeTrue();
		sessionItem.Title = "General Hermes";
		webItem.Url = "https://jira.example.test/dashboard";

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		editor.SessionId.ShouldBe(session.Id);
		editor.SessionEdit?.Title.ShouldBe("General Hermes");
		editor.WebPageId.ShouldBe(webPage.Id);
		editor.WebPageEdit?.Url.ShouldBe("https://jira.example.test/dashboard");
		section.IsDirty.ShouldBeFalse();
	}

	[Test]
	public async Task SelectItem_deep_links_directly_to_root_item()
	{
		var now = DateTimeOffset.UtcNow;
		var webPage = new WebPageRecord(
			"root-web",
			"Jira",
			"https://jira.example.test",
			"https://jira.example.test",
			now,
			now);
		RootTabsViewModel rootTabs = new(new RootTabsRecord(1, null, [], [webPage], []));
		RootTabsSectionViewModel section = new(
			() => rootTabs,
			new RecordingRootTabsEditor(),
			@"C:\settings\root-tabs.json");
		await section.LoadAsync(CancellationToken.None);

		section.SelectItem(webPage.Id, null);

		section.SelectedItem.ShouldBeOfType<RootWebPageSettingsItemViewModel>()
			.Id.ShouldBe(webPage.Id);
	}

	private sealed class RecordingRootTabsEditor : IRootTabsSettingsEditor
	{
		public string? SessionId { get; private set; }
		public SessionSettingsEdit? SessionEdit { get; private set; }
		public string? WebPageId { get; private set; }
		public RootWebPageSettingsEdit? WebPageEdit { get; private set; }

		public Task UpdateRootSessionSettingsAsync(
			string sessionId,
			SessionSettingsEdit edit,
			CancellationToken cancellationToken)
		{
			SessionId = sessionId;
			SessionEdit = edit;
			return Task.CompletedTask;
		}

		public Task UpdateRootWebPageSettingsAsync(
			string webPageId,
			RootWebPageSettingsEdit edit,
			CancellationToken cancellationToken)
		{
			WebPageId = webPageId;
			WebPageEdit = edit;
			return Task.CompletedTask;
		}
	}
}
