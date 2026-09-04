using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class TaskbarAttentionPolicyTests
{
	[Test]
	public void ShouldSetCompletionAttention_returns_true_for_completed_current_session_when_window_is_inactive() => TaskbarAttentionPolicy.ShouldSetCompletionAttention(
			markUnreadCompletion: true,
			wasBusyLongEnough: true,
			isWindowActive: false).ShouldBeTrue();

	[Test]
	[TestCase(false, true, false)]
	[TestCase(true, false, false)]
	[TestCase(true, true, true)]
	public void ShouldSetCompletionAttention_ignores_suppressed_short_or_visible_completion(
		bool markUnreadCompletion,
		bool wasBusyLongEnough,
		bool isWindowActive) => TaskbarAttentionPolicy.ShouldSetCompletionAttention(
			markUnreadCompletion,
			wasBusyLongEnough,
			isWindowActive).ShouldBeFalse();

	[Test]
	[TestCase(true, false, false, true)]
	[TestCase(false, true, false, true)]
	[TestCase(true, true, true, false)]
	[TestCase(false, false, false, false)]
	public void ShouldFlashTaskbar_requires_inactive_window_and_attention(
		bool hasUnreadCompletions,
		bool hasCompletionAttention,
		bool isWindowActive,
		bool expected) => TaskbarAttentionPolicy.ShouldFlashTaskbar(
			hasUnreadCompletions,
			hasCompletionAttention,
			isWindowActive).ShouldBe(expected);
}