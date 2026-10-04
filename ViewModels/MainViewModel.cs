using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GLook.Models;
using GLook.Services;

namespace GLook.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int BulkSyncThreadsPerFolder = 10;
    private readonly GmailClientService gmail;
    private readonly LocalMailStore localStore;
    private readonly SignatureSettingsStore? signatureStore;
    private CancellationTokenSource? refreshCancellation;
    private readonly Dictionary<string, DateTimeOffset> folderSyncTimes = new(StringComparer.Ordinal);
    private string? composeInReplyTo;
    private string? composeReferences;
    private string? composeRfcMessageId;

    public MainViewModel(
        GmailClientService gmail,
        LocalMailStore localStore,
        SignatureSettingsStore? signatureStore = null)
    {
        this.gmail = gmail;
        this.localStore = localStore;
        this.signatureStore = signatureStore;
    }

    public ObservableCollection<MailFolder> Folders { get; } = [];

    public ObservableCollection<MailThreadSummary> Threads { get; } = [];

    public ObservableCollection<MailSignature> Signatures { get; } = [];

    public ObservableCollection<string> ComposeAttachmentPaths { get; } = [];

    public ObservableCollection<MailAttachmentInput> ComposeRetainedAttachments { get; } = [];

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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        SyncStatusText = "Checking your Gmail mirror";
        SyncStatusDetail = "Looking for a securely stored account session.";
        try
        {
            await localStore.InitializeAsync();
            var profile = await gmail.TryReconnectAsync(cancellationToken);
            if (profile is null)
            {
                localStore.SetAccountScope(null);
                Threads.Clear();
                SetDisconnectedStatus();
                return;
            }

            ApplyProfile(profile);
            await LoadCachedSignatureSettingsAsync();
            await LoadCachedThreadsAsync();
            await RefreshAsync(cancellationToken);
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
            var profile = await gmail.ConnectAsync();
            ApplyProfile(profile);
            await LoadCachedSignatureSettingsAsync();
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

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            await LoadCachedThreadsAsync();
            return;
        }

        var operationCancellation = BeginRefreshOperation(cancellationToken);
        cancellationToken = operationCancellation.Token;

        IsBusy = true;
        IsSyncingAll = false;
        SyncProgressValue = 0;
        SyncStatusText = "Syncing with Gmail";
        SyncStatusDetail = $"Fetching labels and recent conversations for {SelectedFolder?.Name ?? "Inbox"}.";
        SyncGlyph = "\uE895";
        try
        {
            var selectedId = SelectedFolder?.Id ?? "INBOX";
            var foldersTask = gmail.GetFoldersAsync(cancellationToken);
            var threadsTask = gmail.GetThreadsAsync(selectedId, SearchQuery, cancellationToken: cancellationToken);
            await Task.WhenAll(foldersTask, threadsTask);

            ReplaceFolders(await foldersTask, selectedId);
            var threads = await threadsTask;
            await localStore.SaveFolderSnapshotAsync(
                selectedId,
                threads,
                // A Gmail list response is capped, so rows outside this window
                // cannot safely be treated as removed from the label.
                reconcileMissing: false);
            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                folderSyncTimes[selectedId] = DateTimeOffset.UtcNow;
            }

            ReplaceThreads(threads);
            SyncStatusText = "Gmail mirror is current";
            SyncStatusDetail = $"Synced {threads.Count} recent conversations just now.";
            SyncGlyph = "\uE73E";
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

        var operationCancellation = BeginRefreshOperation(cancellationToken);
        cancellationToken = operationCancellation.Token;

        IsBusy = true;
        IsSyncingAll = true;
        SyncProgressValue = 0;
        SyncStatusText = "Preparing to sync all folders";
        SyncStatusDetail = "Refreshing the Gmail folder list.";
        SyncGlyph = "\uE895";
        var completedFolderCount = 0;
        var totalFolderCount = 0;

        try
        {
            var selectedId = SelectedFolder?.Id ?? "INBOX";
            var folders = await gmail.GetFoldersAsync(cancellationToken);
            ReplaceFolders(folders, selectedId);
            selectedId = SelectedFolder?.Id ?? "INBOX";

            var folderList = folders.ToList();
            totalFolderCount = folderList.Count;
            var distinctThreadIds = new HashSet<string>(StringComparer.Ordinal);
            var sharedSummaryCache = new Dictionary<string, MailThreadSummary>(StringComparer.Ordinal);
            Action<string> quotaStatus = message => SyncStatusDetail = message;
            if (string.IsNullOrWhiteSpace(SearchQuery)
                && folderSyncTimes.TryGetValue(selectedId, out var selectedFolderSyncedAt)
                && selectedFolderSyncedAt >= DateTimeOffset.UtcNow.AddMinutes(-1))
            {
                foreach (var thread in Threads)
                {
                    sharedSummaryCache[thread.Id] = thread;
                }
            }

            for (var index = 0; index < folderList.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = folderList[index];
                SyncStatusText = $"Syncing all folders ({index + 1} of {folderList.Count})";
                SyncStatusDetail = $"Fetching recent conversations from {folder.FullName}.";

                var threads = await gmail.GetThreadsForBulkSyncAsync(
                    folder.Id,
                    sharedSummaryCache,
                    quotaStatus,
                    maxResults: BulkSyncThreadsPerFolder,
                    cancellationToken: cancellationToken);
                await localStore.SaveFolderSnapshotAsync(
                    folder.Id,
                    threads,
                    reconcileMissing: false);
                folderSyncTimes[folder.Id] = DateTimeOffset.UtcNow;
                distinctThreadIds.UnionWith(threads.Select(thread => thread.Id));

                completedFolderCount = index + 1;
                SyncProgressValue = completedFolderCount * 100d / folderList.Count;
            }

            SyncStatusText = "Finishing selected folder";
            SyncStatusDetail = $"Refreshing {SelectedFolder?.FullName ?? "Inbox"} at normal depth.";
            var selectedFolderThreads = await gmail.GetThreadsForBulkSyncAsync(
                selectedId,
                sharedSummaryCache,
                quotaStatus,
                query: SearchQuery,
                maxResults: 40,
                cancellationToken: cancellationToken);
            await localStore.SaveFolderSnapshotAsync(
                selectedId,
                selectedFolderThreads,
                reconcileMissing: false);
            distinctThreadIds.UnionWith(selectedFolderThreads.Select(thread => thread.Id));
            folderSyncTimes[selectedId] = DateTimeOffset.UtcNow;
            ReplaceThreads(selectedFolderThreads);

            SyncStatusText = "Recent mail synced across all folders";
            SyncStatusDetail = $"Updated {folderList.Count} folders and {distinctThreadIds.Count} recent conversations.";
            SyncGlyph = "\uE73E";
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (ReferenceEquals(refreshCancellation, operationCancellation))
            {
                SyncStatusText = "Sync all canceled";
                SyncStatusDetail = $"Updated {completedFolderCount} of {totalFolderCount} folders before stopping.";
                SyncGlyph = "\uE711";
            }

            return false;
        }
        catch (Exception ex)
        {
            SyncStatusText = "Sync all stopped";
            SyncStatusDetail = $"Some folders may not be current. {UserFacingError(ex)}";
            SyncGlyph = "\uE774";
            await LoadCachedThreadsAsync();
            return false;
        }
        finally
        {
            if (ReferenceEquals(refreshCancellation, operationCancellation))
            {
                IsSyncingAll = false;
            }

            CompleteRefreshOperation(operationCancellation);
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
        SelectedThread = thread;
        SelectedThreadDetail = null;
        if (thread is null || !IsConnected)
        {
            return;
        }

        IsBusy = true;
        try
        {
            SelectedThreadDetail = await gmail.GetThreadAsync(thread.Id);
            if (thread.IsUnread)
            {
                await gmail.SetThreadReadAsync(thread.Id, true);
                var index = Threads.IndexOf(thread);
                if (index >= 0)
                {
                    var updated = thread with { IsUnread = false };
                    Threads[index] = updated;
                    SelectedThread = updated;
                }
            }
        }
        catch (Exception ex)
        {
            SyncStatusText = "Could not open conversation";
            SyncStatusDetail = UserFacingError(ex);
            SyncGlyph = "\uEA39";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SearchAsync()
    {
        await RefreshAsync();
    }

    public Task<IReadOnlyList<MailThreadSummary>> GetUnreadInboxSampleAsync(
        CancellationToken cancellationToken = default) =>
        !IsConnected
            ? Task.FromResult<IReadOnlyList<MailThreadSummary>>([])
            : gmail.GetThreadsAsync("INBOX", "is:unread", 10, cancellationToken);

    public Task TrashSelectedThreadAsync() => TrashThreadsAsync(CurrentThreadSelection());

    public Task TrashThreadsAsync(IEnumerable<MailThreadSummary> threads) =>
        RunThreadMutationAsync(
            threads,
            "Moving conversations to Trash",
            (thread, cancellationToken) => gmail.TrashThreadAsync(thread.Id, cancellationToken),
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
        ComposeTo = message.From;
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
        ComposeTo = message.From;
        ComposeCc = ReplyAllRecipients(message);
        ComposeSubject = PrefixSubject(message.Subject, "Re:");
        ComposeBody = ApplyDefaultSignature(QuoteMessage(message), MailComposeMode.ReplyAll);
        IsComposeOpen = true;
    }

    public Task StartReplyAsync() => PrepareReplyAsync(replyAll: false);

    public Task StartReplyAllAsync() => PrepareReplyAsync(replyAll: true);

    public async Task StartForwardAsync()
    {
        var thread = SelectedThread;
        if (thread is null || !IsConnected)
        {
            return;
        }

        try
        {
            ApplyComposeRequest(await gmail.CreateForwardRequestAsync(thread.Id), MailComposeMode.Forward);
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
            var localSignatures = Signatures
                .Where(signature => !signature.IsGmailBacked)
                .ToList();
            var signatures = await gmail.GetSignaturesAsync();
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

                saved.Add(await gmail.UpdateSignatureAsync(original, edited.Text));
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
            var savedDraftId = ComposeDraftId;
            if (string.IsNullOrWhiteSpace(savedDraftId))
            {
                await gmail.SendMessageAsync(await CreateComposeRequestAsync());
            }
            else
            {
                await gmail.SendDraftAsync(savedDraftId, await CreateComposeRequestAsync());
            }
            messageSent = true;
            ResetCompose();

            SyncStatusText = "Message sent";
            SyncStatusDetail = "Gmail accepted the message for delivery.";
            SyncGlyph = "\uE73E";
            await RefreshAfterMutationAsync(SelectedThread?.Id);
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
            var result = await gmail.SaveDraftAsync(await CreateComposeRequestAsync(), ComposeDraftId);
            draftSaved = true;
            ComposeDraftId = result.DraftId;
            SyncStatusText = "Draft saved";
            SyncStatusDetail = "The draft is available in Gmail.";
            SyncGlyph = "\uE73E";
            await RefreshFoldersOnlyAsync(SelectedFolder?.Id ?? "DRAFT");
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
                await gmail.DeleteDraftAsync(ComposeDraftId);
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
            summary => summary with { IsUnread = !isRead });

    public Task ToggleSelectedThreadStarAsync() =>
        SetSelectedThreadStarredAsync(!(SelectedThread?.IsStarred ?? true));

    public Task SetSelectedThreadStarredAsync(bool isStarred) =>
        SetThreadsStarredAsync(CurrentThreadSelection(), isStarred);

    public Task SetThreadsStarredAsync(IEnumerable<MailThreadSummary> threads, bool isStarred) =>
        RunThreadMutationAsync(
            threads,
            isStarred ? "Starring conversations" : "Removing stars",
            (thread, cancellationToken) => gmail.SetThreadStarredAsync(thread.Id, isStarred, cancellationToken),
            summary => summary with { IsStarred = isStarred });

    public Task MarkSelectedThreadJunkAsync(bool isJunk = true) =>
        MarkThreadsJunkAsync(CurrentThreadSelection(), isJunk);

    public Task MarkThreadsJunkAsync(IEnumerable<MailThreadSummary> threads, bool isJunk = true) =>
        RunThreadMutationAsync(
            threads,
            isJunk ? "Moving conversations to Spam" : "Removing conversations from Spam",
            (thread, cancellationToken) => gmail.SetThreadSpamAsync(thread.Id, isJunk, cancellationToken),
            removeFromCurrentFolder: true);

    public Task MarkSelectedThreadNotJunkAsync() => MarkSelectedThreadJunkAsync(isJunk: false);

    public Task RestoreSelectedThreadFromTrashAsync() => RestoreThreadsFromTrashAsync(CurrentThreadSelection());

    public Task RestoreThreadsFromTrashAsync(IEnumerable<MailThreadSummary> threads) =>
        RunThreadMutationAsync(
            threads,
            "Restoring conversations from Trash",
            (thread, cancellationToken) => gmail.RestoreThreadFromTrashAsync(thread.Id, cancellationToken),
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

    public Task MoveThreadsAsync(IEnumerable<MailThreadSummary> threads, MailFolder targetFolder) =>
        RunThreadMutationAsync(
            threads,
            $"Moving conversation to {targetFolder.FullName}",
            (thread, cancellationToken) => gmail.MoveThreadAsync(
                thread.Id,
                SelectedFolder is { CanDelete: true } || SelectedFolder?.Id == "INBOX"
                    ? SelectedFolder.Id
                    : null,
                targetFolder.Id,
                cancellationToken),
            removeFromCurrentFolder: SelectedFolder?.Id != targetFolder.Id);

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
            var folder = await gmail.CreateFolderAsync(folderPath);
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
            await gmail.DeleteFolderAsync(folder.Id);
            Folders.Remove(folder);
            if (SelectedFolder?.Id == folder.Id)
            {
                SelectedFolder = Folders.FirstOrDefault(item => item.Id == "INBOX") ?? Folders.FirstOrDefault();
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

    public Task<int> GetFolderThreadCountAsync(
        MailFolder folder,
        CancellationToken cancellationToken = default) =>
        !folder.CanDelete || !IsConnected
            ? Task.FromResult(0)
            : gmail.GetFolderThreadCountAsync(folder.Id, cancellationToken);

    public Task<int> GetTrashMessageCountAsync(CancellationToken cancellationToken = default) =>
        !IsConnected
            ? Task.FromResult(0)
            : gmail.GetFolderMessageCountAsync("TRASH", cancellationToken);

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
            var result = await gmail.EmptyTrashAsync(
                (completed, total) =>
                {
                    SyncStatusText = total == 0
                        ? "Trash is already empty"
                        : $"Permanently deleting Trash ({completed}/{total})";
                    SyncStatusDetail = total == 0
                        ? "Gmail reported no messages in Trash."
                        : "Deleted messages cannot be recovered.";
                },
                CancellationToken.None);

            await ReconcileTrashCacheAsync(clearOnFetchFailure: result.RemainingCount == 0);
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
            await ReconcileTrashCacheAsync(clearOnFetchFailure: true);
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

    private async Task ReconcileTrashCacheAsync(bool clearOnFetchFailure)
    {
        IReadOnlyList<MailThreadSummary> currentTrash = [];
        try
        {
            using var reconciliationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            currentTrash = await gmail.GetThreadsAsync(
                "TRASH",
                query: null,
                cancellationToken: reconciliationCancellation.Token);
        }
        catch
        {
            // Trash is a disposable mirror. Clearing stale membership is safer
            // than presenting messages that may already be permanently gone.
            if (!clearOnFetchFailure)
            {
                return;
            }
        }

        try
        {
            await localStore.SaveFolderSnapshotAsync("TRASH", currentTrash, reconcileMissing: true);
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
            var threadIds = await gmail.TrashFolderThreadsAsync(
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
                await localStore.RemoveThreadAsync(threadId);
            }

            Folders.Remove(folder);
            if (SelectedFolder?.Id == folder.Id)
            {
                SelectedFolder = Folders.FirstOrDefault(item => item.Id == "INBOX") ?? Folders.FirstOrDefault();
            }

            SelectedThread = null;
            SelectedThreadDetail = null;
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
            await gmail.RenameFolderTreeAsync(folder.FullName, newFullName);
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
        await localStore.ClearCurrentAccountAsync();
        localStore.SetAccountScope(null);
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
        IsBusy = true;
        SyncStatusText = progressText;
        SyncStatusDetail = $"Applying this change directly to Gmail for {threads.Count} conversation{(threads.Count == 1 ? string.Empty : "s")}.";
        SyncGlyph = "\uE895";
        var mutationApplied = false;
        try
        {
            foreach (var thread in threads)
            {
                await mutation(thread, cancellation.Token);
                mutationApplied = true;
            }

            var preferredThreadId = !removeFromCurrentFolder && threads.Count == 1
                ? threads[0].Id
                : null;
            if (removeFromCurrentFolder)
            {
                foreach (var thread in threads)
                {
                    await localStore.RemoveThreadAsync(thread.Id);
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

                    var updated = updateSummary(current);
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

            await RefreshAfterMutationAsync(preferredThreadId, cancellation.Token);
            SyncStatusText = threads.Count == 1 ? "Change synced to Gmail" : "Changes synced to Gmail";
            SyncStatusDetail = $"Updated {threads.Count} conversation{(threads.Count == 1 ? string.Empty : "s")}; folder and conversation views are current.";
            SyncGlyph = "\uE73E";
        }
        catch (Exception ex)
        {
            if (mutationApplied)
            {
                try
                {
                    await RefreshAfterMutationAsync(cancellationToken: CancellationToken.None);
                }
                catch
                {
                    // Preserve the original mutation failure while leaving the next sync to reconcile the view.
                }
            }

            SyncStatusText = mutationApplied ? "Some changes may have synced" : "Change did not sync";
            SyncStatusDetail = mutationApplied
                ? $"Gmail accepted at least one change before the operation stopped. {UserFacingError(ex)}"
                : UserFacingError(ex);
            SyncGlyph = mutationApplied ? "\uE774" : "\uEA39";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAfterMutationAsync(
        string? preferredThreadId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return;
        }

        var selectedFolderId = SelectedFolder?.Id ?? "INBOX";
        var foldersTask = gmail.GetFoldersAsync(cancellationToken);
        var threadsTask = gmail.GetThreadsAsync(
            selectedFolderId,
            SearchQuery,
            cancellationToken: cancellationToken);
        await Task.WhenAll(foldersTask, threadsTask);

        ReplaceFolders(await foldersTask, selectedFolderId);
        var threads = await threadsTask;
        await localStore.SaveFolderSnapshotAsync(
            selectedFolderId,
            threads,
            reconcileMissing: false);
        Threads.Clear();
        foreach (var item in threads)
        {
            Threads.Add(item);
        }

        if (string.IsNullOrWhiteSpace(preferredThreadId))
        {
            SelectedThread = null;
            SelectedThreadDetail = null;
            return;
        }

        SelectedThread = Threads.FirstOrDefault(item => item.Id == preferredThreadId);
        SelectedThreadDetail = SelectedThread is null
            ? null
            : await gmail.GetThreadAsync(preferredThreadId, cancellationToken);
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
            var request = await gmail.CreateReplyRequestAsync(thread.Id, replyAll);
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
        ReplaceFolders(folders, selectedId);
    }

    private CancellationTokenSource BeginRefreshOperation(CancellationToken cancellationToken)
    {
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return refreshCancellation;
    }

    private void CompleteRefreshOperation(CancellationTokenSource operationCancellation)
    {
        if (!ReferenceEquals(refreshCancellation, operationCancellation))
        {
            return;
        }

        refreshCancellation = null;
        operationCancellation.Dispose();
        IsBusy = false;
    }

    private async Task LoadCachedThreadsAsync()
    {
        var cached = await localStore.LoadThreadsAsync(SelectedFolder?.Id);
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
        localStore.SetAccountScope(profile.EmailAddress);
        IsConnected = true;
        AccountEmail = profile.EmailAddress;
        SyncStatusText = "Connected to Gmail";
        SyncStatusDetail = "Preparing your local mirror.";
        SyncGlyph = "\uE73E";
    }

    private void SetDisconnectedStatus()
    {
        IsConnected = false;
        AccountEmail = "No account connected";
        SyncStatusText = "Ready to connect";
        SyncStatusDetail = "Connect your Google account to begin.";
        SyncGlyph = "\uE895";
    }

    private static string UserFacingError(Exception exception) => exception switch
    {
        InvalidDataException => exception.Message,
        ArgumentException => exception.Message,
        _ => exception.GetBaseException().Message
    };
}
