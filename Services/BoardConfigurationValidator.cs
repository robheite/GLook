using GLook.Models;

namespace GLook.Services;

public static class BoardConfigurationValidator
{
    public const int MaximumBoardNameLength = 100;
    public const int MaximumColumnTitleLength = 80;
    public const int MaximumGmailLabelIdLength = 512;
    public const int MaximumColumnsPerBoard = 50;

    public static void Validate(BoardSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.SchemaVersion != BoardSettingsSnapshot.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported board settings schema {snapshot.SchemaVersion}. " +
                $"Expected {BoardSettingsSnapshot.CurrentSchemaVersion}.");
        }

        ArgumentNullException.ThrowIfNull(snapshot.Boards);
        ArgumentNullException.ThrowIfNull(snapshot.AccountBindings);

        var boardIds = new HashSet<Guid>();
        foreach (var board in snapshot.Boards)
        {
            ValidateBoard(board);
            if (!boardIds.Add(board.BoardId))
            {
                throw new InvalidDataException($"Board ID {board.BoardId} is duplicated.");
            }
        }

        var bindingKeys = new HashSet<(Guid BoardId, Guid AccountId)>();
        foreach (var binding in snapshot.AccountBindings)
        {
            if (!bindingKeys.Add((binding.BoardId, binding.AccountId)))
            {
                throw new InvalidDataException(
                    $"Board {binding.BoardId} has more than one binding for account {binding.AccountId}.");
            }

            var board = snapshot.Boards.FirstOrDefault(candidate => candidate.BoardId == binding.BoardId)
                ?? throw new InvalidDataException($"Binding references unknown board {binding.BoardId}.");
            ValidateAccountBinding(board, binding);
        }
    }

    public static void ValidateBoard(BoardDefinition board)
    {
        ArgumentNullException.ThrowIfNull(board);

        if (board.BoardId == Guid.Empty)
        {
            throw new InvalidDataException("A board must have a non-empty ID.");
        }

        ValidateRequiredText(board.Name, MaximumBoardNameLength, "Board name");
        ArgumentNullException.ThrowIfNull(board.Columns);

        if (board.Columns.Count == 0)
        {
            throw new InvalidDataException("A board must contain at least one column.");
        }

        if (board.Columns.Count > MaximumColumnsPerBoard)
        {
            throw new InvalidDataException($"A board cannot contain more than {MaximumColumnsPerBoard} columns.");
        }

        var columnIds = new HashSet<Guid>();
        var positions = new HashSet<int>();
        foreach (var column in board.Columns)
        {
            if (column.ColumnId == Guid.Empty)
            {
                throw new InvalidDataException("A board column must have a non-empty ID.");
            }

            if (!columnIds.Add(column.ColumnId))
            {
                throw new InvalidDataException($"Column ID {column.ColumnId} is duplicated.");
            }

            ValidateRequiredText(column.DisplayTitle, MaximumColumnTitleLength, "Column title");

            if (!Enum.IsDefined(column.DragBehavior))
            {
                throw new InvalidDataException($"Column {column.ColumnId} has an unsupported drag behavior.");
            }

            if (!positions.Add(column.Position))
            {
                throw new InvalidDataException($"Column position {column.Position} is duplicated.");
            }
        }

        if (!positions.SetEquals(Enumerable.Range(0, board.Columns.Count)))
        {
            throw new InvalidDataException("Board column positions must be contiguous and start at zero.");
        }

        if (board.SelectedMailbox is null)
        {
            return;
        }

        if (!Enum.IsDefined(board.SelectedMailbox.Scope))
        {
            throw new InvalidDataException("The selected mailbox scope is unsupported.");
        }

        if (board.SelectedMailbox.Scope == BoardMailboxScope.Unified)
        {
            throw new InvalidDataException("Unified board mailboxes are reserved for a future release.");
        }

        if (board.SelectedMailbox.AccountId is not Guid accountId || accountId == Guid.Empty)
        {
            throw new InvalidDataException("An account mailbox selection must include a non-empty account ID.");
        }
    }

    public static void ValidateAccountBinding(BoardDefinition board, BoardAccountBinding binding)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(binding);

        if (binding.BoardId != board.BoardId)
        {
            throw new InvalidDataException("The account binding belongs to a different board.");
        }

        if (binding.AccountId == Guid.Empty)
        {
            throw new InvalidDataException("An account binding must have a non-empty account ID.");
        }

        ArgumentNullException.ThrowIfNull(binding.ColumnBindings);
        var validColumnIds = board.Columns.Select(column => column.ColumnId).ToHashSet();
        var boundColumns = new HashSet<Guid>();
        var boundLabels = new HashSet<string>(StringComparer.Ordinal);

        foreach (var columnBinding in binding.ColumnBindings)
        {
            if (!validColumnIds.Contains(columnBinding.ColumnId))
            {
                throw new InvalidDataException(
                    $"Binding references unknown column {columnBinding.ColumnId} on board {board.BoardId}.");
            }

            if (!boundColumns.Add(columnBinding.ColumnId))
            {
                throw new InvalidDataException(
                    $"Column {columnBinding.ColumnId} has more than one Gmail label binding for this account.");
            }

            ValidateRequiredText(
                columnBinding.GmailLabelId,
                MaximumGmailLabelIdLength,
                "Gmail label ID");

            if (!boundLabels.Add(columnBinding.GmailLabelId))
            {
                throw new InvalidDataException(
                    $"Gmail label {columnBinding.GmailLabelId} is assigned to more than one board column for this account.");
            }
        }
    }

    public static string NormalizeBoardName(string value) =>
        NormalizeRequiredText(value, MaximumBoardNameLength, "Board name");

    public static string NormalizeColumnTitle(string value) =>
        NormalizeRequiredText(value, MaximumColumnTitleLength, "Column title");

    public static string NormalizeGmailLabelId(string value) =>
        NormalizeRequiredText(value, MaximumGmailLabelIdLength, "Gmail label ID");

    private static string NormalizeRequiredText(string value, int maximumLength, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Trim();
        ValidateRequiredText(normalized, maximumLength, fieldName);
        return normalized;
    }

    private static void ValidateRequiredText(string value, int maximumLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{fieldName} cannot be empty.");
        }

        if (value.Length > maximumLength)
        {
            throw new InvalidDataException($"{fieldName} cannot exceed {maximumLength} characters.");
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{fieldName} cannot start or end with whitespace.");
        }
    }
}
