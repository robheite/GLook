namespace GLook.Models;

using System.Collections.ObjectModel;

public enum MailComposeMode
{
    New,
    Reply,
    ReplyAll,
    Forward
}

public static class MailResourceLimits
{
    public const int MaxThreadMessages = 50;
    public const int MaxMimeDepth = 24;
    public const int MaxMimePartCount = 256;
    public const int MaxDecodedBodyBytes = 8 * 1024 * 1024;
    public const int MaxAggregateThreadBodyBytes = 24 * 1024 * 1024;
    public const int MaxAttachmentCount = 20;
    public const int MaxAttachmentBytes = 15 * 1024 * 1024;
    public const int MaxAggregateAttachmentBytes = 18 * 1024 * 1024;
}

public sealed record MailFolder(
    string Id,
    string Name,
    string FullName,
    string Glyph,
    bool IsSystem,
    bool CanDelete,
    int UnreadCount = 0,
    int ThreadCount = 0)
{
    public string CountText => UnreadCount > 0 ? $"({UnreadCount})" : string.Empty;
}

public sealed class FolderTreeItem
{
    public FolderTreeItem(string name, string fullName, MailFolder? folder = null)
    {
        Name = name;
        FullName = fullName;
        Folder = folder;
    }

    public string Name { get; }

    public string FullName { get; }

    public MailFolder? Folder { get; set; }

    public List<FolderTreeItem> Children { get; } = [];

    public string Glyph => Folder?.Glyph ?? "\uE8B7";

    public string CountText => Folder?.CountText ?? string.Empty;

    public bool IsSelectable => Folder is not null;
}

public sealed record MailThreadSummary(
    string Id,
    string Sender,
    string Subject,
    string Snippet,
    DateTimeOffset ReceivedAt,
    bool IsUnread,
    bool IsStarred,
    IReadOnlyList<string> LabelIds)
{
    public string UnreadMarker => IsUnread ? "●" : string.Empty;

    public string StarMarker => IsStarred ? "★" : string.Empty;

    public string ReceivedText
    {
        get
        {
            var local = ReceivedAt.ToLocalTime();
            var now = DateTimeOffset.Now;
            return local.Date == now.Date
                ? local.ToString("h:mm tt")
                : local.Year == now.Year
                    ? local.ToString("MMM d")
                    : local.ToString("MMM d, yyyy");
        }
    }

    public string Initials
    {
        get
        {
            var words = Sender.Split([' ', '@', '.', '<'], StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
        }
    }
}

public sealed record MailMessage(
    string Id,
    string From,
    string To,
    string Subject,
    DateTimeOffset SentAt,
    string BodyText)
{
    public string? BodyHtml { get; init; }

    public string Cc { get; init; } = string.Empty;

    public string? RfcMessageId { get; init; }

    public string? References { get; init; }

    public bool IsOmissionNotice { get; init; }

    public string SentText => IsOmissionNotice ? string.Empty : SentAt.ToLocalTime().ToString("g");
}

public sealed class MailThreadGroup : ObservableCollection<MailThreadSummary>
{
    public MailThreadGroup(
        string key,
        string title,
        IEnumerable<MailThreadSummary> threads,
        bool isCollapsed = false)
        : base(isCollapsed ? [] : threads)
    {
        Key = key;
        Title = title;
        AllThreads = threads.ToList();
        IsCollapsed = isCollapsed;
    }

    public string Key { get; }

    public string Title { get; }

    public IReadOnlyList<MailThreadSummary> AllThreads { get; }

    public int TotalCount => AllThreads.Count;

    public bool IsCollapsed { get; }

    public string ChevronGlyph => IsCollapsed ? "\uE76C" : "\uE70D";

    public string AutomationName => $"{Title}, {(IsCollapsed ? "collapsed" : "expanded")}";

    public string ToggleHelpText => IsCollapsed ? "Expand this message group" : "Collapse this message group";
}

public sealed record MailSignature(
    string EmailAddress,
    string DisplayName,
    string Html,
    string Text,
    bool IsPrimary,
    bool IsDefault)
{
    public string Id { get; init; } = EmailAddress;

    public bool IsGmailBacked { get; init; } = true;

    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName)
        ? EmailAddress
        : IsGmailBacked
            ? $"{DisplayName} <{EmailAddress}>"
            : DisplayName;

    public string SourceLabel => IsGmailBacked
        ? "Gmail signature"
        : "GLook-only signature";
}

public sealed record SignatureSettingsSnapshot(
    IReadOnlyList<MailSignature> Signatures,
    string? NewSignatureEmail,
    string? ReplySignatureEmail,
    string? ForwardSignatureEmail);

public sealed record MailThreadDetail(
    string Id,
    string Subject,
    IReadOnlyList<MailMessage> Messages);

public sealed record GmailAccountProfile(string EmailAddress, ulong HistoryId);

public sealed record EmptyTrashResult(
    int DeletedCount,
    int SkippedCount,
    int RemainingCount);

public sealed record MailAttachmentInput(
    string FileName,
    byte[] Content,
    string ContentType = "application/octet-stream");

public sealed record ComposeMailRequest
{
    public string? RfcMessageId { get; init; }

    public string To { get; init; } = string.Empty;

    public string Cc { get; init; } = string.Empty;

    public string Bcc { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string BodyText { get; init; } = string.Empty;

    public string? BodyHtml { get; init; }

    public string? ThreadId { get; init; }

    public string? InReplyTo { get; init; }

    public string? References { get; init; }

    public IReadOnlyList<MailAttachmentInput> Attachments { get; init; } = [];
}

public sealed record MailSendResult(
    string MessageId,
    string ThreadId,
    IReadOnlyList<string> LabelIds);

public sealed record MailDraftResult(
    string DraftId,
    string MessageId,
    string? ThreadId);
