using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GLook.Models;
using GLook.Services;

namespace GLook.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int MaxCachedThreadDetails = 12;
    private readonly GmailClientService gmail;
    private readonly LocalMailStore localStore;
    private readonly SignatureSettingsStore? signatureStore;
    private readonly CancellationToken lifetimeToken;
    private readonly SemaphoreSlim? operationGate;
    private readonly bool hasPreBoundAccountScope;
    private readonly Dictionary<string, MailThreadDetail> threadDetailCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> threadDetailLru = new();
    private readonly Dictionary<string, LinkedListNode<string>> threadDetailLruNodes = new(StringComparer.Ordinal);
    private CancellationTokenSource? refreshCancellation;
    private CancellationTokenSource? selectionCancellation;
    private long selectionGeneration;
    private DateTimeOffset folderListRefreshedAt = DateTimeOffset.MinValue;
    private readonly HashSet<string> hydratedFolderIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> partialFolderWindowStarts = new(StringComparer.Ordinal);
    private SynchronizationContext? quotaSynchronizationContext;
    private bool quotaTracking;
    private GmailQuotaSnapshot quotaSnapshot;
    private string? composeInReplyTo;
    private string? composeReferences;
    private string? composeRfcMessageId;

    public MainViewModel(
        GmailClientService gmail,
        LocalMailStore localStore,
        SignatureSettingsStore? signatureStore = null,
        CancellationToken lifetimeToken = default,
        SemaphoreSlim? operationGate = null)
    {
        this.gmail = gmail;
        this.localStore = localStore;
        this.signatureStore = signatureStore;
        this.lifetimeToken = lifetimeToken;
        this.operationGate = operationGate;
        hasPreBoundAccountScope = localStore.HasAccountScope;
        quotaSnapshot = gmail.QuotaSnapshot;
    }

    public void StartQuotaTracking()
    {
        if (quotaTracking)
        {
            return;
        }

        quotaSynchronizationContext = SynchronizationContext.Current;
        gmail.QuotaSnapshotChanged += Gmail_QuotaSnapshotChanged;
        quotaTracking = true;
        NotifyQuotaChanged();
    }

    public void StopQuotaTracking()
    {
        if (!quotaTracking)
        {
            return;
        }

        gmail.QuotaSnapshotChanged -= Gmail_QuotaSnapshotChanged;
        quotaTracking = false;
        quotaSynchronizationContext = null;
    }

    public void RefreshQuotaStatus() => NotifyQuotaChanged(gmail.QuotaSnapshot);

    private void Gmail_QuotaSnapshotChanged(GmailQuotaSnapshot snapshot)
    {
        if (quotaSynchronizationContext is { } context
            && SynchronizationContext.Current != context)
        {
            context.Post(_ => NotifyQuotaChanged(snapshot), null);
            return;
        }

        NotifyQuotaChanged(snapshot);
    }

    private void NotifyQuotaChanged() => NotifyQuotaChanged(gmail.QuotaSnapshot);

    private void NotifyQuotaChanged(GmailQuotaSnapshot snapshot)
    {
        if (quotaSnapshot.State == snapshot.State
            && quotaSnapshot.RollingMinuteUnits == snapshot.RollingMinuteUnits
            && quotaSnapshot.EstimatedDailyUnits == snapshot.EstimatedDailyUnits
            && quotaSnapshot.BackgroundMinuteBudget == snapshot.BackgroundMinuteBudget)
        {
            return;
        }

        quotaSnapshot = snapshot;
        OnPropertyChanged(nameof(QuotaSnapshot));
        OnPropertyChanged(nameof(QuotaStatusText));
        OnPropertyChanged(nameof(QuotaStatusDetail));
        OnPropertyChanged(nameof(QuotaStatusAnnouncement));
    }

    public ObservableCollection<MailFolder> Folders { get; } = [];

    public ObservableCollection<MailThreadSummary> Threads { get; } = [];

    public ObservableCollection<MailSignature> Signatures { get; } = [];

    public ObservableCollection<string> ComposeAttachmentPaths { get; } = [];

    public ObservableCollection<MailAttachmentInput> ComposeRetainedAttachments { get; } = [];

    public IReadOnlyList<MailThreadSummary> LastSyncNotificationCandidates { get; private set; } = [];

    public bool LastRefreshSucceeded { get; private set; }

    public GmailQuotaSnapshot QuotaSnapshot => quotaSnapshot;

    public string QuotaStatusText =>
        $"Gmail API units/min — this GLook process: {QuotaSnapshot.RollingMinuteUnits:N0} / {GmailQuotaGovernor.UserUnitsPerMinute:N0} estimated";

    public string QuotaStatusDetail =>
        $"A rolling 60-second local estimate for the active mailbox. Recent activity is retained across restarts on this PC, but Google Cloud telemetry is authoritative across all installations. "
        + $"Google's limit is {GmailQuotaGovernor.UserUnitsPerMinute:N0} units per user per Cloud project per minute; GLook pauses background work at {QuotaSnapshot.BackgroundMinuteBudget:N0}. "
        + $"Estimated use over the last 24 hours is {QuotaSnapshot.EstimatedDailyUnits:N0} units. State: {QuotaSnapshot.State}.";

    public string QuotaStatusAnnouncement =>
        $"{QuotaStatusText}. {QuotaSnapshot.State}.";

    [ObservableProperty]
    public partial MailFolder? SelectedFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedThread))]
    [NotifyPropertyChangedFor(nameof(HasActionSelection))]
    public partial MailThreadSummary? SelectedThread { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThreadDetail))]
    public partial MailThreadDetail? SelectedThreadDetail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSync))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAccount))]
    [NotifyPropertyChangedFor(nameof(ConnectionButtonText))]
    [NotifyPropertyChangedFor(nameof(ConnectionButtonAccessibleName))]
    [NotifyPropertyChangedFor(nameof(CanSendCompose))]
    [NotifyPropertyChangedFor(nameof(CanSync))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionButtonText))]
    [NotifyPropertyChangedFor(nameof(ConnectionButtonAccessibleName))]
    public partial string AccountEmail { get; set; } = "No account connected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncStatusAnnouncement))]
    public partial string SyncStatusText { get; set; } = "Ready to connect";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncStatusAnnouncement))]
    public partial string SyncStatusDetail { get; set; } = "Connect your Google account to begin.";

    [ObservableProperty]
    public partial string SyncGlyph { get; set; } = "\uE895";

    [ObservableProperty]
    public partial bool IsSyncingAll { get; set; }

    [ObservableProperty]
    public partial double SyncProgressValue { get; set; }

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendCompose))]
    public partial bool IsComposeOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendCompose))]
    public partial bool IsComposeBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendCompose))]
    public partial string ComposeTo { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendCompose))]
    public partial string ComposeCc { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendCompose))]
    public partial string ComposeBcc { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ComposeSubject { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendCompose))]
    public partial string ComposeBody { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ComposeDraftId { get; set; }

    [ObservableProperty]
    public partial string ComposeErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial MailComposeMode ComposeMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionSelection))]
    public partial int SelectedThreadCount { get; set; }

    [ObservableProperty]
    public partial string? ComposeThreadId { get; set; }

    [ObservableProperty]
    public partial string? NewSignatureEmail { get; set; }

    [ObservableProperty]
    public partial string? ReplySignatureEmail { get; set; }

    [ObservableProperty]
    public partial string? ForwardSignatureEmail { get; set; }

    public bool NeedsAccount => !IsConnected;

    public bool HasSelectedThread => SelectedThread is not null;

    public bool HasActionSelection => SelectedThreadCount > 0 || HasSelectedThread;

    public bool HasThreadDetail => SelectedThreadDetail is not null;

    public bool CanSendCompose => IsConnected
        && IsComposeOpen
        && !IsComposeBusy
        && (!string.IsNullOrWhiteSpace(ComposeTo)
            || !string.IsNullOrWhiteSpace(ComposeCc)
            || !string.IsNullOrWhiteSpace(ComposeBcc));

    public bool CanSync => IsConnected && !IsBusy;

    public string SyncStatusAnnouncement => $"{SyncStatusText} — {SyncStatusDetail}";

    public string ConnectionButtonText => IsConnected ? AccountEmail : "Connect Gmail";

    public string ConnectionButtonAccessibleName => IsConnected
        ? $"Gmail account {AccountEmail}"
        : "Connect Gmail";

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default,
        bool hydrateSelectedFolder = true)
    {
        using var linkedInitialization = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeToken);
        cancellationToken = linkedInitialization.Token;
        IsBusy = true;
        SyncStatusText = "Checking your Gmail mirror";
        SyncStatusDetail = "Looking for a securely stored account session.";
        try
        {
            var gateEntered = false;
            GmailAccountProfile? profile;
            try
            {
                if (operationGate is not null)
                {
                    await operationGate.WaitAsync(cancellationToken);
                    gateEntered = true;
                }

                await localStore.InitializeAsync();
                var cachedFolders = await localStore.LoadFoldersAsync();
                if (cachedFolders.Count > 0)
                {
                    ReplaceFolders(cachedFolders, SelectedFolder?.Id ?? "INBOX");
                    folderListRefreshedAt = await localStore.GetFolderSnapshotUpdatedAtAsync()
                        ?? DateTimeOffset.MinValue;
                }
                foreach (var folderId in await localStore.LoadHydratedFolderIdsAsync())
                {
                    hydratedFolderIds.Add(folderId);
                }
                await LoadCachedThreadsAsync();
                profile = await gmail.TryReconnectAsync(cancellationToken);
            }
            finally
            {
                if (gateEntered)
                {
                    operationGate!.Release();
                }
            }

            if (profile is null)
            {
                if (!hasPreBoundAccountScope)
                {
                    localStore.SetAccountScope(null);
                }
                SetDisconnectedStatus(Threads.Count > 0 || Folders.Count > 0);
                return;
            }

            ApplyProfile(profile);
            await LoadCachedSignatureSettingsAsync();
            await RefreshAsync(cancellationToken, hydrateSelectedFolder);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            AccountEmail = "Reconnect required";
            SyncStatusText = "Gmail needs attention";
            SyncStatusDetail = UserFacingError(ex);
            SyncGlyph = "\uE7BA";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ConnectAsync()
    {
        IsBusy = true;
        SyncStatusText = "Waiting for Google";
        SyncStatusDetail = "Finish signing in and approving Gmail access in your browser.";
        SyncGlyph = "\uE72E";
        try
        {
            {
                using var accountOperation = await AcquireAccountOperationAsync();
                var profile = await gmail.ConnectAsync();
                ApplyProfile(profile);
                await LoadCachedSignatureSettingsAsync();
            }
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            SyncStatusText = "Could not connect Gmail";
            SyncStatusDetail = UserFacingError(ex);
            SyncGlyph = "\uEA39";
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshAsync(
        CancellationToken cancellationToken = default,
        bool hydrateSelectedFolder = true)
    {
        if (!IsConnected)
        {
            await LoadCachedThreadsAsync();
            return;
        }

        var operationCancellation = BeginRefreshOperation(cancellationToken);
        cancellationToken = operationCancellation.Token;
        var gateEntered = false;

        IsBusy = true;
        LastRefreshSucceeded = false;
        SyncProgressValue = 0;
        SyncStatusText = "Syncing with Gmail";
        SyncStatusDetail = $"Checking Gmail history for {SelectedFolder?.Name ?? "Inbox"}.";
        SyncGlyph = "\uE895";
        LastSyncNotificationCandidates = [];
        try
        {
            if (operationGate is not null)
            {
                await operationGate.WaitAsync(cancellationToken);
                gateEntered = true;
            }

            var selectedId = SelectedFolder?.Id ?? "INBOX";
            if (Folders.Count == 0
                || folderListRefreshedAt <= DateTimeOffset.UtcNow.AddMinutes(-30))
            {
                var folders = await gmail.GetFoldersAsync(cancellationToken);
                await localStore.SaveFoldersAsync(folders);
                ReplaceFolders(folders, selectedId);
                selectedId = SelectedFolder?.Id ?? "INBOX";
                folderListRefreshedAt = DateTimeOffset.UtcNow;
            }

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                var searchResults = await gmail.GetThreadsAsync(
                    selectedId,
                    SearchQuery,
                    cancellationToken: cancellationToken);
                ReplaceThreads(searchResults);
                LastSyncNotificationCandidates = [];
                SyncStatusText = "Search results are current";
                SyncStatusDetail = $"Found {searchResults.Count} recent matching conversations.";
                SyncGlyph = "\uE73E";
                LastRefreshSucceeded = true;
                return;
            }

            Action<string> quotaStatus = message => SyncStatusDetail = message;
            var cursor = await localStore.GetHistoryCursorAsync();
            var sync = cursor is null
                ? await gmail.GetBoundedBootstrapAsync(status: quotaStatus, cancellationToken: cancellationToken)
                : await gmail.GetHistoryChangesAsync(cursor.Value, quotaStatus, cancellationToken);
            var rebuilt = sync.RequiresBootstrap;
            if (rebuilt)
            {
                SyncStatusText = "Rebuilding the local mirror";
                SyncStatusDetail = "The saved Gmail history cursor expired; loading a bounded recent snapshot.";
                sync = await gmail.GetBoundedBootstrapAsync(status: quotaStatus, cancellationToken: cancellationToken);
            }

            // A bounded bootstrap is a reconciliation seed, not a complete
            // account snapshot. Merge it so mail retained locally from older
            // sessions/builds is not erased merely because Gmail history
            // expired or the changed set exceeded the incremental budget.
            var usedBootstrap = rebuilt || cursor is null;
            await localStore.ApplyHistorySyncAsync(sync, replaceAccountSnapshot: false);
            if (rebuilt)
            {
                await localStore.ClearHydratedFolderIdsAsync();
                hydratedFolderIds.Clear();
                partialFolderWindowStarts.Clear();
            }

            LastSyncNotificationCandidates = sync.NotificationCandidates;
            await LoadCachedThreadsAsync();
            if (hydrateSelectedFolder && (usedBootstrap || !hydratedFolderIds.Contains(selectedId)))
            {
                SyncStatusDetail = $"Loading a bounded recent page for {SelectedFolder?.FullName ?? "Inbox"}.";
                var recentFolderThreads = await gmail.GetThreadsAsync(
                    selectedId,
                    query: null,
                    maxResults: 50,
                    cancellationToken: cancellationToken);
                var completeFolderSnapshot = recentFolderThreads.Count < 50;
                DateTimeOffset? recentWindowStart = completeFolderSnapshot || recentFolderThreads.Count == 0
                    ? null
                    : recentFolderThreads.Min(thread => thread.ReceivedAt);
                await localStore.SaveFolderSnapshotAsync(
                    selectedId,
                    recentFolderThreads,
                    reconcileMissing: completeFolderSnapshot,
                    reconcileMissingOnOrAfter: recentWindowStart);
                if (completeFolderSnapshot)
                {
                    partialFolderWindowStarts.Remove(selectedId);
                    await localStore.MarkFolderHydratedAsync(selectedId);
                    hydratedFolderIds.Add(selectedId);
                }
                else if (recentWindowStart is DateTimeOffset windowStart)
                {
                    partialFolderWindowStarts[selectedId] = windowStart;
                    hydratedFolderIds.Remove(selectedId);
                }
                ReplaceThreads(recentFolderThreads);
            }

            var quota = gmail.QuotaSnapshot;
            SyncStatusText = sync.HistoryCatchUpPending
                ? "Gmail mirror catch-up is continuing"
                : "Gmail mirror is current";
            SyncStatusDetail = sync.HistoryCatchUpPending
                ? $"Applied {sync.ChangedThreadCount} changed conversations and saved a resumable history checkpoint. The next sync will continue the backlog. Gmail API use: {quota.RollingMinuteUnits:N0}/{GmailQuotaGovernor.UserUnitsPerMinute:N0} estimated units in the rolling minute."
                : usedBootstrap
                ? $"Reconciled {sync.UpsertedThreads.Count} recent conversations into the retained local mirror. Gmail API use: {quota.RollingMinuteUnits:N0}/{GmailQuotaGovernor.UserUnitsPerMinute:N0} estimated units in this process's rolling minute (background guard {quota.BackgroundMinuteBudget:N0})."
                : $"Applied {sync.ChangedThreadCount} changed conversation{(sync.ChangedThreadCount == 1 ? string.Empty : "s")} from Gmail history. Gmail API use: {quota.RollingMinuteUnits:N0}/{GmailQuotaGovernor.UserUnitsPerMinute:N0} units in the rolling minute (background guard {quota.BackgroundMinuteBudget:N0}).";
            SyncGlyph = "\uE73E";
            SyncProgressValue = 100;
            LastRefreshSucceeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SyncStatusText = "Showing the local mirror";
            SyncStatusDetail = $"Gmail could not be reached. {UserFacingError(ex)}";
            SyncGlyph = "\uE774";
            await LoadCachedThreadsAsync();
        }
        finally
        {
            if (gateEntered)
            {
                operationGate!.Release();
            }

            CompleteRefreshOperation(operationCancellation);
        }
    }

    public async Task<bool> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            await LoadCachedThreadsAsync();
            return false;
        }

        IsSyncingAll = true;
        SyncProgressValue = 0;
        SyncStatusText = "Syncing mailbox history";
        SyncStatusDetail = "Checking Gmail once for changes across all labels and folders.";
        SyncGlyph = "\uE895";
        try
        {
            await RefreshAsync(cancellationToken);
            return SyncGlyph == "\uE73E";
        }
        finally
        {
            IsSyncingAll = false;
        }
    }

    public void CancelSyncAll()
    {
        if (IsSyncingAll)
        {
            refreshCancellation?.Cancel();
        }
    }

    public async Task SelectFolderAsync(MailFolder? folder)
    {
        if (folder is null || SelectedFolder?.Id == folder.Id)
        {
            return;
        }

        SelectedFolder = folder;
        SelectedThread = null;
        SelectedThreadDetail = null;
        SearchQuery = string.Empty;
        await RefreshAsync();
    }

    public async Task SelectThreadAsync(MailThreadSummary? thread)
    {
        var priorSelection = selectionCancellation;
        priorSelection?.Cancel();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        selectionCancellation = operation;
        var generation = Interlocked.Increment(ref selectionGeneration);
        SelectedThread = thread;
        SelectedThreadDetail = null;
        if (thread is null || !IsConnected)
        {
            if (ReferenceEquals(selectionCancellation, operation))
            {
                selectionCancellation = null;
            }
            operation.Dispose();
            return;
        }

        IsBusy = true;
        try
        {
            using var accountOperation = await AcquireAccountOperationAsync(operation.Token);
            if (!TryGetCachedThreadDetail(thread.Id, out var detail))
            {
                detail = await gmail.GetThreadAsync(thread.Id, operation.Token);
                CacheThreadDetail(detail);
            }

            if (generation != selectionGeneration || SelectedThread?.Id != thread.Id)
            {
                return;
            }
            SelectedThreadDetail = detail;
            if (thread.IsUnread)
            {
                await gmail.SetThreadReadAsync(thread.Id, true, operation.Token);
                if (generation != selectionGeneration || SelectedThread?.Id != thread.Id)
                {
                    return;
                }
                var index = Threads.IndexOf(thread);
                if (index >= 0)
                {
                    var updated = thread with
                    {
                        IsUnread = false,
                        LabelIds = thread.LabelIds
                            .Where(labelId => !string.Equals(labelId, "UNREAD", StringComparison.Ordinal))
                            .ToArray()
                    };
                    Threads[index] = updated;
                    SelectedThread = updated;
                    await localStore.SaveFolderSnapshotAsync(
                        SelectedFolder?.Id ?? "INBOX",
                        [updated],
                        reconcileMissing: false);
                    AdjustFolderUnreadCounts(thread.LabelIds, -1);
                }
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SyncStatusText = "Could not open conversation";
            SyncStatusDetail = UserFacingError(ex);
            SyncGlyph = "\uEA39";
        }
        finally
        {
            if (ReferenceEquals(selectionCancellation, operation))
            {
                selectionCancellation = null;
                IsBusy = false;
            }
            operation.Dispose();
        }
    }

    public async Task SearchAsync()
    {
        await RefreshAsync();
    }

    public async Task<IReadOnlyList<MailThreadSummary>> GetUnreadInboxSampleAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return [];
        }

        using var accountOperation = await AcquireAccountOperationAsync(cancellationToken);
        return await gmail.GetThreadsAsync("INBOX", "is:unread", 10, cancellationToken);
    }

    public Task TrashSelectedThreadAsync() => TrashThreadsAsync(CurrentThreadSelection());

    public Task TrashThreadsAsync(IEnumerable<MailThreadSummary> threads) =>
        RunThreadMutationAsync(
            threads,
            "Moving conversations to Trash",
            (thread, cancellationToken) => gmail.TrashThreadAsync(thread.Id, cancellationToken),
            summary => summary with
            {
                LabelIds = summary.LabelIds
                    .Where(id => id is not ("INBOX" or "SPAM"))
                    .Append("TRASH")
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            },
            removeFromCurrentFolder: true);

    public void StartNewCompose()
    {
        ResetCompose();
        ComposeBody = ApplyDefaultSignature(string.Empty, MailComposeMode.New);
        IsComposeOpen = true;
    }

    public void StartReply()
    {
        var message = SelectedThreadDetail?.Messages.LastOrDefault();
        if (message is null)
        {
            return;
        }

        ResetCompose();
        ComposeMode = MailComposeMode.Reply;
        ComposeThreadId = SelectedThreadDetail?.Id;
        composeInReplyTo = message.RfcMessageId;
        composeReferences = BuildReferences(message);
        ComposeTo = string.IsNullOrWhiteSpace(message.ReplyTo) ? message.From : message.ReplyTo;
        ComposeSubject = PrefixSubject(message.Subject, "Re:");
        ComposeBody = ApplyDefaultSignature(QuoteMessage(message), MailComposeMode.Reply);
        IsComposeOpen = true;
    }

    public void StartReplyAll()
    {
        var message = SelectedThreadDetail?.Messages.LastOrDefault();
        if (message is null)
        {
            return;
        }

        ResetCompose();
        ComposeMode = MailComposeMode.ReplyAll;
        ComposeThreadId = SelectedThreadDetail?.Id;
        composeInReplyTo = message.RfcMessageId;
        composeReferences = BuildReferences(message);
        ComposeTo = string.IsNullOrWhiteSpace(message.ReplyTo) ? message.From : message.ReplyTo;
        ComposeCc = ReplyAllRecipients(message);
        ComposeSubject = PrefixSubject(message.Subject, "Re:");
        ComposeBody = ApplyDefaultSignature(QuoteMessage(message), MailComposeMode.ReplyAll);
        IsComposeOpen = true;
    }

    public Task StartReplyAsync()
    {
        var detail = SelectedThreadDetail;
        if (detail is not null
            && detail.Id == SelectedThread?.Id
            && detail.Messages.Count > 0)
        {
            StartReply();
            return Task.CompletedTask;
        }

        return PrepareReplyAsync(replyAll: false);
    }

    public Task StartReplyAllAsync()
    {
        var detail = SelectedThreadDetail;
        if (detail is not null
            && detail.Id == SelectedThread?.Id
            && detail.Messages.Count > 0)
        {
            StartReplyAll();
            return Task.CompletedTask;
        }

        return PrepareReplyAsync(replyAll: true);
    }

    public async Task StartForwardAsync()
    {
        var thread = SelectedThread;
        if (thread is null || !IsConnected)
        {
            return;
        }

        try
        {
            using var accountOperation = await AcquireAccountOperationAsync();
            ApplyComposeRequest(
                await gmail.CreateForwardRequestAsync(thread.Id, lifetimeToken),
                MailComposeMode.Forward);
        }
        catch (Exception ex)
        {
            SyncStatusText = "Could not prepare forward";
            SyncStatusDetail = UserFacingError(ex);
            SyncGlyph = "\uEA39";
        }
    }

    public void StartForward()
    {
        var message = SelectedThreadDetail?.Messages.LastOrDefault();
        if (message is null)
        {
            return;
        }

        ResetCompose();
        ComposeMode = MailComposeMode.Forward;
        ComposeSubject = PrefixSubject(message.Subject, "Fwd:");
        ComposeBody = ApplyDefaultSignature(ForwardMessage(message), MailComposeMode.Forward);
        IsComposeOpen = true;
    }

    public async Task RefreshSignaturesAsync()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Connect Gmail before managing signatures.");
        }

        IsBusy = true;
        SyncStatusText = "Loading Gmail signatures";
        SyncStatusDetail = "Google may ask you to approve Gmail settings access.";
        try
        {
            using var accountOperation = await AcquireAccountOperationAsync();
            var localSignatures = Signatures
                .Where(signature => !signature.IsGmailBacked)
                .ToList();
            var signatures = await gmail.GetSignaturesAsync(lifetimeToken);
            Signatures.Clear();
            foreach (var signature in signatures)
            {
                Signatures.Add(signature);
            }

            foreach (var signature in localSignatures)
            {
                Signatures.Add(signature);
            }

            var defaultId = signatures.FirstOrDefault(item => item.IsDefault)?.Id
                ?? signatures.FirstOrDefault(item => item.IsPrimary)?.Id;
            NewSignatureEmail = ValidSignatureChoice(NewSignatureEmail) ?? defaultId;
            ReplySignatureEmail = ValidSignatureChoice(ReplySignatureEmail) ?? defaultId;
            ForwardSignatureEmail = ValidSignatureChoice(ForwardSignatureEmail) ?? defaultId;
            await PersistSignatureSettingsAsync();
            SyncStatusText = "Gmail signatures loaded";
            SyncStatusDetail = $"Loaded {signatures.Count} Gmail signature{(signatures.Count == 1 ? string.Empty : "s")} and {localSignatures.Count} GLook-only signature{(localSignatures.Count == 1 ? string.Empty : "s")}.";
            SyncGlyph = "\uE73E";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveSignatureSettingsAsync(
        IReadOnlyList<MailSignature> editedSignatures,
        string? newSignatureEmail,
        string? replySignatureEmail,
        string? forwardSignatureEmail)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Connect Gmail before saving signatures.");
        }

        IsBusy = true;
        SyncStatusText = "Saving Gmail signatures";
        try
        {
            using var accountOperation = await AcquireAccountOperationAsync();
            var saved = new List<MailSignature>(editedSignatures.Count);
            foreach (var edited in editedSignatures)
            {
                var original = Signatures.FirstOrDefault(item =>
                    string.Equals(item.Id, edited.Id, StringComparison.Ordinal));
                if (!edited.IsGmailBacked || original is null
                    || string.Equals(original.Text, edited.Text.Trim(), StringComparison.Ordinal))
                {
                    saved.Add(edited with { Text = edited.Text.Trim() });
                    continue;
                }

                saved.Add(await gmail.UpdateSignatureAsync(original, edited.Text, lifetimeToken));
            }

            Signatures.Clear();
            foreach (var signature in saved)
            {
                Signatures.Add(signature);
            }

            NewSignatureEmail = NormalizeSignatureChoice(newSignatureEmail);
            ReplySignatureEmail = NormalizeSignatureChoice(replySignatureEmail);
            ForwardSignatureEmail = NormalizeSignatureChoice(forwardSignatureEmail);
            await PersistSignatureSettingsAsync();
            SyncStatusText = "Signature settings saved";
            SyncStatusDetail = "Gmail-backed signatures, local signature choices, and compose defaults are current.";
            SyncGlyph = "\uE73E";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AddComposeAttachments(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            if (!ComposeAttachmentPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                ComposeAttachmentPaths.Add(path);
            }
        }
    }

    public void RemoveComposeAttachment(string path)
    {
        var existing = ComposeAttachmentPaths.FirstOrDefault(item =>
            string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ComposeAttachmentPaths.Remove(existing);
        }
    }

    public void RemoveComposeAttachment(MailAttachmentInput attachment)
    {
        ComposeRetainedAttachments.Remove(attachment);
    }

    public async Task SendComposeAsync()
    {
        if (!CanSendCompose)
        {
            ComposeErrorText = !IsConnected
                ? "Connect Gmail before sending a message."
                : "Enter at least one recipient.";
            return;
        }

        IsComposeBusy = true;
        ComposeErrorText = string.Empty;
        var messageSent = false;
        try
        {
            using var accountOperation = await AcquireAccountOperationAsync();
            var savedDraftId = ComposeDraftId;
            if (string.IsNullOrWhiteSpace(savedDraftId))
            {
                await gmail.SendMessageAsync(await CreateComposeRequestAsync(), lifetimeToken);
            }
            else
            {
                await gmail.SendDraftAsync(
                    savedDraftId,
                    await CreateComposeRequestAsync(),
                    lifetimeToken);
            }
            messageSent = true;
            ResetCompose();

            SyncStatusText = "Message sent";
            SyncStatusDetail = "Gmail accepted the message for delivery. The next scheduled history sync will reconcile the local mirror without an immediate follow-up read.";
            SyncGlyph = "\uE73E";
        }
        catch (Exception ex)
        {
            if (messageSent)
            {
                SyncStatusText = "Message sent";
                SyncStatusDetail = $"Gmail accepted the message, but the view could not refresh. {UserFacingError(ex)}";
                SyncGlyph = "\uE774";
            }
            else
            {
                ComposeErrorText = UserFacingError(ex);
                SyncStatusText = "Message was not sent";
                SyncStatusDetail = ComposeErrorText;
                SyncGlyph = "\uEA39";
            }
        }
        finally
        {
            IsComposeBusy = false;
        }
    }

    public async Task SaveDraftAsync()
    {
        if (!IsConnected || !IsComposeOpen)
        {
            return;
        }

        IsComposeBusy = true;
        ComposeErrorText = string.Empty;
        var draftSaved = false;
        try
        {
            using var accountOperation = await AcquireAccountOperationAsync();
            var createdDraft = string.IsNullOrWhiteSpace(ComposeDraftId);
            var result = await gmail.SaveDraftAsync(
                await CreateComposeRequestAsync(),
                ComposeDraftId,
                lifetimeToken);
            draftSaved = true;
            ComposeDraftId = result.DraftId;
            if (createdDraft)
            {
                IncrementFolderThreadCount("DRAFT");
            }
            SyncStatusText = "Draft saved";
            SyncStatusDetail = "The draft is available in Gmail. Folder totals will be confirmed by the scheduled sync.";
            SyncGlyph = "\uE73E";
        }
        catch (Exception ex)
        {
            if (draftSaved)
            {
                SyncStatusText = "Draft saved";
                SyncStatusDetail = $"The Gmail draft was saved, but folder counts could not refresh. {UserFacingError(ex)}";
                SyncGlyph = "\uE774";
            }
            else
            {
                ComposeErrorText = UserFacingError(ex);
                SyncStatusText = "Draft was not saved";
                SyncStatusDetail = ComposeErrorText;
                SyncGlyph = "\uEA39";
            }
        }
        finally
        {
            IsComposeBusy = false;
        }
    }

    public async Task CloseComposeAsync(bool discardDraft = false)
    {
        if (discardDraft && IsConnected && !string.IsNullOrWhiteSpace(ComposeDraftId))
        {
            IsComposeBusy = true;
            try
            {
                using var accountOperation = await AcquireAccountOperationAsync();
                await gmail.DeleteDraftAsync(ComposeDraftId, lifetimeToken);
            }
            finally
            {
                IsComposeBusy = false;
            }
        }

        ResetCompose();
    }

    public Task ArchiveSelectedThreadAsync() => ArchiveThreadsAsync(CurrentThreadSelection());

    public Task ArchiveThreadsAsync(IEnumerable<MailThreadSummary> threads) =>
        RunThreadMutationAsync(
            threads,
            "Archiving conversations",
            (thread, cancellationToken) => gmail.ArchiveThreadAsync(thread.Id, cancellationToken),
            summary => summary with
            {
                LabelIds = summary.LabelIds
                    .Where(id => !string.Equals(id, "INBOX", StringComparison.Ordinal))
                    .ToList()
            },
            removeFromCurrentFolder: SelectedFolder?.Id == "INBOX");

    public Task ToggleSelectedThreadReadAsync() =>
        SetSelectedThreadReadAsync(SelectedThread?.IsUnread ?? false);

    public Task SetSelectedThreadReadAsync(bool isRead) =>
        SetThreadsReadAsync(CurrentThreadSelection(), isRead);

    public Task SetThreadsReadAsync(IEnumerable<MailThreadSummary> threads, bool isRead) =>
        RunThreadMutationAsync(
            threads,
            isRead ? "Marking conversations read" : "Marking conversations unread",
            (thread, cancellationToken) => gmail.SetThreadReadAsync(thread.Id, isRead, cancellationToken),
            summary => summary with
            {
                IsUnread = !isRead,
                LabelIds = isRead
                    ? summary.LabelIds
                        .Where(id => !string.Equals(id, "UNREAD", StringComparison.Ordinal))
                        .ToList()
                    : summary.LabelIds
                        .Append("UNREAD")
                        .Distinct(StringComparer.Ordinal)
                        .ToList()
            });

    public Task ToggleSelectedThreadStarAsync() =>
        SetSelectedThreadStarredAsync(!(SelectedThread?.IsStarred ?? true));

    public Task SetSelectedThreadStarredAsync(bool isStarred) =>
        SetThreadsStarredAsync(CurrentThreadSelection(), isStarred);

    public Task SetThreadsStarredAsync(IEnumerable<MailThreadSummary> threads, bool isStarred) =>
        RunThreadMutationAsync(
            threads,
            isStarred ? "Starring conversations" : "Removing stars",
            (thread, cancellationToken) => gmail.SetThreadStarredAsync(thread.Id, isStarred, cancellationToken),
            summary => summary with
            {
                IsStarred = isStarred,
                LabelIds = isStarred
                    ? summary.LabelIds
                        .Append("STARRED")
                        .Distinct(StringComparer.Ordinal)
                        .ToList()
                    : summary.LabelIds
                        .Where(id => !string.Equals(id, "STARRED", StringComparison.Ordinal))
                        .ToList()
            });

    public Task MarkSelectedThreadJunkAsync(bool isJunk = true) =>
        MarkThreadsJunkAsync(CurrentThreadSelection(), isJunk);

    public Task MarkThreadsJunkAsync(IEnumerable<MailThreadSummary> threads, bool isJunk = true) =>
        RunThreadMutationAsync(
            threads,
            isJunk ? "Moving conversations to Spam" : "Removing conversations from Spam",
            (thread, cancellationToken) => gmail.SetThreadSpamAsync(thread.Id, isJunk, cancellationToken),
            summary => summary with
            {
                LabelIds = isJunk
                    ? summary.LabelIds
                        .Where(id => id is not ("INBOX" or "TRASH"))
                        .Append("SPAM")
                        .Distinct(StringComparer.Ordinal)
                        .ToList()
                    : summary.LabelIds
                        .Where(id => id != "SPAM")
                        .Append("INBOX")
                        .Distinct(StringComparer.Ordinal)
                        .ToList()
            },
            removeFromCurrentFolder: true);

    public Task MarkSelectedThreadNotJunkAsync() => MarkSelectedThreadJunkAsync(isJunk: false);

    public Task RestoreSelectedThreadFromTrashAsync() => RestoreThreadsFromTrashAsync(CurrentThreadSelection());

    public Task RestoreThreadsFromTrashAsync(IEnumerable<MailThreadSummary> threads) =>
        RunThreadMutationAsync(
            threads,
            "Restoring conversations from Trash",
            (thread, cancellationToken) => gmail.RestoreThreadFromTrashAsync(thread.Id, cancellationToken),
            summary => summary with
            {
                LabelIds = summary.LabelIds
                    .Where(id => id != "TRASH")
                    .Append("INBOX")
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            },
            removeFromCurrentFolder: SelectedFolder?.Id == "TRASH");

    public Task AddLabelToSelectedThreadAsync(MailFolder label) =>
        AddLabelToThreadsAsync(CurrentThreadSelection(), label);

    public Task AddLabelToThreadsAsync(IEnumerable<MailThreadSummary> threads, MailFolder label) =>
        RunThreadMutationAsync(
            threads,
            $"Adding {label.FullName}",
            (thread, cancellationToken) => gmail.ModifyThreadLabelsAsync(thread.Id, [label.Id], null, cancellationToken),
            summary => summary with
            {
                LabelIds = summary.LabelIds.Append(label.Id).Distinct(StringComparer.Ordinal).ToList()
            });

    public Task RemoveLabelFromSelectedThreadAsync(MailFolder label) =>
        RemoveLabelFromThreadsAsync(CurrentThreadSelection(), label);

    public Task RemoveLabelFromThreadsAsync(IEnumerable<MailThreadSummary> threads, MailFolder label) =>
        RunThreadMutationAsync(
            threads,
            $"Removing {label.FullName}",
            (thread, cancellationToken) => gmail.ModifyThreadLabelsAsync(thread.Id, null, [label.Id], cancellationToken),
            summary => summary with
            {
                LabelIds = summary.LabelIds.Where(id => !string.Equals(id, label.Id, StringComparison.Ordinal)).ToList()
            },
            removeFromCurrentFolder: SelectedFolder?.Id == label.Id);

    public Task MoveSelectedThreadAsync(MailFolder targetFolder) =>
        MoveThreadsAsync(CurrentThreadSelection(), targetFolder);

    public Task MoveThreadsAsync(IEnumerable<MailThreadSummary> threads, MailFolder targetFolder)
    {
        var sourceFolderId = SelectedFolder is { CanDelete: true } || SelectedFolder?.Id == "INBOX"
            ? SelectedFolder.Id
            : null;
        return RunThreadMutationAsync(
            threads,
            $"Moving conversation to {targetFolder.FullName}",
            (thread, cancellationToken) => gmail.MoveThreadAsync(
                thread.Id,
                sourceFolderId,
                targetFolder.Id,
                cancellationToken),
            summary => summary with
            {
                LabelIds = summary.LabelIds
                    .Where(id => !string.Equals(id, sourceFolderId, StringComparison.Ordinal))
                    .Append(targetFolder.Id)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            },
            removeFromCurrentFolder: sourceFolderId is not null
                && !string.Equals(sourceFolderId, targetFolder.Id, StringComparison.Ordinal));
    }

    public Task ApplyLabelsToSelectedThreadAsync(
        IEnumerable<MailFolder> addLabels,
        IEnumerable<MailFolder> removeLabels)
    {
        var additions = addLabels.Select(label => label.Id).Distinct(StringComparer.Ordinal).ToArray();
        var removals = removeLabels.Select(label => label.Id).Distinct(StringComparer.Ordinal).ToArray();
        return RunThreadMutationAsync(
            CurrentThreadSelection(),
            "Updating conversation labels",
            (thread, cancellationToken) => gmail.ModifyThreadLabelsAsync(
                thread.Id,
                additions,
                removals,
                cancellationToken),
            summary => summary with
            {
                LabelIds = summary.LabelIds
                    .Concat(additions)
                    .Where(id => !removals.Contains(id, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            },
            removeFromCurrentFolder: SelectedFolder is not null
                && removals.Contains(SelectedFolder.Id, StringComparer.Ordinal));
    }

    public async Task ApplyBoardLabelsAsync(
        MailThreadSummary thread,
        string targetLabelId,
        string? sourceLabelId,
        bool move,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLabelId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token,
            lifetimeToken);
        var gateEntered = false;
        IsBusy = true;
        try
        {
            if (operationGate is not null)
            {
                await operationGate.WaitAsync(linked.Token);
                gateEntered = true;
            }

            var removals = move
                && !string.IsNullOrWhiteSpace(sourceLabelId)
                && !string.Equals(sourceLabelId, targetLabelId, StringComparison.Ordinal)
                ? new[] { sourceLabelId }
                : null;
            await gmail.ModifyThreadLabelsAsync(
                thread.Id,
                [targetLabelId],
                removals,
                linked.Token);

            var current = Threads.FirstOrDefault(candidate => candidate.Id == thread.Id);
            var sourceSummary = current ?? thread;
            var updated = sourceSummary with
            {
                LabelIds = sourceSummary.LabelIds
                    .Append(targetLabelId)
                    .Where(labelId => removals is null || !removals.Contains(labelId, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
            await localStore.SaveThreadsAsync([updated]);
            if (current is not null)
            {
                Threads[Threads.IndexOf(current)] = updated;
                if (SelectedThread?.Id == updated.Id)
                {
                    SelectedThread = updated;
                }
            }

            SyncStatusText = move ? "Conversation moved" : "Label added";
            SyncStatusDetail = "The Board change was accepted by Gmail.";
            SyncGlyph = "\uE73E";
        }
        catch (Exception ex)
        {
            SyncStatusText = "Board change did not sync";
            SyncStatusDetail = UserFacingError(ex);
            SyncGlyph = "\uEA39";
            throw;
        }
        finally
        {
            if (gateEntered)
            {
                operationGate!.Release();
            }

            IsBusy = false;
        }
    }

    public async Task CreateFolderAsync(string folderPath)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Connect Gmail before creating a folder.");
        }

        IsBusy = true;
        SyncStatusText = "Creating Gmail label";
        SyncStatusDetail = "Nested folders become slash-separated labels in Gmail.";
        try
        {
            using var accountOperation = await AcquireAccountOperationAsync();
            var folder = await gmail.CreateFolderAsync(folderPath, lifetimeToken);
            if (Folders.All(item => item.Id != folder.Id))
            {
                Folders.Add(folder);
            }

            SyncStatusText = "Folder created in Gmail";
            SyncStatusDetail = folder.FullName;
            SyncGlyph = "\uE73E";
            await RefreshFoldersOnlyAsync(folder.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteSelectedFolderAsync()
    {
        if (SelectedFolder is { } folder)
        {
            await DeleteFolderAsync(folder);
        }
    }

    public async Task DeleteFolderAsync(MailFolder folder)
    {
        if (!folder.CanDelete || !IsConnected)
        {
            return;
        }

        IsBusy = true;
        try
        {
            {
                using var accountOperation = await AcquireAccountOperationAsync();
                await gmail.DeleteFolderAsync(folder.Id, lifetimeToken);
                Folders.Remove(folder);
                if (SelectedFolder?.Id == folder.Id)
                {
                    SelectedFolder = Folders.FirstOrDefault(item => item.Id == "INBOX") ?? Folders.FirstOrDefault();
                }
                await localStore.SaveFoldersAsync(Folders);
                folderListRefreshedAt = DateTimeOffset.UtcNow;
            }
            await RefreshAsync();
            SyncStatusText = "Label removed from Gmail";
            SyncStatusDetail = "The empty label was deleted.";
            SyncGlyph = "\uE73E";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<int> GetFolderThreadCountAsync(
        MailFolder folder,
        CancellationToken cancellationToken = default)
    {
        if (!folder.CanDelete || !IsConnected)
        {
            return 0;
        }

        using var accountOperation = await AcquireAccountOperationAsync(cancellationToken);
        return await gmail.GetFolderThreadCountAsync(folder.Id, cancellationToken);
    }

    public async Task<int> GetTrashMessageCountAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return 0;
        }

        using var accountOperation = await AcquireAccountOperationAsync(cancellationToken);
        return await gmail.GetFolderMessageCountAsync("TRASH", cancellationToken);
    }

    public async Task EmptyTrashAsync()
    {
        if (!IsConnected || IsBusy)
        {
            return;
        }

        IsBusy = true;
        SyncStatusText = "Waiting for permanent-delete permission";
        SyncStatusDetail = "Google may ask you to approve full Gmail access for this destructive action.";
        SyncGlyph = "\uE895";
        try
        {
            EmptyTrashResult result;
            {
                using var accountOperation = await AcquireAccountOperationAsync();
                result = await gmail.EmptyTrashAsync(
                    (completed, total) =>
                    {
                        SyncStatusText = total == 0
                            ? "Trash is already empty"
                            : $"Permanently deleting Trash ({completed}/{total})";
                        SyncStatusDetail = total == 0
                            ? "Gmail reported no messages in Trash."
                            : "Deleted messages cannot be recovered.";
                    },
                    lifetimeToken);
                await ReconcileTrashCacheCoreAsync(clearWithoutFetch: result.RemainingCount == 0);
            }

            SelectedThread = null;
            SelectedThreadDetail = null;
            await RefreshAsync(CancellationToken.None);
            SyncStatusText = result.RemainingCount == 0
                ? "Trash emptied"
                : "Trash changed while emptying";
            SyncStatusDetail = result.RemainingCount == 0
                ? $"Permanently deleted {result.DeletedCount:N0} message{(result.DeletedCount == 1 ? string.Empty : "s")} from Gmail."
                : $"Deleted {result.DeletedCount:N0}; skipped {result.SkippedCount:N0} restored message{(result.SkippedCount == 1 ? string.Empty : "s")}; {result.RemainingCount:N0} newer message{(result.RemainingCount == 1 ? string.Empty : "s")} remain in Trash.";
            SyncGlyph = "\uE73E";
        }
        catch (Exception ex)
        {
            try
            {
                if (IsConnected)
                {
                    using var accountOperation = await AcquireAccountOperationAsync();
                    await ReconcileTrashCacheCoreAsync(clearWithoutFetch: true);
                }
            }
            catch
            {
                // Preserve the original Gmail failure below.
            }
            SyncStatusText = "Empty Trash stopped";
            SyncStatusDetail = $"Some batches may already have been permanently deleted. {UserFacingError(ex)}";
            SyncGlyph = "\uEA39";
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReconcileTrashCacheCoreAsync(bool clearWithoutFetch)
    {
        IReadOnlyList<MailThreadSummary> currentTrash = [];
        if (!clearWithoutFetch)
        {
            try
            {
                using var reconciliationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                currentTrash = await gmail.GetThreadsAsync(
                    "TRASH",
                    query: null,
                    maxResults: 50,
                    cancellationToken: reconciliationCancellation.Token);
            }
            catch
            {
                return;
            }
        }

        try
        {
            var completeTrashSnapshot = clearWithoutFetch || currentTrash.Count < 50;
            DateTimeOffset? recentWindowStart = completeTrashSnapshot || currentTrash.Count == 0
                ? null
                : currentTrash.Min(thread => thread.ReceivedAt);
            await localStore.SaveFolderSnapshotAsync(
                "TRASH",
                currentTrash,
                reconcileMissing: completeTrashSnapshot,
                reconcileMissingOnOrAfter: recentWindowStart);
            await localStore.MarkFolderHydratedAsync("TRASH");
            if (SelectedFolder?.Id == "TRASH")
            {
                ReplaceThreads(currentTrash);
                SelectedThread = null;
                SelectedThreadDetail = null;
            }
        }
        catch
        {
            // Preserve the original Gmail error shown to the user.
        }
    }

    public async Task DeleteFolderAndTrashContentsAsync(MailFolder folder)
    {
        if (!folder.CanDelete || !IsConnected)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromHours(1));
        IsBusy = true;
        SyncStatusText = "Preparing label deletion";
        SyncStatusDetail = $"Finding every conversation in {folder.FullName}.";
        SyncGlyph = "\uE895";
        try
        {
            IReadOnlyList<string> threadIds;
            {
                using var accountOperation = await AcquireAccountOperationAsync(cancellation.Token);
                threadIds = await gmail.TrashFolderThreadsAsync(
                    folder.Id,
                    (completed, total) =>
                    {
                        SyncStatusText = total == 0
                            ? "Deleting empty label"
                            : $"Moving label mail to Trash ({completed}/{total})";
                        SyncStatusDetail = total == 0
                            ? folder.FullName
                            : $"The label will be removed after all {total} conversations are in Gmail Trash.";
                    },
                    cancellation.Token);

                await gmail.DeleteFolderAsync(folder.Id, cancellation.Token);
                foreach (var threadId in threadIds)
                {
                    var cached = await localStore.LoadThreadAsync(threadId);
                    if (cached is not null)
                    {
                        await localStore.SaveThreadsAsync([
                            cached with
                            {
                                LabelIds = cached.LabelIds
                                    .Where(id => id != folder.Id && id is not ("INBOX" or "SPAM"))
                                    .Append("TRASH")
                                    .Distinct(StringComparer.Ordinal)
                                    .ToList()
                            }
                        ]);
                    }
                }

                Folders.Remove(folder);
                if (SelectedFolder?.Id == folder.Id)
                {
                    SelectedFolder = Folders.FirstOrDefault(item => item.Id == "INBOX") ?? Folders.FirstOrDefault();
                }

                SelectedThread = null;
                SelectedThreadDetail = null;
                await localStore.SaveFoldersAsync(Folders);
                folderListRefreshedAt = DateTimeOffset.UtcNow;
            }
            await RefreshAsync();
            SyncStatusText = "Mail moved to Trash and label removed";
            SyncStatusDetail = $"Moved {threadIds.Count} conversation{(threadIds.Count == 1 ? string.Empty : "s")} to Gmail Trash, then deleted {folder.FullName}.";
            SyncGlyph = "\uE73E";
        }
        catch (Exception ex)
        {
            SyncStatusText = "Label deletion stopped";
            SyncStatusDetail = $"Some conversations may already be in Gmail Trash, but the label was not removed unless every move completed. {UserFacingError(ex)}";
            SyncGlyph = "\uEA39";
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RenameFolderAsync(MailFolder folder, string newLeafName)
    {
        if (!folder.CanDelete || !IsConnected)
        {
            throw new InvalidOperationException("Only user-created Gmail labels can be renamed.");
        }

        var normalizedLeaf = newLeafName.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedLeaf) || normalizedLeaf.Contains('/'))
        {
            throw new ArgumentException("Enter one folder name without a slash.", nameof(newLeafName));
        }

        var separator = folder.FullName.LastIndexOf('/');
        var parentPath = separator >= 0 ? folder.FullName[..separator] : string.Empty;
        var newFullName = string.IsNullOrEmpty(parentPath)
            ? normalizedLeaf
            : $"{parentPath}/{normalizedLeaf}";

        IsBusy = true;
        SyncStatusText = "Renaming Gmail label";
        SyncStatusDetail = folder.FullName;
        try
        {
            using var accountOperation = await AcquireAccountOperationAsync();
            await gmail.RenameFolderTreeAsync(folder.FullName, newFullName, lifetimeToken);
            await RefreshFoldersOnlyAsync(folder.Id);
            SyncStatusText = "Label renamed in Gmail";
            SyncStatusDetail = newFullName;
            SyncGlyph = "\uE73E";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisconnectAsync()
    {
        using (await AcquireAccountOperationAsync())
        {
            await localStore.ClearCurrentAccountAsync();
            if (!hasPreBoundAccountScope)
            {
                localStore.SetAccountScope(null);
            }
            await gmail.DisconnectAsync();
            Folders.Clear();
            Threads.Clear();
            SelectedFolder = null;
            SelectedThread = null;
            SelectedThreadDetail = null;
            Signatures.Clear();
            NewSignatureEmail = null;
            ReplySignatureEmail = null;
            ForwardSignatureEmail = null;
            ResetCompose();
            SetDisconnectedStatus();
        }
    }

    private async Task<ComposeMailRequest> CreateComposeRequestAsync(
        CancellationToken cancellationToken = default)
    {
        var rfcMessageId = composeRfcMessageId ??= MimeKit.Utils.MimeUtils.GenerateMessageId();
        var to = ComposeTo.Trim();
        var cc = ComposeCc.Trim();
        var bcc = ComposeBcc.Trim();
        var subject = ComposeSubject.Trim();
        var bodyText = ComposeBody;
        var threadId = ComposeThreadId;
        var inReplyTo = composeInReplyTo;
        var references = composeReferences;
        var retainedAttachments = ComposeRetainedAttachments.ToList();
        var attachmentPaths = ComposeAttachmentPaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (retainedAttachments.Count + attachmentPaths.Count > MailResourceLimits.MaxAttachmentCount)
        {
            throw new InvalidOperationException(
                $"A message can include up to {MailResourceLimits.MaxAttachmentCount} attachments.");
        }

        var attachments = new List<MailAttachmentInput>(retainedAttachments.Count + attachmentPaths.Count);
        long totalBytes = 0;
        foreach (var attachment in retainedAttachments)
        {
            EnsureComposeAttachmentFits(attachment.FileName, attachment.Content.LongLength, totalBytes);
            totalBytes += attachment.Content.LongLength;
            attachments.Add(attachment);
        }

        foreach (var path in attachmentPaths)
        {
            var fileName = Path.GetFileName(path);
            var expectedBytes = new FileInfo(path).Length;
            EnsureComposeAttachmentFits(fileName, expectedBytes, totalBytes);

            var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            EnsureComposeAttachmentFits(fileName, content.LongLength, totalBytes);
            totalBytes += content.LongLength;
            attachments.Add(new MailAttachmentInput(
                fileName,
                content,
                MimeKit.MimeTypes.GetMimeType(path)));
        }

        return new ComposeMailRequest
        {
            RfcMessageId = rfcMessageId,
            To = to,
            Cc = cc,
            Bcc = bcc,
            Subject = subject,
            BodyText = bodyText,
            ThreadId = threadId,
            InReplyTo = inReplyTo,
            References = references,
            Attachments = attachments
        };
    }

    private static void EnsureComposeAttachmentFits(
        string fileName,
        long attachmentBytes,
        long currentTotalBytes)
    {
        if (attachmentBytes > MailResourceLimits.MaxAttachmentBytes)
        {
            throw new InvalidOperationException(
                $"Attachment '{fileName}' is larger than GLook's {MailResourceLimits.MaxAttachmentBytes / (1024 * 1024)} MB per-file limit.");
        }

        if (attachmentBytes > MailResourceLimits.MaxAggregateAttachmentBytes - currentTotalBytes)
        {
            throw new InvalidOperationException(
                $"Attachments exceed GLook's {MailResourceLimits.MaxAggregateAttachmentBytes / (1024 * 1024)} MB total limit.");
        }
    }

    private IReadOnlyList<MailThreadSummary> CurrentThreadSelection() =>
        SelectedThread is null ? [] : [SelectedThread];

    private async Task RunThreadMutationAsync(
        IEnumerable<MailThreadSummary> targetThreads,
        string progressText,
        Func<MailThreadSummary, CancellationToken, Task> mutation,
        Func<MailThreadSummary, MailThreadSummary>? updateSummary = null,
        bool removeFromCurrentFolder = false)
    {
        var threads = targetThreads.DistinctBy(thread => thread.Id).ToList();
        if (threads.Count == 0 || !IsConnected || IsBusy)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellation.Token,
            lifetimeToken);
        IsBusy = true;
        SyncStatusText = progressText;
        SyncStatusDetail = $"Applying this change directly to Gmail for {threads.Count} conversation{(threads.Count == 1 ? string.Empty : "s")}.";
        SyncGlyph = "\uE895";
        var mutationApplied = false;
        var gateEntered = false;
        try
        {
            if (operationGate is not null)
            {
                await operationGate.WaitAsync(linkedCancellation.Token);
                gateEntered = true;
            }

            foreach (var thread in threads)
            {
                await mutation(thread, linkedCancellation.Token);
                mutationApplied = true;
            }

            var projectedThreads = updateSummary is null
                ? []
                : threads.Select(updateSummary).ToList();
            if (projectedThreads.Count > 0)
            {
                await localStore.SaveThreadsAsync(projectedThreads);
                foreach (var pair in threads.Zip(projectedThreads))
                {
                    if (pair.First.IsUnread)
                    {
                        AdjustFolderUnreadCounts(
                            pair.First.LabelIds.Except(pair.Second.IsUnread ? pair.Second.LabelIds : [], StringComparer.Ordinal),
                            -1);
                    }

                    if (pair.Second.IsUnread)
                    {
                        AdjustFolderUnreadCounts(
                            pair.Second.LabelIds.Except(pair.First.IsUnread ? pair.First.LabelIds : [], StringComparer.Ordinal),
                            1);
                    }
                }
            }

            if (removeFromCurrentFolder)
            {
                foreach (var thread in threads)
                {
                    if (updateSummary is null)
                    {
                        await localStore.RemoveThreadAsync(thread.Id);
                    }

                    var current = Threads.FirstOrDefault(item => item.Id == thread.Id);
                    if (current is not null)
                    {
                        Threads.Remove(current);
                    }
                }

                SelectedThread = null;
                SelectedThreadDetail = null;
            }
            else if (updateSummary is not null)
            {
                foreach (var thread in threads)
                {
                    var current = Threads.FirstOrDefault(item => item.Id == thread.Id);
                    if (current is null)
                    {
                        continue;
                    }

                    var updated = projectedThreads.First(item => item.Id == thread.Id);
                    var index = Threads.IndexOf(current);
                    if (index >= 0)
                    {
                        Threads[index] = updated;
                    }

                    if (threads.Count == 1)
                    {
                        SelectedThread = updated;
                    }
                }
            }

            SyncStatusText = threads.Count == 1 ? "Change synced to Gmail" : "Changes synced to Gmail";
            SyncStatusDetail = $"Updated {threads.Count} conversation{(threads.Count == 1 ? string.Empty : "s")} locally and in Gmail. The next scheduled history check will confirm the change without rereading the folder.";
            SyncGlyph = "\uE73E";
        }
        catch (Exception ex)
        {
            if (mutationApplied)
            {
                // Avoid an expensive burst of history + thread reads after a
                // partial batch. The next scheduled delta will authoritatively
                // reconcile whichever requests Gmail accepted.
            }

            SyncStatusText = mutationApplied ? "Some changes may have synced" : "Change did not sync";
            SyncStatusDetail = mutationApplied
                ? $"Gmail accepted at least one change before the operation stopped. {UserFacingError(ex)}"
                : UserFacingError(ex);
            SyncGlyph = mutationApplied ? "\uE774" : "\uEA39";
        }
        finally
        {
            if (gateEntered)
            {
                operationGate!.Release();
            }
            IsBusy = false;
        }
    }

    private async Task<bool> RefreshAfterMutationAsync(
        string? preferredThreadId = null,
        bool applyChangesToCurrentView = false,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return false;
        }

        var cursor = await localStore.GetHistoryCursorAsync();
        if (cursor is null)
        {
            return false;
        }

        var sync = await gmail.GetHistoryChangesAsync(
            cursor.Value,
            message => SyncStatusDetail = message,
            cancellationToken);
        if (sync.RequiresBootstrap)
        {
            return false;
        }

        await localStore.ApplyHistorySyncAsync(sync);
        LastSyncNotificationCandidates = sync.NotificationCandidates;

        if (applyChangesToCurrentView && string.IsNullOrWhiteSpace(SearchQuery))
        {
            ApplyHistoryChangesToCurrentView(sync, preferredThreadId);
        }

        return true;
    }

    private void ApplyHistoryChangesToCurrentView(
        GmailHistorySyncResult sync,
        string? preferredThreadId)
    {
        foreach (var threadId in sync.RemovedThreadIds)
        {
            RemoveCachedThreadDetail(threadId);
        }

        foreach (var changed in sync.UpsertedThreads)
        {
            RemoveCachedThreadDetail(changed.Id);
        }

        var selectedFolderId = SelectedFolder?.Id ?? "INBOX";
        foreach (var threadId in sync.RemovedThreadIds)
        {
            var current = Threads.FirstOrDefault(thread => thread.Id == threadId);
            if (current is not null)
            {
                Threads.Remove(current);
            }
        }

        foreach (var changed in sync.UpsertedThreads)
        {
            var current = Threads.FirstOrDefault(thread => thread.Id == changed.Id);
            var belongsInCurrentFolder = changed.LabelIds.Contains(
                selectedFolderId,
                StringComparer.Ordinal);
            if (current is not null && !belongsInCurrentFolder)
            {
                Threads.Remove(current);
                continue;
            }

            if (current is not null)
            {
                var index = Threads.IndexOf(current);
                if (index >= 0)
                {
                    Threads[index] = changed;
                }
            }
            else if (belongsInCurrentFolder)
            {
                Threads.Add(changed);
            }
        }

        if (SelectedThread is { } selected)
        {
            SelectedThread = Threads.FirstOrDefault(thread => thread.Id == selected.Id);
            if (SelectedThread is null)
            {
                SelectedThreadDetail = null;
            }
        }
        else if (!string.IsNullOrWhiteSpace(preferredThreadId))
        {
            SelectedThread = Threads.FirstOrDefault(thread => thread.Id == preferredThreadId);
        }
    }

    private bool TryGetCachedThreadDetail(string threadId, out MailThreadDetail detail)
    {
        if (!threadDetailCache.TryGetValue(threadId, out detail!))
        {
            return false;
        }

        if (threadDetailLruNodes.TryGetValue(threadId, out var node))
        {
            threadDetailLru.Remove(node);
            threadDetailLru.AddFirst(node);
        }
        return true;
    }

    private void CacheThreadDetail(MailThreadDetail detail)
    {
        RemoveCachedThreadDetail(detail.Id);
        threadDetailCache[detail.Id] = detail;
        var node = threadDetailLru.AddFirst(detail.Id);
        threadDetailLruNodes[detail.Id] = node;
        while (threadDetailCache.Count > MaxCachedThreadDetails
               && threadDetailLru.Last is { } oldest)
        {
            RemoveCachedThreadDetail(oldest.Value);
        }
    }

    private void RemoveCachedThreadDetail(string threadId)
    {
        threadDetailCache.Remove(threadId);
        if (threadDetailLruNodes.Remove(threadId, out var node))
        {
            threadDetailLru.Remove(node);
        }
    }

    private void AdjustFolderUnreadCounts(IEnumerable<string> labelIds, int delta)
    {
        foreach (var labelId in labelIds.Distinct(StringComparer.Ordinal))
        {
            var folder = Folders.FirstOrDefault(item => item.Id == labelId);
            if (folder is null)
            {
                continue;
            }

            var index = Folders.IndexOf(folder);
            if (index >= 0)
            {
                Folders[index] = folder with { UnreadCount = Math.Max(0, folder.UnreadCount + delta) };
            }
        }
    }

    private async Task<IDisposable> AcquireAccountOperationAsync(
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeToken);
        linked.Token.ThrowIfCancellationRequested();
        if (operationGate is null)
        {
            return EmptyDisposable.Instance;
        }

        await operationGate.WaitAsync(linked.Token);
        return new SemaphoreLease(operationGate);
    }

    private void ResetCompose()
    {
        IsComposeOpen = false;
        ComposeMode = MailComposeMode.New;
        ComposeTo = string.Empty;
        ComposeCc = string.Empty;
        ComposeBcc = string.Empty;
        ComposeSubject = string.Empty;
        ComposeBody = string.Empty;
        ComposeDraftId = null;
        ComposeThreadId = null;
        composeInReplyTo = null;
        composeReferences = null;
        composeRfcMessageId = null;
        ComposeErrorText = string.Empty;
        ComposeAttachmentPaths.Clear();
        ComposeRetainedAttachments.Clear();
    }

    private async Task PrepareReplyAsync(bool replyAll)
    {
        var thread = SelectedThread;
        if (thread is null || !IsConnected)
        {
            return;
        }

        try
        {
            using var accountOperation = await AcquireAccountOperationAsync();
            var request = await gmail.CreateReplyRequestAsync(thread.Id, replyAll, lifetimeToken);
            ApplyComposeRequest(request, replyAll ? MailComposeMode.ReplyAll : MailComposeMode.Reply);
        }
        catch (Exception ex)
        {
            SyncStatusText = "Could not prepare reply";
            SyncStatusDetail = UserFacingError(ex);
            SyncGlyph = "\uEA39";
        }
    }

    private void ApplyComposeRequest(ComposeMailRequest request, MailComposeMode mode)
    {
        ResetCompose();
        ComposeMode = mode;
        ComposeTo = request.To;
        ComposeCc = request.Cc;
        ComposeBcc = request.Bcc;
        ComposeSubject = request.Subject;
        ComposeBody = ApplyDefaultSignature(request.BodyText, mode);
        ComposeThreadId = request.ThreadId;
        composeInReplyTo = request.InReplyTo;
        composeReferences = request.References;
        composeRfcMessageId = request.RfcMessageId;
        foreach (var attachment in request.Attachments)
        {
            ComposeRetainedAttachments.Add(attachment);
        }
        IsComposeOpen = true;
    }

    private string ReplyAllRecipients(MailMessage message)
    {
        var recipients = new List<MimeKit.MailboxAddress>();
        foreach (var value in new[] { message.To, message.Cc })
        {
            if (MimeKit.InternetAddressList.TryParse(value, out var parsed))
            {
                recipients.AddRange(parsed.Mailboxes);
            }
        }

        var senderAddress = ParseFirstAddress(message.From);
        return string.Join(", ", recipients
            .Where(address => !string.Equals(address.Address, AccountEmail, StringComparison.OrdinalIgnoreCase))
            .Where(address => !string.Equals(address.Address, senderAddress, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(address => address.Address, StringComparer.OrdinalIgnoreCase)
            .Select(address => address.ToString()));
    }

    private static string ParseFirstAddress(string value) =>
        MimeKit.InternetAddressList.TryParse(value, out var parsed)
            ? parsed.Mailboxes.FirstOrDefault()?.Address ?? value
            : value;

    private static string PrefixSubject(string subject, string prefix) =>
        subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? subject
            : $"{prefix} {subject}";

    private static string QuoteMessage(MailMessage message)
    {
        var quoted = string.Join(Environment.NewLine, message.BodyText
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => $"> {line}"));
        return $"{Environment.NewLine}{Environment.NewLine}On {message.SentAt.ToLocalTime():g}, {message.From} wrote:{Environment.NewLine}{quoted}";
    }

    private static string ForwardMessage(MailMessage message) =>
        $"{Environment.NewLine}{Environment.NewLine}---------- Forwarded message ----------{Environment.NewLine}" +
        $"From: {message.From}{Environment.NewLine}" +
        $"Date: {message.SentAt.ToLocalTime():f}{Environment.NewLine}" +
        $"Subject: {message.Subject}{Environment.NewLine}" +
        $"To: {message.To}{Environment.NewLine}{Environment.NewLine}" +
        message.BodyText;

    private string ApplyDefaultSignature(string body, MailComposeMode mode)
    {
        var signatureEmail = mode switch
        {
            MailComposeMode.New => NewSignatureEmail,
            MailComposeMode.Forward => ForwardSignatureEmail,
            _ => ReplySignatureEmail
        };
        var signature = Signatures.FirstOrDefault(item =>
            string.Equals(item.Id, signatureEmail, StringComparison.Ordinal));
        if (signature is null || string.IsNullOrWhiteSpace(signature.Text))
        {
            return body;
        }

        return string.IsNullOrWhiteSpace(body)
            ? $"{Environment.NewLine}{Environment.NewLine}{signature.Text}"
            : $"{Environment.NewLine}{Environment.NewLine}{signature.Text}{Environment.NewLine}{Environment.NewLine}{body.TrimStart()}";
    }

    private async Task LoadCachedSignatureSettingsAsync()
    {
        if (signatureStore is null || string.IsNullOrWhiteSpace(AccountEmail))
        {
            return;
        }

        var snapshot = await signatureStore.LoadAsync(AccountEmail);
        if (snapshot is null)
        {
            return;
        }

        Signatures.Clear();
        foreach (var signature in snapshot.Signatures)
        {
            Signatures.Add(signature);
        }

        NewSignatureEmail = snapshot.NewSignatureEmail;
        ReplySignatureEmail = snapshot.ReplySignatureEmail;
        ForwardSignatureEmail = snapshot.ForwardSignatureEmail;
    }

    private Task PersistSignatureSettingsAsync()
    {
        if (signatureStore is null || string.IsNullOrWhiteSpace(AccountEmail))
        {
            return Task.CompletedTask;
        }

        return signatureStore.SaveAsync(AccountEmail, new SignatureSettingsSnapshot(
            Signatures.ToList(),
            NewSignatureEmail,
            ReplySignatureEmail,
            ForwardSignatureEmail));
    }

    private string? NormalizeSignatureChoice(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : ValidSignatureChoice(email);

    private string? ValidSignatureChoice(string? signatureId) =>
        string.IsNullOrWhiteSpace(signatureId)
            ? null
            : Signatures.Any(item => string.Equals(item.Id, signatureId, StringComparison.Ordinal)
                || string.Equals(item.EmailAddress, signatureId, StringComparison.OrdinalIgnoreCase))
                ? Signatures.First(item => string.Equals(item.Id, signatureId, StringComparison.Ordinal)
                    || string.Equals(item.EmailAddress, signatureId, StringComparison.OrdinalIgnoreCase)).Id
                : null;

    private static string? BuildReferences(MailMessage? message)
    {
        if (message?.RfcMessageId is null)
        {
            return message?.References;
        }

        return string.IsNullOrWhiteSpace(message.References)
            ? message.RfcMessageId
            : $"{message.References} {message.RfcMessageId}";
    }

    private async Task RefreshFoldersOnlyAsync(string selectedId)
    {
        var folders = await gmail.GetFoldersAsync();
        await localStore.SaveFoldersAsync(folders);
        ReplaceFolders(folders, selectedId);
        folderListRefreshedAt = DateTimeOffset.UtcNow;
    }

    private void IncrementFolderThreadCount(string folderId)
    {
        var folder = Folders.FirstOrDefault(item => item.Id == folderId);
        if (folder is null)
        {
            return;
        }

        var index = Folders.IndexOf(folder);
        if (index >= 0)
        {
            Folders[index] = folder with { ThreadCount = folder.ThreadCount + 1 };
        }
    }

    private CancellationTokenSource BeginRefreshOperation(CancellationToken cancellationToken)
    {
        refreshCancellation?.Cancel();
        refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        return refreshCancellation;
    }

    private void CompleteRefreshOperation(CancellationTokenSource operationCancellation)
    {
        if (ReferenceEquals(refreshCancellation, operationCancellation))
        {
            refreshCancellation = null;
            IsBusy = false;
        }

        operationCancellation.Dispose();
    }

    private async Task LoadCachedThreadsAsync()
    {
        var cached = await localStore.LoadThreadsAsync(SelectedFolder?.Id);
        if (SelectedFolder?.Id is { } selectedFolderId
            && partialFolderWindowStarts.TryGetValue(selectedFolderId, out var verifiedWindowStart))
        {
            cached = cached
                .Where(thread => thread.ReceivedAt >= verifiedWindowStart)
                .ToArray();
        }
        ReplaceThreads(cached);
    }

    private void ReplaceFolders(IEnumerable<MailFolder> folders, string? selectedId)
    {
        Folders.Clear();
        foreach (var folder in folders)
        {
            Folders.Add(folder);
        }

        SelectedFolder = Folders.FirstOrDefault(folder => folder.Id == selectedId)
            ?? Folders.FirstOrDefault(folder => folder.Id == "INBOX")
            ?? Folders.FirstOrDefault();
    }

    private void ReplaceThreads(IEnumerable<MailThreadSummary> threads)
    {
        Threads.Clear();
        foreach (var thread in threads)
        {
            Threads.Add(thread);
        }

        SelectedThread = null;
        SelectedThreadDetail = null;
    }

    private void ApplyProfile(GmailAccountProfile profile)
    {
        if (!localStore.HasAccountScope)
        {
            localStore.SetAccountScope(profile.EmailAddress);
            hydratedFolderIds.Clear();
            folderListRefreshedAt = DateTimeOffset.MinValue;
        }
        IsConnected = true;
        AccountEmail = profile.EmailAddress;
        SyncStatusText = "Connected to Gmail";
        SyncStatusDetail = "Preparing your local mirror.";
        SyncGlyph = "\uE73E";
    }

    private void SetDisconnectedStatus(bool hasLocalMirror = false)
    {
        IsConnected = false;
        AccountEmail = hasPreBoundAccountScope ? "Reconnect required" : "No account connected";
        SyncStatusText = hasLocalMirror ? "Showing saved mail" : "Ready to connect";
        SyncStatusDetail = hasLocalMirror
            ? "Your local mailbox mirror is available. Reauthorize Gmail to retrieve only newer changes."
            : "Connect your Google account to begin.";
        SyncGlyph = hasLocalMirror ? "\uE72E" : "\uE895";
    }

    private static string UserFacingError(Exception exception) => exception switch
    {
        InvalidDataException => exception.Message,
        ArgumentException => exception.Message,
        _ => exception.GetBaseException().Message
    };

    private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? heldSemaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref heldSemaphore, null)?.Release();
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
