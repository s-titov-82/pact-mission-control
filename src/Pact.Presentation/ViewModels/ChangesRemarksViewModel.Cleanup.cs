using Pact.Core.Remarks;

namespace Pact.Presentation.ViewModels;

/// <summary>A captured project list offered for explicit removal; later changes invalidate it.</summary>
/// <param name="Threads">All project threads at the time of the offer, across Working tree and commits.</param>
public sealed record RemarkCleanupRequest(IReadOnlyList<RemarkThread> Threads)
{
	/// <summary>The project-wide count displayed beside the completion question.</summary>
	public string Description => $"Все замечания проекта: {Threads.Count}";
}

public sealed partial class ChangesRemarksViewModel
{
	private bool? _cleanupWasComplete;
	private IReadOnlyList<RemarkThread> _cleanupObservedThreads = [];
	private long _cleanupNotificationVersion;
	private long _cleanupAppliedNotification;
	/// <summary>The project's completion question, if not declined or invalidated.</summary>
	public RemarkCleanupRequest? PendingCleanup { get; private set; }
	/// <summary>Whether all project remarks can be explicitly finished and cleared.</summary>
	public bool CanRequestCleanup => !_disposed && !IsClearingReview && CanClearThreads(_book.Threads);
	/// <summary>Whether a deletion is being persisted; relevant editors are disabled until it ends.</summary>
	public bool IsClearingReview { get; private set; }
	/// <summary>Shows the completion question again after No or after loading saved resolved threads.</summary>
	public void RequestCleanup()
	{
		if (!CanRequestCleanup)
		{ return; }
		PendingCleanup = new(_book.Threads.ToArray());
		RaiseCleanup();
	}
	/// <summary>Declines the current question without changing stored threads.</summary>
	public void DismissCleanup()
	{
		PendingCleanup = null;
		RaiseCleanup();
	}
	/// <summary>Removes the offered project list only if unchanged, retaining all data on a failed save.</summary>
	public async Task ConfirmCleanupAsync(RemarkCleanupRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (_disposed || IsClearingReview || !ReferenceEquals(request, PendingCleanup))
		{ return; }
		if (!CanClearThreads(request.Threads))
		{
			PendingCleanup = null;
			SetStatus("Remarks changed. Review the list before clearing it.");
			RaiseCleanup();
			return;
		}
		var views = request.Threads.Select(thread => _threads.GetValueOrDefault(thread.Id)).OfType<RemarkThreadViewModel>().ToArray();
		IsClearingReview = true;
		foreach (var view in views)
		{ view.SetEditingEnabled(false); }
		RaiseCleanup();
		try
		{
			var cleared = await _book.TryClearReviewAsync(request.Threads, cancellationToken).ConfigureAwait(true);
			PendingCleanup = null;
			SetStatus(cleared ? null : "Remarks changed. Review the list before clearing it.");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			SetStatus($"Could not clear remarks: {exception.Message}. Try again.");
		}
		finally
		{
			foreach (var view in views)
			{ view.SetEditingEnabled(true); }
			IsClearingReview = false;
			RaiseCleanup();
		}
	}
	private bool CanClearThreads(IReadOnlyList<RemarkThread> threads) => threads.Count > 0
		&& threads.All(thread => thread.IsResolved && thread.Messages.All(message => message.Delivery == RemarkDelivery.Sent)
			&& !(_threads.TryGetValue(thread.Id, out var view) && view.HasUnsavedDraft));

	// Completion follows one ordered stream for the whole project. Selecting another source
	// only changes line placement; it neither starts nor ends a separate review.
	private void ObserveCleanupCompletion(IReadOnlyList<RemarkThread> threads)
	{
		if (_disposed)
		{ return; }
		_cleanupObservedThreads = threads;
		var complete = CanClearThreads(threads);
		if (PendingCleanup is { } offered && (!complete || !threads.SequenceEqual(offered.Threads)))
		{ PendingCleanup = null; }
		if (_cleanupWasComplete == false && complete)
		{ PendingCleanup = new(threads.ToArray()); }
		_cleanupWasComplete = threads.Count == 0 ? null : complete;
		RaiseCleanup();
	}
	private void RaiseCleanup() => Raise(nameof(PendingCleanup), nameof(CanRequestCleanup), nameof(IsClearingReview));
}