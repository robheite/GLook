using GLook.Models;

namespace GLook.Services;

internal static class AccountNotificationPolicy
{
    public static bool ShouldPublish(
        Guid? requestedAccountId,
        GmailAccountRegistration? account)
    {
        // A null ID is the legacy single-account path. Once an account ID is
        // supplied, fail closed unless its persisted registration explicitly
        // allows notifications.
        return requestedAccountId is null
            || account is { NotificationsEnabled: true };
    }
}
