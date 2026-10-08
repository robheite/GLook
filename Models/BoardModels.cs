namespace GLook.Models;

public enum BoardDragBehavior
{
    Move = 0,
    AddLabel = 1,
}

public enum BoardMailboxScope
{
    Account = 0,
    Unified = 1,
}

public sealed record BoardMailboxSelection(BoardMailboxScope Scope, Guid? AccountId)
{
    public static BoardMailboxSelection ForAccount(Guid accountId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(accountId, Guid.Empty);
        return new BoardMailboxSelection(BoardMailboxScope.Account, accountId);
    }
}

public sealed record BoardColumnDefinition(
    Guid ColumnId,
    string DisplayTitle,
    int Position,
    BoardDragBehavior DragBehavior);

public sealed record BoardDefinition(
    Guid BoardId,
    string Name,
    BoardMailboxSelection? SelectedMailbox,
    IReadOnlyList<BoardColumnDefinition> Columns)
{
    public static BoardDefinition CreateDefault(string name = "Mail workflow")
    {
        var columns = new[]
        {
            new BoardColumnDefinition(Guid.NewGuid(), "Inbox", 0, BoardDragBehavior.Move),
            new BoardColumnDefinition(Guid.NewGuid(), "Needs reply", 1, BoardDragBehavior.Move),
            new BoardColumnDefinition(Guid.NewGuid(), "Waiting", 2, BoardDragBehavior.Move),
            new BoardColumnDefinition(Guid.NewGuid(), "Done", 3, BoardDragBehavior.Move),
        };

        return new BoardDefinition(Guid.NewGuid(), name, null, columns);
    }
}

public sealed record BoardColumnAccountBinding(Guid ColumnId, string GmailLabelId);

public sealed record BoardAccountBinding(
    Guid BoardId,
    Guid AccountId,
    IReadOnlyList<BoardColumnAccountBinding> ColumnBindings);

public sealed record BoardSettingsSnapshot(
    int SchemaVersion,
    IReadOnlyList<BoardDefinition> Boards,
    IReadOnlyList<BoardAccountBinding> AccountBindings)
{
    public const int CurrentSchemaVersion = 1;

    public static BoardSettingsSnapshot Empty { get; } = new(
        CurrentSchemaVersion,
        Array.Empty<BoardDefinition>(),
        Array.Empty<BoardAccountBinding>());
}
