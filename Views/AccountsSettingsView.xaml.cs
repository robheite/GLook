using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GLook.Models;
using GLook.Services;

namespace GLook.Views;

public sealed partial class AccountsSettingsView : UserControl
{
    private GmailAccountSessionCoordinator? coordinator;
    private bool isRefreshing;

    public AccountsSettingsView()
    {
        InitializeComponent();
    }

    public ObservableCollection<AccountSettingsItemViewModel> Accounts { get; } = [];

    public event EventHandler? AddAccountRequested;

    public event EventHandler<AccountActionRequestedEventArgs>? ReauthorizeAccountRequested;

    public event EventHandler<AccountActionRequestedEventArgs>? RemoveAccountRequested;

    public event EventHandler<AccountRegistrationChangedEventArgs>? AccountRegistrationChanged;

    public async Task InitializeAsync(
        GmailAccountSessionCoordinator accountCoordinator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountCoordinator);
        coordinator = accountCoordinator;
        await coordinator.InitializeAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var activeCoordinator = coordinator
            ?? throw new InvalidOperationException("Initialize the account settings view before refreshing it.");
        if (isRefreshing)
        {
            return;
        }

        isRefreshing = true;
        LoadingIndicator.IsActive = true;
        LoadingIndicator.Visibility = Visibility.Visible;
        AccountList.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        try
        {
            var registrations = await activeCoordinator.GetAccountsAsync(cancellationToken);
            var items = new List<AccountSettingsItemViewModel>(registrations.Count);
            foreach (var registration in registrations)
            {
                var session = await activeCoordinator.GetSessionAsync(registration.AccountId, cancellationToken);
                items.Add(new AccountSettingsItemViewModel(registration, session.Gmail.QuotaSnapshot));
            }

            Accounts.Clear();
            for (var index = 0; index < items.Count; index++)
            {
                items[index].SetPosition(index, items.Count);
                Accounts.Add(items[index]);
            }

            AccountList.Visibility = Accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Visibility = Accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AccountStatusAnnouncement.Text = Accounts.Count switch
            {
                0 => "No Gmail accounts are connected.",
                1 => "1 Gmail account loaded.",
                _ => $"{Accounts.Count} Gmail accounts loaded."
            };
        }
        catch (Exception ex)
        {
            ShowNotice("Account settings could not be loaded", ex.GetBaseException().Message, InfoBarSeverity.Error);
            AccountStatusAnnouncement.Text = "Account settings could not be loaded.";
            throw;
        }
        finally
        {
            LoadingIndicator.IsActive = false;
            LoadingIndicator.Visibility = Visibility.Collapsed;
            isRefreshing = false;
        }
    }

    public async Task UpdateRuntimeStatusAsync(
        Guid accountId,
        GmailQuotaSnapshot quotaSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quotaSnapshot);
        var item = Accounts.FirstOrDefault(candidate => candidate.AccountId == accountId);
        if (item is null)
        {
            return;
        }

        var registration = coordinator is null
            ? item.ToRegistration()
            : await coordinator.Registry.GetAccountAsync(accountId, cancellationToken)
                ?? item.ToRegistration();
        item.ApplyStatus(registration, quotaSnapshot);
    }

    public void AnnounceError(string title, string message)
    {
        ShowNotice(title, message, InfoBarSeverity.Error);
        AccountStatusAnnouncement.Text = $"{title}: {message}";
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        AccountStatusAnnouncement.Text = "Add Gmail account requested.";
        AddAccountRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void DisplayName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid accountId })
        {
            await PersistAccountAsync(accountId);
        }
    }

    private async void AccountPreference_Toggled(object sender, RoutedEventArgs e)
    {
        if (!isRefreshing && sender is FrameworkElement { Tag: Guid accountId })
        {
            await PersistAccountAsync(accountId);
        }
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid accountId })
        {
            await MoveAccountAsync(accountId, -1);
        }
    }

    private async void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid accountId })
        {
            await MoveAccountAsync(accountId, 1);
        }
    }

    private void Reauthorize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid accountId }
            || FindAccount(accountId) is not { } item)
        {
            return;
        }

        AccountStatusAnnouncement.Text = $"Reauthorize requested for {item.DisplayName}.";
        ReauthorizeAccountRequested?.Invoke(
            this,
            new AccountActionRequestedEventArgs(item.ToRegistration()));
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid accountId }
            || FindAccount(accountId) is not { } item)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Remove {item.DisplayName}?",
            Content =
                $"GLook will remove the encrypted authorization and local cache for {item.EmailAddress} from this PC. Gmail messages and labels will not be changed.",
            PrimaryButtonText = "Remove account",
            CloseButtonText = "Keep account",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            AccountStatusAnnouncement.Text = $"Kept {item.DisplayName}.";
            return;
        }

        AccountStatusAnnouncement.Text = $"Remove confirmed for {item.DisplayName}.";
        RemoveAccountRequested?.Invoke(
            this,
            new AccountActionRequestedEventArgs(item.ToRegistration()));
    }

    private async Task PersistAccountAsync(Guid accountId)
    {
        var activeCoordinator = coordinator;
        var item = FindAccount(accountId);
        if (activeCoordinator is null || item is null)
        {
            return;
        }

        await item.SaveGate.WaitAsync();
        try
        {
            item.DisplayName = string.IsNullOrWhiteSpace(item.DisplayName)
                ? item.EmailAddress
                : item.DisplayName.Trim();
            var registration = await activeCoordinator.UpdateAccountPreferencesAsync(
                accountId,
                item.DisplayName,
                item.AutomaticSyncEnabled,
                item.NotificationsEnabled);
            item.AcceptRegistration(registration);
            AccountRegistrationChanged?.Invoke(
                this,
                new AccountRegistrationChangedEventArgs(registration));
            AccountStatusAnnouncement.Text = $"Saved settings for {item.DisplayName}.";
        }
        catch (Exception ex)
        {
            ShowNotice("Account settings were not saved", ex.GetBaseException().Message, InfoBarSeverity.Error);
            AccountStatusAnnouncement.Text = $"Settings for {item.DisplayName} were not saved.";
        }
        finally
        {
            item.SaveGate.Release();
        }
    }

    private async Task MoveAccountAsync(Guid accountId, int offset)
    {
        var activeCoordinator = coordinator;
        var currentIndex = -1;
        for (var index = 0; index < Accounts.Count; index++)
        {
            if (Accounts[index].AccountId == accountId)
            {
                currentIndex = index;
                break;
            }
        }

        var targetIndex = currentIndex + offset;
        if (activeCoordinator is null
            || currentIndex < 0
            || targetIndex < 0
            || targetIndex >= Accounts.Count)
        {
            return;
        }

        var item = Accounts[currentIndex];
        try
        {
            Accounts.Move(currentIndex, targetIndex);
            await activeCoordinator.Registry.SetOrderAsync(
                Accounts.Select(account => account.AccountId).ToArray());
            RefreshPositions();
            AccountStatusAnnouncement.Text = $"Moved {item.DisplayName} to position {targetIndex + 1}.";
        }
        catch (Exception ex)
        {
            Accounts.Move(targetIndex, currentIndex);
            RefreshPositions();
            ShowNotice("Mailbox order was not saved", ex.GetBaseException().Message, InfoBarSeverity.Error);
            AccountStatusAnnouncement.Text = $"Could not move {item.DisplayName}.";
        }
    }

    private AccountSettingsItemViewModel? FindAccount(Guid accountId) =>
        Accounts.FirstOrDefault(account => account.AccountId == accountId);

    private void RefreshPositions()
    {
        for (var index = 0; index < Accounts.Count; index++)
        {
            Accounts[index].SetPosition(index, Accounts.Count);
        }
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        AccountNotice.Title = title;
        AccountNotice.Message = message;
        AccountNotice.Severity = severity;
        AccountNotice.IsOpen = true;
    }
}

