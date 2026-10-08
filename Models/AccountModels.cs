namespace GLook.Models;

public enum GmailAccountSyncState
{
    Disconnected,
    Ready,
    Syncing,
    Throttled,
    WaitingForGmail,
    RebuildRequired,
    Error
}

public sealed record GmailAccountRegistration(
    Guid AccountId,
    string EmailAddress,
    string DisplayName,
    int SortOrder,
    string AccentKey,
    bool AutomaticSyncEnabled,
    bool NotificationsEnabled,
    GmailAccountSyncState SyncState = GmailAccountSyncState.Disconnected,
    DateTimeOffset? LastSuccessfulSyncAt = null,
    string? LastSyncError = null)
{
    public string MailboxKey => $"mailbox:{AccountId:D}";

    public string FolderKey(string labelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelId);
        return $"folder:{AccountId:D}:{labelId}";
    }

    public string ThreadKey(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return $"thread:{AccountId:D}:{threadId}";
    }
}
