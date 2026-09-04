namespace Pact.Presentation.Services;

internal static class SavedItemOrder
{
	public static IReadOnlyList<T> Move<T>(
		IReadOnlyList<T> items,
		Func<T, string> getId,
		string sourceId,
		string targetId,
		bool insertAfter)
	{
		var sourceIndex = FindIndex(items, getId, sourceId);
		var targetIndex = FindIndex(items, getId, targetId);
		if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
		{
			return items;
		}

		var reordered = items.ToList();
		var source = reordered[sourceIndex];
		reordered.RemoveAt(sourceIndex);
		targetIndex = FindIndex(reordered, getId, targetId);
		reordered.Insert(insertAfter ? targetIndex + 1 : targetIndex, source);
		return reordered;
	}

	private static int FindIndex<T>(
		IReadOnlyList<T> items,
		Func<T, string> getId,
		string id)
	{
		for (var index = 0; index < items.Count; index++)
		{
			if (string.Equals(getId(items[index]), id, StringComparison.Ordinal))
			{
				return index;
			}
		}

		return -1;
	}
}
