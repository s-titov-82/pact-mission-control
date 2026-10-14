using Pact.Core.Sessions;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class DeliveryTraceTests
{
	[Test]
	public void A_trace_line_names_every_stage_without_prompt_text()
	{
		DeliveryTrace trace = new("s1", PromptSender.ScenarioOwner)
		{
			Admission = "ok",
			Paste = "bracketed",
			Shown = "12",
			Enter = "260",
			Confirmed = "900",
			Repair = "none"
		};

		trace.Format(new PromptDeliveryResult(PromptDeliveryOutcome.Confirmed)).ShouldBe(
			"delivery session=s1 sender=ScenarioOwner start=none gate-wait=0 admission=ok "
			+ "paste=bracketed shown=12 enter=260 confirmed=900 repair=none keyboard=no outcome=Confirmed");
	}
}
