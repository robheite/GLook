using GLook.Models;

namespace GLook.Services;

public static class BoardConfigurationEditor
{
    public static (BoardSettingsSnapshot Snapshot, BoardDefinition Board) AddDefaultBoard(
        BoardSettingsSnapshot snapshot,
        string name = "Mail workflow")
    {
        BoardConfigurationValidator.Validate(snapshot);
        var board = BoardDefinition.CreateDefault(BoardConfigurationValidator.NormalizeBoardName(name));
        var updated = snapshot with { Boards = [.. snapshot.Boards, board] };
        BoardConfigurationValidator.Validate(updated);
        return (updated, board);
    }

    public static BoardSettingsSnapshot RemoveBoard(BoardSettingsSnapshot snapshot, Guid boardId)
    {
        var board = GetBoard(snapshot, boardId);
        var updated = snapshot with
        {
            Boards = snapshot.Boards.Where(candidate => candidate.BoardId != board.BoardId).ToArray(),
            AccountBindings = snapshot.AccountBindings
                .Where(binding => binding.BoardId != board.BoardId)
                .ToArray(),
        };
        BoardConfigurationValidator.Validate(updated);
        return updated;
    }

    public static BoardSettingsSnapshot RenameBoard(
        BoardSettingsSnapshot snapshot,
        Guid boardId,
        string displayName) =>
        ReplaceBoard(snapshot, GetBoard(snapshot, boardId) with
        {
            Name = BoardConfigurationValidator.NormalizeBoardName(displayName),
        });

    public static BoardSettingsSnapshot RememberSelectedMailbox(
        BoardSettingsSnapshot snapshot,
        Guid boardId,
        Guid accountId) =>
        ReplaceBoard(snapshot, GetBoard(snapshot, boardId) with
        {
            SelectedMailbox = BoardMailboxSelection.ForAccount(accountId),
        });

    public static BoardSettingsSnapshot ClearSelectedMailbox(
        BoardSettingsSnapshot snapshot,
        Guid boardId) =>
        ReplaceBoard(snapshot, GetBoard(snapshot, boardId) with
        {
            SelectedMailbox = null,
        });

    public static BoardSettingsSnapshot RemoveAccount(
        BoardSettingsSnapshot snapshot,
        Guid accountId)
    {
        BoardConfigurationValidator.Validate(snapshot);
        ArgumentOutOfRangeException.ThrowIfEqual(accountId, Guid.Empty);
        var updated = snapshot with
        {
            Boards = snapshot.Boards
                .Select(board => board.SelectedMailbox?.AccountId == accountId
                    ? board with { SelectedMailbox = null }
                    : board)
                .ToArray(),
            AccountBindings = snapshot.AccountBindings
                .Where(binding => binding.AccountId != accountId)
                .ToArray(),
        };
        BoardConfigurationValidator.Validate(updated);
        return updated;
    }

    public static BoardSettingsSnapshot AddColumn(
        BoardSettingsSnapshot snapshot,
        Guid boardId,
        string displayTitle,
        BoardDragBehavior dragBehavior = BoardDragBehavior.Move)
    {
        if (!Enum.IsDefined(dragBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(dragBehavior));
        }

        var board = GetBoard(snapshot, boardId);
        if (board.Columns.Count >= BoardConfigurationValidator.MaximumColumnsPerBoard)
        {
            throw new InvalidOperationException(
                $"A board cannot contain more than {BoardConfigurationValidator.MaximumColumnsPerBoard} columns.");
        }

        var column = new BoardColumnDefinition(
            Guid.NewGuid(),
            BoardConfigurationValidator.NormalizeColumnTitle(displayTitle),
            board.Columns.Count,
            dragBehavior);
        return ReplaceBoard(snapshot, board with { Columns = [.. OrderedColumns(board), column] });
    }

    public static BoardSettingsSnapshot RemoveColumn(
        BoardSettingsSnapshot snapshot,
        Guid boardId,
        Guid columnId)
    {
        var board = GetBoard(snapshot, boardId);
        _ = GetColumn(board, columnId);
        if (board.Columns.Count == 1)
        {
            throw new InvalidOperationException("A board must keep at least one column.");
        }

        var retainedColumns = OrderedColumns(board)
            .Where(column => column.ColumnId != columnId)
            .Select((column, position) => column with { Position = position })
            .ToArray();

        var updated = snapshot with
        {
            Boards = snapshot.Boards
                .Select(candidate => candidate.BoardId == boardId
                    ? board with { Columns = retainedColumns }
                    : candidate)
                .ToArray(),
            AccountBindings = snapshot.AccountBindings
                .Select(binding => binding.BoardId == boardId
                    ? binding with
                    {
                        ColumnBindings = binding.ColumnBindings
                            .Where(columnBinding => columnBinding.ColumnId != columnId)
                            .ToArray(),
                    }
                    : binding)
                .Where(binding => binding.BoardId != boardId || binding.ColumnBindings.Count > 0)
                .ToArray(),
        };
        BoardConfigurationValidator.Validate(updated);
        return updated;
    }

