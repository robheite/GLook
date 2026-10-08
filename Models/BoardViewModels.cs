namespace GLook.Models;

public sealed class BoardMailboxOption
{
    public BoardMailboxOption(Guid accountId, string displayName, string emailAddress)
    {
        AccountId = accountId;
        DisplayName = displayName;
        EmailAddress = emailAddress;
    }

    public Guid AccountId { get; set; }

    public string DisplayName { get; set; }

    public string EmailAddress { get; set; }

    public string DisplayText => string.Equals(DisplayName, EmailAddress, StringComparison.OrdinalIgnoreCase)
        ? EmailAddress
        : $"{DisplayName} ({EmailAddress})";
}

public sealed class BoardCardItem
{
    public BoardCardItem(
        Guid accountId,
        string threadId,
        Guid columnId,
        string mailboxDisplayName,
        string sender,
        string subject,
        string preview,
        string receivedText,
        bool isUnread)
    {
        AccountId = accountId;
        ThreadId = threadId;
        ColumnId = columnId;
        MailboxDisplayName = mailboxDisplayName;
        Sender = sender;
        Subject = subject;
        Preview = preview;
        ReceivedText = receivedText;
        IsUnread = isUnread;
    }

    public Guid AccountId { get; set; }

    public string ThreadId { get; set; }

    public Guid ColumnId { get; set; }

    public string MailboxDisplayName { get; set; }

    public string Sender { get; set; }

    public string Subject { get; set; }

    public string Preview { get; set; }

    public string ReceivedText { get; set; }

    public bool IsUnread { get; set; }

    public string UnreadMarker => IsUnread ? "Unread" : "Read";

    public string AutomationName =>
        $"{UnreadMarker} message from {Sender}, {Subject}, in {MailboxDisplayName}";
}

public sealed class BoardLaneViewModel
{
    public BoardLaneViewModel(
        BoardColumnDefinition definition,
        string? gmailLabelId,
        IReadOnlyList<BoardCardItem> cards)
    {
        Definition = definition;
        GmailLabelId = gmailLabelId;
        Cards = cards;
    }

    public BoardColumnDefinition Definition { get; set; }

    public string? GmailLabelId { get; set; }

    public IReadOnlyList<BoardCardItem> Cards { get; set; }

    public bool IsMapped => !string.IsNullOrWhiteSpace(GmailLabelId);

    public bool IsUnmapped => !IsMapped;

    public string BindingText => IsMapped ? $"Gmail label: {GmailLabelId}" : "Gmail label not mapped";

    public string CountText => Cards.Count == 1 ? "1 conversation" : $"{Cards.Count} conversations";

    public string AutomationName => $"{Definition.DisplayTitle}, {CountText}, {BindingText}";
}

public sealed class BoardMailboxChangedEventArgs(Guid boardId, Guid accountId) : EventArgs
{
    public Guid BoardId { get; } = boardId;

    public Guid AccountId { get; } = accountId;
}

public sealed class BoardColumnRenamedEventArgs(Guid boardId, Guid columnId, string displayTitle) : EventArgs
{
    public Guid BoardId { get; } = boardId;

    public Guid ColumnId { get; } = columnId;

    public string DisplayTitle { get; } = displayTitle;
}

public sealed class BoardColumnBindingRequestedEventArgs(Guid boardId, Guid accountId, Guid columnId) : EventArgs
{
    public Guid BoardId { get; } = boardId;

    public Guid AccountId { get; } = accountId;

    public Guid ColumnId { get; } = columnId;
}

public sealed class BoardCardRequestedEventArgs(BoardCardItem card) : EventArgs
{
    public BoardCardItem Card { get; } = card;
}

public sealed class BoardCardActionRequestedEventArgs(
    BoardCardItem card,
    Guid sourceColumnId,
    string sourceGmailLabelId,
    Guid targetColumnId,
    string targetGmailLabelId,
    BoardDragBehavior behavior) : EventArgs
{
    public BoardCardItem Card { get; } = card;

    public Guid SourceColumnId { get; } = sourceColumnId;

    public string SourceGmailLabelId { get; } = sourceGmailLabelId;

    public Guid TargetColumnId { get; } = targetColumnId;

    public string TargetGmailLabelId { get; } = targetGmailLabelId;

    public BoardDragBehavior Behavior { get; } = behavior;
}