public sealed class AccountSettingsItemViewModel : INotifyPropertyChanged
{
    private string displayName;
    private bool automaticSyncEnabled;
    private bool notificationsEnabled;
    private string syncStatusText;
    private string quotaStatusText;
    private string lastSyncText;
    private bool canMoveUp;
    private bool canMoveDown;

    public AccountSettingsItemViewModel(
        GmailAccountRegistration registration,
        GmailQuotaSnapshot quotaSnapshot)
    {
        Registration = registration;
        displayName = registration.DisplayName;
        automaticSyncEnabled = registration.AutomaticSyncEnabled;
        notificationsEnabled = registration.NotificationsEnabled;
        syncStatusText = FormatSyncStatus(registration);
        quotaStatusText = FormatQuotaStatus(quotaSnapshot);
        lastSyncText = FormatLastSync(registration.LastSuccessfulSyncAt);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal SemaphoreSlim SaveGate { get; } = new(1, 1);

    internal GmailAccountRegistration Registration { get; private set; }

    public Guid AccountId => Registration.AccountId;

    public string EmailAddress => Registration.EmailAddress;

    public string DisplayName
    {
        get => displayName;
        set => SetField(ref displayName, value);
    }

    public bool AutomaticSyncEnabled
    {
        get => automaticSyncEnabled;
        set => SetField(ref automaticSyncEnabled, value);
    }

    public bool NotificationsEnabled
    {
        get => notificationsEnabled;
        set => SetField(ref notificationsEnabled, value);
    }

    public string SyncStatusText
    {
        get => syncStatusText;
        private set => SetField(ref syncStatusText, value);
    }

    public string QuotaStatusText
    {
        get => quotaStatusText;
        private set => SetField(ref quotaStatusText, value);
    }

    public string LastSyncText
    {
        get => lastSyncText;
        private set => SetField(ref lastSyncText, value);
    }

    public bool CanMoveUp
    {
        get => canMoveUp;
        private set => SetField(ref canMoveUp, value);
    }

    public bool CanMoveDown
    {
        get => canMoveDown;
        private set => SetField(ref canMoveDown, value);
    }

    public string CardAutomationName => $"Mailbox settings for {DisplayName}, {EmailAddress}";

    public string DisplayNameAutomationName => $"Display label for {EmailAddress}";

    public string EmailAutomationName => $"Gmail address {EmailAddress}";

    public string SyncAutomationName => $"Automatic sync for {DisplayName}";

    public string NotificationsAutomationName => $"Windows notifications for {DisplayName}";

    public string MoveUpAutomationName => $"Move {DisplayName} up";

    public string MoveDownAutomationName => $"Move {DisplayName} down";

    public string ReauthorizeAutomationName => $"Reauthorize {DisplayName}";

    public string RemoveAutomationName => $"Remove {DisplayName}";

    internal GmailAccountRegistration ToRegistration()
    {
        Registration = MergeEditableFields(Registration);
        return Registration;
    }

    internal GmailAccountRegistration MergeEditableFields(GmailAccountRegistration registration) =>
        registration with
        {
            DisplayName = DisplayName,
            AutomaticSyncEnabled = AutomaticSyncEnabled,
            NotificationsEnabled = NotificationsEnabled
        };

    internal void AcceptRegistration(GmailAccountRegistration registration) =>
        Registration = registration;

    internal void ApplyStatus(
        GmailAccountRegistration registration,
        GmailQuotaSnapshot quotaSnapshot)
    {
        Registration = registration;
        SyncStatusText = FormatSyncStatus(registration);
        QuotaStatusText = FormatQuotaStatus(quotaSnapshot);
        LastSyncText = FormatLastSync(registration.LastSuccessfulSyncAt);
    }

    internal void SetPosition(int index, int count)
    {
        CanMoveUp = index > 0;
        CanMoveDown = index >= 0 && index < count - 1;
    }

    private static string FormatSyncStatus(GmailAccountRegistration registration)
    {
        if (!string.IsNullOrWhiteSpace(registration.LastSyncError))
        {
            return $"Sync error: {registration.LastSyncError}";
        }

        return registration.SyncState switch
        {
            GmailAccountSyncState.Disconnected => "Not connected",
            GmailAccountSyncState.Ready => "Ready to sync",
            GmailAccountSyncState.Syncing => "Syncing changes",
            GmailAccountSyncState.Throttled => "Sync paused by the quota guard",
            GmailAccountSyncState.WaitingForGmail => "Waiting for Gmail",
            GmailAccountSyncState.RebuildRequired => "Local mailbox rebuild required",
            GmailAccountSyncState.Error => "Sync needs attention",
            _ => "Sync status unavailable"
        };
    }

    private static string FormatQuotaStatus(GmailQuotaSnapshot quota) =>
        $"Quota: {quota.State} · {quota.RollingMinuteUnits:N0} of {quota.BackgroundMinuteBudget:N0} background units this minute · {quota.EstimatedDailyUnits:N0} estimated today";

    private static string FormatLastSync(DateTimeOffset? lastSuccessfulSyncAt) =>
        lastSuccessfulSyncAt is null
            ? "Last successful sync: Not yet"
            : $"Last successful sync: {lastSuccessfulSyncAt.Value.ToLocalTime():g}";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(DisplayName))
        {
            OnDisplayNameChanged();
        }

        return true;
    }

    private void OnDisplayNameChanged()
    {
        OnPropertyChanged(nameof(CardAutomationName));
        OnPropertyChanged(nameof(SyncAutomationName));
        OnPropertyChanged(nameof(NotificationsAutomationName));
        OnPropertyChanged(nameof(MoveUpAutomationName));
        OnPropertyChanged(nameof(MoveDownAutomationName));
        OnPropertyChanged(nameof(ReauthorizeAutomationName));
        OnPropertyChanged(nameof(RemoveAutomationName));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AccountActionRequestedEventArgs : EventArgs
{
    public AccountActionRequestedEventArgs(GmailAccountRegistration account)
    {
        Account = account;
    }

    public GmailAccountRegistration Account { get; }
}

public sealed class AccountRegistrationChangedEventArgs : EventArgs
{
    public AccountRegistrationChangedEventArgs(GmailAccountRegistration account)
    {
        Account = account;
    }

    public GmailAccountRegistration Account { get; }
}