    public static BoardSettingsSnapshot RenameColumn(
        BoardSettingsSnapshot snapshot,
        Guid boardId,
        Guid columnId,
        string displayTitle)
    {
        var board = GetBoard(snapshot, boardId);
        _ = GetColumn(board, columnId);
        var columns = board.Columns
            .Select(column => column.ColumnId == columnId
                ? column with { DisplayTitle = BoardConfigurationValidator.NormalizeColumnTitle(displayTitle) }
                : column)
            .ToArray();
        return ReplaceBoard(snapshot, board with { Columns = columns });
    }

    public static BoardSettingsSnapshot SetColumnDragBehavior(
        BoardSettingsSnapshot snapshot,
        Guid boardId,
        Guid columnId,
        BoardDragBehavior dragBehavior)
    {
        if (!Enum.IsDefined(dragBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(dragBehavior));
        }

        var board = GetBoard(snapshot, boardId);
        _ = GetColumn(board, columnId);
        var columns = board.Columns
            .Select(column => column.ColumnId == columnId
                ? column with { DragBehavior = dragBehavior }
                : column)
            .ToArray();
        return ReplaceBoard(snapshot, board with { Columns = columns });
    }

    public static BoardSettingsSnapshot MoveColumn(
        BoardSettingsSnapshot snapshot,
        Guid boardId,
        Guid columnId,
        int targetPosition)
    {
        var board = GetBoard(snapshot, boardId);
        var column = GetColumn(board, columnId);
        if (targetPosition < 0 || targetPosition >= board.Columns.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPosition));
        }

        var reordered = OrderedColumns(board).Where(candidate => candidate.ColumnId != columnId).ToList();
        reordered.Insert(targetPosition, column);
        var normalized = reordered
            .Select((candidate, position) => candidate with { Position = position })
            .ToArray();
        return ReplaceBoard(snapshot, board with { Columns = normalized });
    }

    public static BoardSettingsSnapshot BindColumnToGmailLabel(
        BoardSettingsSnapshot snapshot,
        Guid boardId,
        Guid accountId,
        Guid columnId,
        string gmailLabelId)
    {
        var board = GetBoard(snapshot, boardId);
        _ = GetColumn(board, columnId);
        ArgumentOutOfRangeException.ThrowIfEqual(accountId, Guid.Empty);
        var normalizedLabelId = BoardConfigurationValidator.NormalizeGmailLabelId(gmailLabelId);

        var accountBinding = snapshot.AccountBindings.FirstOrDefault(binding =>
            binding.BoardId == boardId && binding.AccountId == accountId);
        var bindings = (accountBinding?.ColumnBindings ?? Array.Empty<BoardColumnAccountBinding>())
            .Where(binding => binding.ColumnId != columnId)
            .Append(new BoardColumnAccountBinding(columnId, normalizedLabelId))
            .ToArray();
        var replacement = new BoardAccountBinding(boardId, accountId, bindings);

        var updatedBindings = snapshot.AccountBindings
            .Where(binding => binding.BoardId != boardId || binding.AccountId != accountId)
            .Append(replacement)
            .ToArray();
        var updated = snapshot with { AccountBindings = updatedBindings };
        BoardConfigurationValidator.Validate(updated);
        return updated;
    }

    public static BoardSettingsSnapshot UnbindColumn(
        BoardSettingsSnapshot snapshot,
        Guid boardId,
        Guid accountId,
        Guid columnId)
    {
        var board = GetBoard(snapshot, boardId);
        _ = GetColumn(board, columnId);
        ArgumentOutOfRangeException.ThrowIfEqual(accountId, Guid.Empty);

        var updatedBindings = snapshot.AccountBindings
            .Select(binding => binding.BoardId == boardId && binding.AccountId == accountId
                ? binding with
                {
                    ColumnBindings = binding.ColumnBindings
                        .Where(columnBinding => columnBinding.ColumnId != columnId)
                        .ToArray(),
                }
                : binding)
            .Where(binding => binding.ColumnBindings.Count > 0)
            .ToArray();
        var updated = snapshot with { AccountBindings = updatedBindings };
        BoardConfigurationValidator.Validate(updated);
        return updated;
    }

    private static BoardSettingsSnapshot ReplaceBoard(
        BoardSettingsSnapshot snapshot,
        BoardDefinition replacement)
    {
        _ = GetBoard(snapshot, replacement.BoardId);
        var updated = snapshot with
        {
            Boards = snapshot.Boards
                .Select(board => board.BoardId == replacement.BoardId ? replacement : board)
                .ToArray(),
        };
        BoardConfigurationValidator.Validate(updated);
        return updated;
    }

    private static BoardDefinition GetBoard(BoardSettingsSnapshot snapshot, Guid boardId)
    {
        BoardConfigurationValidator.Validate(snapshot);
        ArgumentOutOfRangeException.ThrowIfEqual(boardId, Guid.Empty);
        return snapshot.Boards.FirstOrDefault(board => board.BoardId == boardId)
            ?? throw new KeyNotFoundException($"Board {boardId} was not found.");
    }

    private static BoardColumnDefinition GetColumn(BoardDefinition board, Guid columnId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(columnId, Guid.Empty);
        return board.Columns.FirstOrDefault(column => column.ColumnId == columnId)
            ?? throw new KeyNotFoundException($"Column {columnId} was not found on board {board.BoardId}.");
    }

    private static IEnumerable<BoardColumnDefinition> OrderedColumns(BoardDefinition board) =>
        board.Columns.OrderBy(column => column.Position);
}
