using GLook.Models;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace GLook.Services;

public sealed record NotificationDeliveryResult(
    bool Succeeded,
    bool AppearsInNotificationCenter,
    AppNotificationSetting Setting,
    string Message);

public sealed class WindowsNotificationService : IDisposable
{
    private readonly AppNotificationManager manager = AppNotificationManager.Default;
    private bool isRegistered;

    public bool IsRegistered => isRegistered;

    public string? RegistrationError { get; private set; }

    public AppNotificationSetting Setting => manager.Setting;

    public string StatusText => GetSettingMessage(Setting);

    public bool Register()
    {
        if (isRegistered)
        {
            return true;
        }

        try
        {
            manager.NotificationInvoked += NotificationInvoked;
            manager.Register();
            isRegistered = true;
            RegistrationError = null;
            return true;
        }
        catch (Exception ex)
        {
            manager.NotificationInvoked -= NotificationInvoked;
            RegistrationError = ex.GetBaseException().Message;
            return false;
        }
    }

    public void ShowNewMail(MailThreadSummary thread)
    {
        if (!isRegistered || Setting != AppNotificationSetting.Enabled)
        {
            return;
        }

        var notification = new AppNotificationBuilder()
            .AddArgument("action", "viewConversation")
            .AddArgument("threadId", thread.Id)
            .AddText("New mail in GLook")
            .AddText(thread.Sender)
            .AddText(thread.Subject)
            .BuildNotification();

        manager.Show(notification);
    }

    public async Task<NotificationDeliveryResult> ShowTestAsync(
        CancellationToken cancellationToken = default)
    {
        if (!isRegistered && !Register())
        {
            return new NotificationDeliveryResult(
                false,
                false,
                AppNotificationSetting.Unsupported,
                RegistrationError ?? "Windows rejected GLook notification registration.");
        }

        var setting = Setting;
        if (setting != AppNotificationSetting.Enabled)
        {
            return new NotificationDeliveryResult(false, false, setting, GetSettingMessage(setting));
        }

        const string tag = "notification-test";
        const string group = "glook-diagnostics";
        var notification = new AppNotificationBuilder()
            .AddArgument("action", "notificationTest")
            .AddText("GLook notifications are working")
            .AddText("Windows accepted this test notification from GLook.")
            .BuildNotification();
        notification.Tag = tag;
        notification.Group = group;
        notification.Priority = AppNotificationPriority.High;

        manager.Show(notification);
        await Task.Delay(500, cancellationToken);

        var postedNotifications = await manager.GetAllAsync();
        var appearsInNotificationCenter = postedNotifications.Any(item =>
            string.Equals(item.Tag, tag, StringComparison.Ordinal)
            && string.Equals(item.Group, group, StringComparison.Ordinal));
        var message = appearsInNotificationCenter
            ? "Windows accepted the test and it appears in Notification Center. If no banner appeared, check Do Not Disturb or Focus settings."
            : "Windows accepted the request, but the test was not retained in Notification Center. Check Windows notification and Do Not Disturb settings.";

        return new NotificationDeliveryResult(
            appearsInNotificationCenter,
            appearsInNotificationCenter,
            setting,
            message);
    }

    public void Dispose()
    {
        if (!isRegistered)
        {
            return;
        }

        manager.NotificationInvoked -= NotificationInvoked;
        manager.Unregister();
        isRegistered = false;
    }

    private static void NotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        if (Application.Current is not App app || app.MainWindow is null)
        {
            return;
        }

        app.MainWindow.DispatcherQueue.TryEnqueue(app.MainWindow.Activate);
    }

    private static string GetSettingMessage(AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.Enabled => "Windows notifications are enabled for GLook.",
        AppNotificationSetting.DisabledForApplication =>
            "Notifications are disabled for GLook in Windows Settings.",
        AppNotificationSetting.DisabledForUser =>
            "Notifications are disabled globally for this Windows user.",
        AppNotificationSetting.DisabledByGroupPolicy =>
            "Notifications are blocked by Windows Group Policy.",
        AppNotificationSetting.DisabledByManifest =>
            "Notifications are blocked by the application manifest.",
        _ => "Windows app notifications are not supported on this configuration."
    };
}
