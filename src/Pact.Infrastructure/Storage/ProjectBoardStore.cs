using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pact.Core.Boards;
using Pact.Core.Projects;

namespace Pact.Infrastructure.Storage;

/// <summary>Atomic project board storage below Settings.</summary>
public sealed class ProjectBoardStore(AppPaths paths, TimeProvider timeProvider) : IProjectBoardStore
{
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	/// <inheritdoc />
	public async Task<BoardSnapshot> LoadAsync(string projectRootPath, CancellationToken cancellationToken)
	{
		var path = BoardPath(projectRootPath);
		var gate = Gates.GetOrAdd(path, static _ => new(1, 1));
		await gate.WaitAsync(cancellationToken);
		try
		{
			return await ReadAsync(path, cancellationToken);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <inheritdoc />
	public async Task<BoardMutationResult> ExecuteAsync(string projectRootPath, BoardCommand command, BoardActor actor, CancellationToken cancellationToken)
	{
		var path = BoardPath(projectRootPath);
		var gate = Gates.GetOrAdd(path, static _ => new(1, 1));
		await gate.WaitAsync(cancellationToken);
		try
		{
			var current = await ReadAsync(path, cancellationToken);
			var transition = BoardRules.Apply(current.State, command, actor, timeProvider.GetUtcNow());
			if (transition.Outcome != BoardMutationOutcome.Applied)
			{
				return new(transition.Outcome, current, transition.CardId, transition.CommentId);
			}

			var content = JsonSerializer.Serialize(transition.State, JsonOptions);
			await AtomicFileWriter.WriteTextAsync(path, content, paths.AtomicTempDirectory, cancellationToken);
			// Once committed, cancellation must not convert a saved change into a canceled result.
			var saved = await ReadAsync(path, CancellationToken.None);
			return new(transition.Outcome, saved, transition.CardId, transition.CommentId);
		}
		finally
		{
			gate.Release();
		}
	}

	private string BoardPath(string root) => Path.Combine(paths.BoardsDirectory, ProjectNoteFileKey.FromRootPath(root) + ".json");

	private static async Task<BoardSnapshot> ReadAsync(string path, CancellationToken cancellationToken)
	{
		byte[] bytes;
		try
		{
			bytes = await File.ReadAllBytesAsync(path, cancellationToken);
		}
		catch (FileNotFoundException)
		{
			return Empty();
		}
		catch (DirectoryNotFoundException)
		{
			return Empty();
		}

		try
		{
			var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
			using var json = JsonDocument.Parse(text);
			if (json.RootElement.ValueKind != JsonValueKind.Object
				|| !json.RootElement.TryGetProperty(nameof(BoardState.SchemaVersion), out var schema)
				|| !schema.TryGetInt32(out var version) || version != 1)
			{
				throw new InvalidDataException("Unsupported board schema. The original file has not been changed.");
			}

			var state = JsonSerializer.Deserialize<BoardState>(text, JsonOptions);
			if (state is null || !IsValid(state))
			{
				throw new InvalidDataException("Invalid board data. The original file has not been changed.");
			}

			state = state with
			{
				Cards = state.Cards.Select(c => c with { Comments = c.Comments.ToList().AsReadOnly() }).ToList().AsReadOnly()
			};
			return new(state, Convert.ToHexStringLower(SHA256.HashData(bytes)), BoardRevisions.ForOrder(state));
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException("Cannot read board data. The original file has not been changed.", exception);
		}
	}

	private static BoardSnapshot Empty() => new(BoardState.Empty, string.Empty, BoardRevisions.ForOrder(BoardState.Empty));

	private static bool IsValid(BoardState state)
	{
		if (state.Cards is null || state.NextCardNumber < 1 || state.OrderVersion < 0
			|| state.Cards.Any(c => c is null)
			|| state.Cards.Count(c => !c.IsArchived) > BoardLimits.ActiveCards
			|| state.Cards.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != state.Cards.Count
			|| state.Cards.Select(c => c.Number).Distinct().Count() != state.Cards.Count)
		{
			return false;
		}

		return state.Cards.All(c => !string.IsNullOrWhiteSpace(c.Id) && c.Id.Length <= 128
			&& c.Number > 0 && c.Number < state.NextCardNumber && c.FieldsVersion >= 0
			&& BoardRules.ValidFields(c) && (!c.IsArchived || c.Status == BoardCardStatus.Done)
			&& c.Comments is not null && c.NextCommentNumber > 0
			&& c.Comments.All(m => m is not null && !string.IsNullOrWhiteSpace(m.Id)
				&& m.Id.Length <= 128 && m.Number > 0 && m.Number < c.NextCommentNumber
				&& m.AuthorName is not null && !string.IsNullOrWhiteSpace(m.Text) && m.Text.Length <= BoardLimits.Comment)
			&& c.Comments.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() == c.Comments.Count
			&& c.Comments.Select(m => m.Number).Distinct().Count() == c.Comments.Count);
	}
}
