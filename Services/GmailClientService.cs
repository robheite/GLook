using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Google;
using GLook.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using MimeKit;
using MimeKit.Utils;

namespace GLook.Services;

public sealed class GmailClientService
{
    private const string UserId = "me";
    private const string CredentialKey = "primary-account";
    private const string SettingsCredentialKey = "signature-settings";
    private const string ClientSecretsKey = "oauth-client-secrets";
    private const string EmbeddedClientResourceName = "GLook.GoogleOAuthClient";
    private const string BodySizeOmissionMarker = "[Message body omitted because it exceeds GLook's 8 MB display limit.]";
    private const string MimeStructureOmissionMarker = "[Message body omitted because its MIME structure exceeds GLook's safety limits.]";
    private static readonly string[] Scopes = [GmailService.Scope.GmailModify];
    private static readonly string[] SettingsScopes = [GmailService.Scope.GmailSettingsBasic];
    private readonly EncryptedDataStore secureStore;
    private readonly SemaphoreSlim labelCountRefreshGate = new(1, 1);
    private Dictionary<string, LabelUnreadCounts> labelUnreadCounts = new(StringComparer.Ordinal);
    private DateTimeOffset labelUnreadCountsExpiresAt = DateTimeOffset.MinValue;
    private GmailService? gmail;
    private GmailService? gmailSettings;
    private string? connectedEmailAddress;

    public GmailClientService(EncryptedDataStore secureStore)
    {
        this.secureStore = secureStore;
    }

    public bool IsConnected => gmail is not null;

    public async Task<GmailAccountProfile?> TryReconnectAsync(CancellationToken cancellationToken = default)
    {
        var clientJson = LoadPackagedClientSecrets()
            ?? await secureStore.GetAsync<string>(ClientSecretsKey);
        return string.IsNullOrWhiteSpace(clientJson)
            ? null
            : await ConnectAsync(clientJson, false, cancellationToken);
    }

    public Task<GmailAccountProfile> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var clientJson = LoadPackagedClientSecrets()
            ?? throw new InvalidOperationException("This build does not contain a Google desktop OAuth client configuration.");
        return ConnectAsync(clientJson, false, cancellationToken);
    }

    public async Task<GmailAccountProfile> ConnectAsync(
        string clientSecretsJson,
        bool rememberClient = true,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(clientSecretsJson));
        var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
        if (string.IsNullOrWhiteSpace(secrets.ClientId) || string.IsNullOrWhiteSpace(secrets.ClientSecret))
        {
            throw new InvalidDataException("The selected file is not a valid Google desktop OAuth client file.");
        }

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            Scopes,
            CredentialKey,
            cancellationToken,
            secureStore);

        gmail?.Dispose();
        gmail = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "GLook"
        });

        if (rememberClient)
        {
            await secureStore.StoreAsync(ClientSecretsKey, clientSecretsJson);
        }

        var profile = await gmail.Users.GetProfile(UserId).ExecuteAsync(cancellationToken);
        connectedEmailAddress = profile.EmailAddress;
        return new GmailAccountProfile(profile.EmailAddress, profile.HistoryId ?? 0);
    }

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        var service = RequireClient();
        var response = await service.Users.Labels.List(UserId).ExecuteAsync(cancellationToken);
        var labels = response.Labels ?? [];
        await ApplyUnreadCountsAsync(service, labels, cancellationToken);

        var folders = labels
            .Where(label => ShouldShowLabel(label))
            .Select(ToFolder)
            .OrderBy(folder => FolderOrder(folder.Id))
            .ThenBy(folder => folder.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return folders;
    }

    public async Task<IReadOnlyList<MailThreadSummary>> GetThreadsAsync(
        string? labelId,
        string? query,
        int maxResults = 40,
        CancellationToken cancellationToken = default)
    {
        var service = RequireClient();
        var list = service.Users.Threads.List(UserId);
        list.MaxResults = maxResults;
        list.Q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        list.IncludeSpamTrash = labelId is "SPAM" or "TRASH"
            || list.Q?.Contains("in:spam", StringComparison.OrdinalIgnoreCase) == true
            || list.Q?.Contains("in:trash", StringComparison.OrdinalIgnoreCase) == true;
        if (!string.IsNullOrWhiteSpace(labelId))
        {
            list.LabelIds = new[] { labelId };
        }

        var response = await list.ExecuteAsync(cancellationToken);
        if (response.Threads is null || response.Threads.Count == 0)
        {
            return [];
        }

        using var throttle = new SemaphoreSlim(6);
        var tasks = response.Threads.Select(async item =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var request = service.Users.Threads.Get(UserId, item.Id);
                request.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Metadata;
                request.MetadataHeaders = new[] { "From", "Subject", "Date" };
                var thread = await request.ExecuteAsync(cancellationToken);
                return ToSummary(thread);
            }
            finally
            {
                throttle.Release();
            }
        });

        var threads = await Task.WhenAll(tasks);
        return threads.OrderByDescending(thread => thread.ReceivedAt).ToList();
    }

    public async Task<IReadOnlyList<MailThreadSummary>> GetThreadsForBulkSyncAsync(
        string labelId,
        IDictionary<string, MailThreadSummary> sharedSummaryCache,
        Action<string>? status = null,
        string? query = null,
        int maxResults = 40,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelId);
        ArgumentNullException.ThrowIfNull(sharedSummaryCache);

        var service = RequireClient();
        var list = service.Users.Threads.List(UserId);
        list.MaxResults = maxResults;
        list.Q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        list.IncludeSpamTrash = labelId is "SPAM" or "TRASH"
            || list.Q?.Contains("in:spam", StringComparison.OrdinalIgnoreCase) == true
            || list.Q?.Contains("in:trash", StringComparison.OrdinalIgnoreCase) == true;
        list.LabelIds = new[] { labelId };

        var response = await ExecuteWithQuotaRetryAsync(
            () => list.ExecuteAsync(cancellationToken),
            status,
            cancellationToken);
        if (response.Threads is null || response.Threads.Count == 0)
        {
            return [];
        }

        var threads = new List<MailThreadSummary>(response.Threads.Count);
        foreach (var item in response.Threads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sharedSummaryCache.TryGetValue(item.Id, out var cached))
            {
                threads.Add(cached);
                continue;
            }

            var request = service.Users.Threads.Get(UserId, item.Id);
            request.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Metadata;
            request.MetadataHeaders = new[] { "From", "Subject", "Date" };
            var thread = await ExecuteWithQuotaRetryAsync(
                () => request.ExecuteAsync(cancellationToken),
                status,
                cancellationToken);
            var summary = ToSummary(thread);
            sharedSummaryCache[summary.Id] = summary;
            threads.Add(summary);

            // A metadata thread read costs 40 Gmail quota units. Pacing bulk reads
            // keeps a full-folder sync below the per-user rolling minute limit.
            await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken);
        }

        return threads.OrderByDescending(thread => thread.ReceivedAt).ToList();
    }

    public async Task<MailThreadDetail> GetThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var service = RequireClient();
        var request = service.Users.Threads.Get(UserId, threadId);
        request.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Metadata;
        request.MetadataHeaders = new[] { "From", "To", "Cc", "Subject", "Date", "Message-Id", "References" };
        var thread = await request.ExecuteAsync(cancellationToken);
        var orderedMessages = (thread.Messages ?? [])
            .OrderBy(message => message.InternalDate ?? 0)
            .ToList();
        var selectedMessages = orderedMessages.TakeLast(MailResourceLimits.MaxThreadMessages).ToList();
        var loadedMessages = new List<MailMessage>(selectedMessages.Count);
        long totalBodyBytes = 0;
        foreach (var summary in selectedMessages.AsEnumerable().Reverse())
        {
            var messageRequest = service.Users.Messages.Get(UserId, summary.Id);
            messageRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
            var message = await messageRequest.ExecuteAsync(cancellationToken);
            var body = await ExtractBodyContentAsync(message, cancellationToken);
            var bodyBytes = Encoding.UTF8.GetByteCount(body.Text)
                + (body.Html is null ? 0 : Encoding.UTF8.GetByteCount(body.Html));
            if (bodyBytes > MailResourceLimits.MaxAggregateThreadBodyBytes - totalBodyBytes)
            {
                break;
            }

            totalBodyBytes += bodyBytes;
            loadedMessages.Add(new MailMessage(
                message.Id,
                Header(message, "From", "Unknown sender"),
                Header(message, "To", string.Empty),
                Header(message, "Subject", "(no subject)"),
                FromUnixMilliseconds(message.InternalDate),
                body.Text)
            {
                BodyHtml = body.Html,
                Cc = Header(message, "Cc", string.Empty),
                RfcMessageId = HeaderOrNull(message, "Message-Id"),
                References = HeaderOrNull(message, "References")
            });
        }

        loadedMessages.Reverse();
        var omittedMessageCount = Math.Max(0, orderedMessages.Count - loadedMessages.Count);
        var messages = new List<MailMessage>(loadedMessages.Count + (omittedMessageCount > 0 ? 1 : 0));
        if (omittedMessageCount > 0)
        {
            var omissionTimestamp = loadedMessages.FirstOrDefault()?.SentAt ?? DateTimeOffset.Now;
            messages.Add(new MailMessage(
                $"glook-omitted-{threadId}",
                "GLook",
                string.Empty,
                "Earlier messages omitted",
                omissionTimestamp,
                $"[{omittedMessageCount} earlier message{(omittedMessageCount == 1 ? string.Empty : "s")} omitted to keep this conversation responsive.]")
            {
                IsOmissionNotice = true
            });
        }

        messages.AddRange(loadedMessages);

        return new MailThreadDetail(
            thread.Id ?? threadId,
            messages.LastOrDefault()?.Subject ?? "(no subject)",
            messages);
    }

    public async Task TrashThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        await RequireClient().Users.Threads.Trash(UserId, threadId).ExecuteAsync(cancellationToken);
        InvalidateLabelUnreadCounts();
    }

    public async Task RestoreThreadFromTrashAsync(string threadId, CancellationToken cancellationToken = default)
    {
        await RequireClient().Users.Threads.Untrash(UserId, threadId).ExecuteAsync(cancellationToken);
        InvalidateLabelUnreadCounts();
    }

    public Task ArchiveThreadAsync(string threadId, CancellationToken cancellationToken = default) =>
        ModifyThreadLabelsAsync(threadId, removeLabelIds: ["INBOX"], cancellationToken: cancellationToken);

    public async Task SetThreadReadAsync(string threadId, bool isRead, CancellationToken cancellationToken = default)
    {
        var body = new ModifyThreadRequest
        {
            AddLabelIds = isRead ? null : ["UNREAD"],
            RemoveLabelIds = isRead ? ["UNREAD"] : null
        };
        await RequireClient().Users.Threads.Modify(body, UserId, threadId).ExecuteAsync(cancellationToken);
        InvalidateLabelUnreadCounts();
    }

    public Task SetThreadStarredAsync(string threadId, bool isStarred, CancellationToken cancellationToken = default) =>
        ModifyThreadLabelsAsync(
            threadId,
            addLabelIds: isStarred ? ["STARRED"] : null,
            removeLabelIds: isStarred ? null : ["STARRED"],
            cancellationToken);

    public Task SetThreadSpamAsync(string threadId, bool isSpam, CancellationToken cancellationToken = default) =>
        ModifyThreadLabelsAsync(
            threadId,
            addLabelIds: isSpam ? ["SPAM"] : ["INBOX"],
            removeLabelIds: isSpam ? ["INBOX"] : ["SPAM"],
            cancellationToken);

    public async Task ModifyThreadLabelsAsync(
        string threadId,
        IEnumerable<string>? addLabelIds = null,
        IEnumerable<string>? removeLabelIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var additions = NormalizeLabelIds(addLabelIds);
        var removals = NormalizeLabelIds(removeLabelIds)
            .Except(additions, StringComparer.Ordinal)
            .ToArray();
        if (additions.Length == 0 && removals.Length == 0)
        {
            return;
        }

        await RequireClient().Users.Threads.Modify(new ModifyThreadRequest
        {
            AddLabelIds = additions.Length == 0 ? null : additions,
            RemoveLabelIds = removals.Length == 0 ? null : removals
        }, UserId, threadId).ExecuteAsync(cancellationToken);
        InvalidateLabelUnreadCounts();
    }

    public Task AddLabelsToThreadAsync(
        string threadId,
        IEnumerable<string> labelIds,
        CancellationToken cancellationToken = default) =>
        ModifyThreadLabelsAsync(threadId, addLabelIds: labelIds, cancellationToken: cancellationToken);

    public Task RemoveLabelsFromThreadAsync(
        string threadId,
        IEnumerable<string> labelIds,
        CancellationToken cancellationToken = default) =>
        ModifyThreadLabelsAsync(threadId, removeLabelIds: labelIds, cancellationToken: cancellationToken);

    public Task MoveThreadAsync(
        string threadId,
        string? currentLabelId,
        string targetLabelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLabelId);
        if (!IsMoveFolderLabel(targetLabelId))
        {
            throw new InvalidOperationException("Move supports Inbox and user-created Gmail labels. Use the dedicated commands for Starred, Sent, Drafts, Spam, and Trash.");
        }

        var removableCurrentLabel = !string.IsNullOrWhiteSpace(currentLabelId)
            && IsMoveFolderLabel(currentLabelId)
            ? currentLabelId
            : null;
        var remove = string.IsNullOrWhiteSpace(removableCurrentLabel)
            || string.Equals(removableCurrentLabel, targetLabelId, StringComparison.Ordinal)
                ? null
                : new[] { removableCurrentLabel };
        return ModifyThreadLabelsAsync(threadId, [targetLabelId], remove, cancellationToken);
    }

    public async Task<MailSendResult> SendMessageAsync(
        ComposeMailRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = RequireClient();
        var message = await CreateApiMessageAsync(request, cancellationToken);
        var sent = await service.Users.Messages.Send(message, UserId).ExecuteAsync(cancellationToken);
        InvalidateLabelUnreadCounts();
        return new MailSendResult(
            sent.Id,
            sent.ThreadId,
            sent.LabelIds?.ToList() ?? []);
    }

    public async Task<MailSendResult> SendDraftAsync(
        string draftId,
        ComposeMailRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        ArgumentNullException.ThrowIfNull(request);
        var service = RequireClient();
        var sent = await service.Users.Drafts.Send(
                new Draft
                {
                    Id = draftId,
                    Message = await CreateApiMessageAsync(request, cancellationToken)
                },
                UserId)
            .ExecuteAsync(cancellationToken);
        InvalidateLabelUnreadCounts();
        return new MailSendResult(
            sent.Id,
            sent.ThreadId,
            sent.LabelIds?.ToList() ?? []);
    }

    public async Task<MailDraftResult> SaveDraftAsync(
        ComposeMailRequest request,
        string? draftId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = RequireClient();
        var draft = new Draft
        {
            Message = await CreateApiMessageAsync(request, cancellationToken, allowNoRecipients: true)
        };

        var saved = string.IsNullOrWhiteSpace(draftId)
            ? await service.Users.Drafts.Create(draft, UserId).ExecuteAsync(cancellationToken)
            : await service.Users.Drafts.Update(draft, UserId, draftId).ExecuteAsync(cancellationToken);
        InvalidateLabelUnreadCounts();
        return new MailDraftResult(saved.Id, saved.Message?.Id ?? string.Empty, saved.Message?.ThreadId);
    }

    public async Task DeleteDraftAsync(string draftId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        await RequireClient().Users.Drafts.Delete(UserId, draftId).ExecuteAsync(cancellationToken);
        InvalidateLabelUnreadCounts();
    }

    public async Task<MailDraftResult> GetDraftAsync(
        string draftId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        var draft = await RequireClient().Users.Drafts.Get(UserId, draftId).ExecuteAsync(cancellationToken);
        return new MailDraftResult(draft.Id, draft.Message?.Id ?? string.Empty, draft.Message?.ThreadId);
    }

    public async Task<ComposeMailRequest> CreateReplyRequestAsync(
        string threadId,
        bool replyAll = false,
        CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestRawMessageAsync(
            threadId,
            includeContent: false,
            cancellationToken: cancellationToken);
        var ownAddress = await GetConnectedEmailAddressAsync(cancellationToken);
        var replyTarget = Header(latest, "Reply-To", Header(latest, "From", string.Empty));
        var to = replyAll
            ? CombineRecipientsExcludingOwn(ownAddress, replyTarget, Header(latest, "To", string.Empty))
            : replyTarget;
        var cc = replyAll
            ? CombineRecipientsExcludingOwn(ownAddress, Header(latest, "Cc", string.Empty))
            : string.Empty;
        var messageId = HeaderOrNull(latest, "Message-Id");

        return new ComposeMailRequest
        {
            To = to,
            Cc = cc,
            Subject = EnsureSubjectPrefix(Header(latest, "Subject", string.Empty), "Re:"),
            ThreadId = threadId,
            InReplyTo = messageId,
            References = AppendReference(HeaderOrNull(latest, "References"), messageId)
        };
    }

    public async Task<ComposeMailRequest> CreateForwardRequestAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestRawMessageAsync(
            threadId,
            includeContent: true,
            cancellationToken: cancellationToken);
        var sentAt = Header(latest, "Date", string.Empty);
        var forwarded = string.Join(Environment.NewLine,
        [
            string.Empty,
            string.Empty,
            "---------- Forwarded message ---------",
            $"From: {Header(latest, "From", string.Empty)}",
            $"Date: {sentAt}",
            $"Subject: {Header(latest, "Subject", string.Empty)}",
            $"To: {Header(latest, "To", string.Empty)}",
            string.Empty,
            await ExtractBodyAsync(latest, cancellationToken)
        ]);

        return new ComposeMailRequest
        {
            Subject = EnsureSubjectPrefix(Header(latest, "Subject", string.Empty), "Fwd:"),
            BodyText = forwarded,
            Attachments = await GetMessageAttachmentsAsync(latest, cancellationToken)
        };
    }

    public async Task<MailFolder> CreateFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var normalized = string.Join('/', folderPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Enter a folder name.", nameof(folderPath));
        }

        var service = RequireClient();
        var existing = (await service.Users.Labels.List(UserId).ExecuteAsync(cancellationToken)).Labels ?? [];
        var parts = normalized.Split('/');
        Label? created = null;

        for (var index = 0; index < parts.Length; index++)
        {
            var currentPath = string.Join('/', parts.Take(index + 1));
            var match = existing.FirstOrDefault(label =>
                string.Equals(label.Name, currentPath, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                created = match;
                continue;
            }

            created = await service.Users.Labels.Create(new Label
            {
                Name = currentPath,
                LabelListVisibility = "labelShow",
                MessageListVisibility = "show"
            }, UserId).ExecuteAsync(cancellationToken);
            existing.Add(created);
        }

        InvalidateLabelUnreadCounts();
        return ToFolder(created!);
    }

    public async Task DeleteFolderAsync(string labelId, CancellationToken cancellationToken = default)
    {
        await RequireClient().Users.Labels.Delete(UserId, labelId).ExecuteAsync(cancellationToken);
        InvalidateLabelUnreadCounts();
    }

    public async Task<int> GetFolderThreadCountAsync(
        string labelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelId);
        var label = await RequireClient().Users.Labels.Get(UserId, labelId)
            .ExecuteAsync(cancellationToken);
        return (int)(label.ThreadsTotal ?? 0);
    }

    public async Task<IReadOnlyList<string>> TrashFolderThreadsAsync(
        string labelId,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelId);
        var service = RequireClient();
        var threadIds = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        do
        {
            var list = service.Users.Threads.List(UserId);
            list.LabelIds = new[] { labelId };
            list.IncludeSpamTrash = true;
            list.MaxResults = 500;
            list.PageToken = pageToken;
            var page = await ExecuteWithQuotaRetryAsync(
                () => list.ExecuteAsync(cancellationToken),
                null,
                cancellationToken);
            foreach (var thread in page.Threads ?? [])
            {
                if (!string.IsNullOrWhiteSpace(thread.Id))
                {
                    threadIds.Add(thread.Id);
                }
            }

            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        var ids = threadIds.ToList();
        progress?.Invoke(0, ids.Count);
        for (var index = 0; index < ids.Count; index++)
        {
            await ExecuteWithQuotaRetryAsync(
                async () =>
                {
                    await service.Users.Threads.Trash(UserId, ids[index]).ExecuteAsync(cancellationToken);
                    return true;
                },
                null,
                cancellationToken);
            progress?.Invoke(index + 1, ids.Count);

            // Thread.trash costs 20 quota units. Four requests per second stays
            // beneath Gmail's per-user rolling quota during a large label cleanup.
            if (index < ids.Count - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }

        InvalidateLabelUnreadCounts();
        return ids;
    }

    public async Task<IReadOnlyList<MailSignature>> GetSignaturesAsync(
        CancellationToken cancellationToken = default)
    {
        var service = await EnsureSettingsClientAsync(cancellationToken);
        var response = await service.Users.Settings.SendAs.List(UserId).ExecuteAsync(cancellationToken);
        return (response.SendAs ?? [])
            .Where(item => string.Equals(item.VerificationStatus, "accepted", StringComparison.OrdinalIgnoreCase)
                || item.IsPrimary == true)
            .OrderByDescending(item => item.IsDefault == true)
            .ThenByDescending(item => item.IsPrimary == true)
            .ThenBy(item => item.SendAsEmail, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new MailSignature(
                item.SendAsEmail,
                item.DisplayName ?? string.Empty,
                item.Signature ?? string.Empty,
                SignatureHtmlToText(item.Signature),
                item.IsPrimary == true,
                item.IsDefault == true)
            {
                Id = item.SendAsEmail,
                IsGmailBacked = true
            })
            .ToList();
    }

    public async Task<MailSignature> UpdateSignatureAsync(
        MailSignature signature,
        string signatureText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var service = await EnsureSettingsClientAsync(cancellationToken);
        var html = SignatureTextToHtml(signatureText);
        var updated = await service.Users.Settings.SendAs.Patch(
                new SendAs { Signature = html },
                UserId,
                signature.EmailAddress)
            .ExecuteAsync(cancellationToken);
        return new MailSignature(
            updated.SendAsEmail ?? signature.EmailAddress,
            updated.DisplayName ?? signature.DisplayName,
            updated.Signature ?? html,
            SignatureHtmlToText(updated.Signature ?? html),
            updated.IsPrimary ?? signature.IsPrimary,
            updated.IsDefault ?? signature.IsDefault)
        {
            Id = signature.Id,
            IsGmailBacked = true
        };
    }

    public async Task RenameFolderTreeAsync(
        string currentFullName,
        string newFullName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newFullName);
        var normalizedCurrent = NormalizeFolderPath(currentFullName);
        var normalizedNew = NormalizeFolderPath(newFullName);
        if (string.Equals(normalizedCurrent, normalizedNew, StringComparison.Ordinal))
        {
            return;
        }

        if (normalizedNew.StartsWith($"{normalizedCurrent}/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A folder cannot be renamed into one of its own subfolders.");
        }

        var service = RequireClient();
        var labels = (await service.Users.Labels.List(UserId).ExecuteAsync(cancellationToken)).Labels ?? [];
        var affected = labels
            .Where(label => label.Type == "user"
                && (string.Equals(label.Name, normalizedCurrent, StringComparison.OrdinalIgnoreCase)
                    || label.Name.StartsWith($"{normalizedCurrent}/", StringComparison.OrdinalIgnoreCase)))
            .Select(label => new
            {
                Label = label,
                NewName = normalizedNew + label.Name[normalizedCurrent.Length..]
            })
            .OrderByDescending(item => item.Label.Name.Count(character => character == '/'))
            .ToList();
        if (affected.Count == 0)
        {
            throw new InvalidOperationException("The Gmail label no longer exists.");
        }

        var affectedIds = affected.Select(item => item.Label.Id).ToHashSet(StringComparer.Ordinal);
        var existingNames = labels
            .Where(label => !affectedIds.Contains(label.Id))
            .Select(label => label.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflict = affected.FirstOrDefault(item => existingNames.Contains(item.NewName));
        if (conflict is not null)
        {
            throw new InvalidOperationException($"A Gmail label named '{conflict.NewName}' already exists.");
        }

        var renamed = new List<(string Id, string OldName)>();
        try
        {
            foreach (var item in affected)
            {
                await service.Users.Labels.Patch(
                        new Label { Name = item.NewName },
                        UserId,
                        item.Label.Id)
                    .ExecuteAsync(cancellationToken);
                renamed.Add((item.Label.Id, item.Label.Name));
            }

            InvalidateLabelUnreadCounts();
        }
        catch
        {
            foreach (var item in renamed.AsEnumerable().Reverse())
            {
                try
                {
                    await service.Users.Labels.Patch(
                            new Label { Name = item.OldName },
                            UserId,
                            item.Id)
                        .ExecuteAsync(CancellationToken.None);
                }
                catch
                {
                    // Preserve the original failure; the next sync will reveal any partial rename.
                }
            }

            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        gmail?.Dispose();
        gmail = null;
        gmailSettings?.Dispose();
        gmailSettings = null;
        connectedEmailAddress = null;
        labelUnreadCounts.Clear();
        labelUnreadCountsExpiresAt = DateTimeOffset.MinValue;
        await secureStore.ClearAsync();
    }

    private async Task<GmailService> EnsureSettingsClientAsync(CancellationToken cancellationToken)
    {
        if (gmailSettings is not null)
        {
            return gmailSettings;
        }

        var clientJson = LoadPackagedClientSecrets()
            ?? await secureStore.GetAsync<string>(ClientSecretsKey);
        if (string.IsNullOrWhiteSpace(clientJson))
        {
            throw new InvalidOperationException("This build does not contain a Google OAuth client configuration.");
        }

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(clientJson));
        var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            SettingsScopes,
            SettingsCredentialKey,
            cancellationToken,
            secureStore);
        gmailSettings = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "GLook"
        });
        return gmailSettings;
    }

    private GmailService RequireClient() =>
        gmail ?? throw new InvalidOperationException("Connect a Gmail account before syncing.");

    private async Task<Message> CreateApiMessageAsync(
        ComposeMailRequest request,
        CancellationToken cancellationToken,
        bool allowNoRecipients = false)
    {
        var mimeMessage = new MimeMessage
        {
            MessageId = string.IsNullOrWhiteSpace(request.RfcMessageId)
                ? MimeUtils.GenerateMessageId()
                : request.RfcMessageId.Trim(),
            Subject = request.Subject?.Trim() ?? string.Empty
        };
        mimeMessage.From.Add(MailboxAddress.Parse(await GetConnectedEmailAddressAsync(cancellationToken)));
        AddAddresses(mimeMessage.To, request.To);
        AddAddresses(mimeMessage.Cc, request.Cc);
        AddAddresses(mimeMessage.Bcc, request.Bcc);
        if (!allowNoRecipients && mimeMessage.To.Count + mimeMessage.Cc.Count + mimeMessage.Bcc.Count == 0)
        {
            throw new ArgumentException("Add at least one recipient before sending.", nameof(request));
        }

        if (!string.IsNullOrWhiteSpace(request.InReplyTo))
        {
            mimeMessage.InReplyTo = request.InReplyTo.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.References))
        {
            mimeMessage.Headers[HeaderId.References] = request.References.Trim();
        }

        var attachments = request.Attachments ?? [];
        ValidateAttachmentSet(attachments);
        var builder = new BodyBuilder
        {
            TextBody = request.BodyText ?? string.Empty,
            HtmlBody = string.IsNullOrWhiteSpace(request.BodyHtml) ? null : request.BodyHtml
        };
        foreach (var attachment in attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.FileName))
            {
                throw new ArgumentException("Every attachment needs a file name.", nameof(request));
            }

            var contentType = ContentType.TryParse(attachment.ContentType, out var parsed)
                ? parsed
                : new ContentType("application", "octet-stream");
            builder.Attachments.Add(attachment.FileName, attachment.Content, contentType);
        }

        mimeMessage.Body = builder.ToMessageBody();
        await using var output = new MemoryStream();
        await mimeMessage.WriteToAsync(output, cancellationToken);
        return new Message
        {
            Raw = EncodeBase64Url(output.ToArray()),
            ThreadId = string.IsNullOrWhiteSpace(request.ThreadId) ? null : request.ThreadId
        };
    }

    private async Task<string> GetConnectedEmailAddressAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(connectedEmailAddress))
        {
            return connectedEmailAddress;
        }

        var profile = await RequireClient().Users.GetProfile(UserId).ExecuteAsync(cancellationToken);
        connectedEmailAddress = profile.EmailAddress;
        return connectedEmailAddress;
    }

    private async Task<Message> GetLatestRawMessageAsync(
        string threadId,
        bool includeContent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var request = RequireClient().Users.Threads.Get(UserId, threadId);
        request.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Metadata;
        request.MetadataHeaders = new[] { "From", "To", "Cc", "Reply-To", "Subject", "Date", "Message-Id", "References" };
        var thread = await request.ExecuteAsync(cancellationToken);
        var latest = (thread.Messages ?? [])
            .OrderByDescending(message => message.InternalDate ?? 0)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The selected conversation does not contain a message.");
        if (!includeContent)
        {
            return latest;
        }

        var messageRequest = RequireClient().Users.Messages.Get(UserId, latest.Id);
        messageRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
        return await messageRequest.ExecuteAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<MailAttachmentInput>> GetMessageAttachmentsAsync(
        Message message,
        CancellationToken cancellationToken)
    {
        var parts = GetBoundedParts(message.Payload, out var mimeLimitExceeded);
        if (mimeLimitExceeded)
        {
            throw new InvalidOperationException(
                $"This message has more than {MailResourceLimits.MaxMimePartCount} MIME parts or is nested more than {MailResourceLimits.MaxMimeDepth} levels, so its attachments cannot be forwarded safely.");
        }

        var attachmentParts = parts
            .Where(part => !string.IsNullOrWhiteSpace(part.Filename))
            .ToList();
        if (attachmentParts.Count > MailResourceLimits.MaxAttachmentCount)
        {
            throw new InvalidOperationException(
                $"This message has {attachmentParts.Count} attachments. GLook can forward up to {MailResourceLimits.MaxAttachmentCount} at once.");
        }

        var attachments = new List<MailAttachmentInput>(attachmentParts.Count);
        long totalBytes = 0;
        foreach (var part in attachmentParts)
        {
            var declaredBytes = part.Body?.Size ?? 0;
            EnsureAttachmentFits(part.Filename, declaredBytes, totalBytes);

            byte[] content;
            if (!string.IsNullOrWhiteSpace(part.Body?.Data))
            {
                content = DecodeBase64UrlBytes(
                    part.Body.Data,
                    MailResourceLimits.MaxAttachmentBytes,
                    $"Attachment '{part.Filename}'");
            }
            else if (!string.IsNullOrWhiteSpace(part.Body?.AttachmentId))
            {
                var body = await RequireClient().Users.Messages.Attachments
                    .Get(UserId, message.Id, part.Body.AttachmentId)
                    .ExecuteAsync(cancellationToken);
                EnsureAttachmentFits(part.Filename, body.Size ?? 0, totalBytes);
                content = string.IsNullOrWhiteSpace(body.Data)
                    ? []
                    : DecodeBase64UrlBytes(
                        body.Data,
                        MailResourceLimits.MaxAttachmentBytes,
                        $"Attachment '{part.Filename}'");
            }
            else
            {
                continue;
            }

            EnsureAttachmentFits(part.Filename, content.LongLength, totalBytes);
            totalBytes += content.LongLength;
            attachments.Add(new MailAttachmentInput(
                part.Filename,
                content,
                string.IsNullOrWhiteSpace(part.MimeType) ? "application/octet-stream" : part.MimeType));
        }

        return attachments;
    }

    private static IReadOnlyList<Google.Apis.Gmail.v1.Data.MessagePart> GetBoundedParts(
        Google.Apis.Gmail.v1.Data.MessagePart? root,
        out bool limitExceeded)
    {
        limitExceeded = false;
        if (root is null)
        {
            return [];
        }

        var parts = new List<Google.Apis.Gmail.v1.Data.MessagePart>();
        var pending = new Stack<(Google.Apis.Gmail.v1.Data.MessagePart Part, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (part, depth) = pending.Pop();
            if (depth > MailResourceLimits.MaxMimeDepth
                || parts.Count >= MailResourceLimits.MaxMimePartCount)
            {
                limitExceeded = true;
                break;
            }

            parts.Add(part);
            var children = part.Parts ?? [];
            if (depth == MailResourceLimits.MaxMimeDepth && children.Count > 0)
            {
                limitExceeded = true;
                break;
            }

            if (children.Count > MailResourceLimits.MaxMimePartCount - parts.Count)
            {
                limitExceeded = true;
                break;
            }

            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push((children[index], depth + 1));
            }
        }

        return parts;
    }

    private static void ValidateAttachmentSet(IReadOnlyList<MailAttachmentInput> attachments)
    {
        if (attachments.Count > MailResourceLimits.MaxAttachmentCount)
        {
            throw new InvalidOperationException(
                $"A message can include up to {MailResourceLimits.MaxAttachmentCount} attachments.");
        }

        long totalBytes = 0;
        foreach (var attachment in attachments)
        {
            EnsureAttachmentFits(attachment.FileName, attachment.Content.LongLength, totalBytes);
            totalBytes += attachment.Content.LongLength;
        }
    }

    private static void EnsureAttachmentFits(string fileName, long attachmentBytes, long currentTotalBytes)
    {
        if (attachmentBytes > MailResourceLimits.MaxAttachmentBytes)
        {
            throw new InvalidOperationException(
                $"Attachment '{fileName}' is larger than GLook's {FormatMegabytes(MailResourceLimits.MaxAttachmentBytes)} MB per-file limit.");
        }

        if (attachmentBytes > MailResourceLimits.MaxAggregateAttachmentBytes - currentTotalBytes)
        {
            throw new InvalidOperationException(
                $"Attachments exceed GLook's {FormatMegabytes(MailResourceLimits.MaxAggregateAttachmentBytes)} MB total limit.");
        }
    }

    private static int FormatMegabytes(int bytes) => bytes / (1024 * 1024);

    private static string[] NormalizeLabelIds(IEnumerable<string>? labelIds) =>
        (labelIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static void AddAddresses(InternetAddressList target, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        target.AddRange(InternetAddressList.Parse(value));
    }

    private static string CombineRecipientsExcludingOwn(string ownAddress, params string[] sources)
    {
        var recipients = new InternetAddressList();
        foreach (var source in sources.Where(source => !string.IsNullOrWhiteSpace(source)))
        {
            AddAddresses(recipients, source);
        }

        var addresses = recipients.Mailboxes
            .Where(address => !string.Equals(address.Address, ownAddress, StringComparison.OrdinalIgnoreCase))
            .GroupBy(address => address.Address, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().ToString());
        return string.Join(", ", addresses);
    }

    private static string EnsureSubjectPrefix(string subject, string prefix) =>
        subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? subject
            : $"{prefix} {subject}".TrimEnd();

    private static string? AppendReference(string? references, string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return references;
        }

        return string.IsNullOrWhiteSpace(references)
            ? messageId
            : $"{references.Trim()} {messageId.Trim()}";
    }

    private static string? LoadPackagedClientSecrets()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(EmbeddedClientResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private async Task ApplyUnreadCountsAsync(
        GmailService service,
        IEnumerable<Label> labelSource,
        CancellationToken cancellationToken)
    {
        var labels = labelSource.Where(label => !string.IsNullOrWhiteSpace(label.Id)).ToList();
        await labelCountRefreshGate.WaitAsync(cancellationToken);
        try
        {
            var needsRefresh = DateTimeOffset.UtcNow >= labelUnreadCountsExpiresAt
                || labels.Any(label => !labelUnreadCounts.ContainsKey(label.Id));
            if (needsRefresh && labels.Count > 0)
            {
                using var throttle = new SemaphoreSlim(8);
                var tasks = labels.Select(async label =>
                {
                    await throttle.WaitAsync(cancellationToken);
                    try
                    {
                        var detail = await service.Users.Labels.Get(UserId, label.Id)
                            .ExecuteAsync(cancellationToken);
                        return new LabelUnreadCountResult(
                            label.Id,
                            new LabelUnreadCounts(
                                detail.MessagesUnread ?? 0,
                                detail.ThreadsUnread ?? 0,
                                detail.MessagesTotal ?? 0,
                                detail.ThreadsTotal ?? 0));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        return null;
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });

                var results = (await Task.WhenAll(tasks))
                    .Where(result => result is not null)
                    .Select(result => result!)
                    .ToList();
                if (results.Count == 0)
                {
                    throw new InvalidOperationException("Gmail returned labels but their unread counts could not be loaded.");
                }

                var refreshed = new Dictionary<string, LabelUnreadCounts>(StringComparer.Ordinal);
                foreach (var label in labels)
                {
                    refreshed[label.Id] = labelUnreadCounts.GetValueOrDefault(label.Id);
                }

                foreach (var result in results)
                {
                    refreshed[result.Id] = result.Counts;
                }

                labelUnreadCounts = refreshed;
                labelUnreadCountsExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
            }

            foreach (var label in labels)
            {
                if (labelUnreadCounts.TryGetValue(label.Id, out var counts))
                {
                    label.MessagesUnread = counts.MessagesUnread;
                    label.ThreadsUnread = counts.ThreadsUnread;
                    label.MessagesTotal = counts.MessagesTotal;
                    label.ThreadsTotal = counts.ThreadsTotal;
                }
            }
        }
        finally
        {
            labelCountRefreshGate.Release();
        }
    }

    private void InvalidateLabelUnreadCounts() => labelUnreadCountsExpiresAt = DateTimeOffset.MinValue;

    private static bool ShouldShowLabel(Label label) =>
        label.Type == "user"
        || label.Id is "INBOX" or "STARRED" or "SENT" or "DRAFT" or "IMPORTANT" or "SPAM" or "TRASH"
        || IsVisibleCategory(label);

    private static bool IsVisibleCategory(Label label)
    {
        if (!label.Id.StartsWith("CATEGORY_", StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(label.LabelListVisibility, "labelShow", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(label.LabelListVisibility, "labelShowIfUnread", StringComparison.OrdinalIgnoreCase)
                && (label.MessagesUnread > 0 || label.ThreadsUnread > 0));
    }

    private static MailFolder ToFolder(Label label) => new(
        label.Id,
        DisplayNameForLabel(label.Id, LeafName(label.Name)),
        DisplayNameForLabel(label.Id, label.Name),
        GlyphForLabel(label.Id),
        label.Type == "system",
        label.Type == "user",
        (int)(label.MessagesUnread ?? 0),
        (int)(label.ThreadsTotal ?? 0));

    private static string LeafName(string? name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "Unnamed" : name;
        var slash = value.LastIndexOf('/');
        return slash >= 0 ? value[(slash + 1)..] : value;
    }

    private static string NormalizeFolderPath(string folderPath)
    {
        var normalized = string.Join('/', folderPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Enter a folder name.", nameof(folderPath));
        }

        return normalized;
    }

    private static string GlyphForLabel(string id) => id switch
    {
        "INBOX" => "\uE715",
        "CATEGORY_PERSONAL" => "\uE715",
        "CATEGORY_SOCIAL" => "\uE716",
        "CATEGORY_PROMOTIONS" => "\uE8EC",
        "CATEGORY_UPDATES" => "\uE895",
        "CATEGORY_FORUMS" => "\uE8BD",
        "STARRED" => "\uE734",
        "SENT" => "\uE724",
        "DRAFT" => "\uE70B",
        "IMPORTANT" => "\uE814",
        "SPAM" => "\uE7BA",
        "TRASH" => "\uE74D",
        _ => "\uE8B7"
    };

    private static int FolderOrder(string id) => id switch
    {
        "INBOX" => 0,
        "CATEGORY_PERSONAL" => 1,
        "CATEGORY_SOCIAL" => 2,
        "CATEGORY_PROMOTIONS" => 3,
        "CATEGORY_UPDATES" => 4,
        "CATEGORY_FORUMS" => 5,
        "STARRED" => 10,
        "IMPORTANT" => 11,
        "SENT" => 12,
        "DRAFT" => 13,
        "SPAM" => 90,
        "TRASH" => 91,
        _ => 20
    };

    private static string DisplayNameForLabel(string id, string fallback) => id switch
    {
        "CATEGORY_PERSONAL" => "Primary",
        "CATEGORY_SOCIAL" => "Social",
        "CATEGORY_PROMOTIONS" => "Promotions",
        "CATEGORY_UPDATES" => "Updates",
        "CATEGORY_FORUMS" => "Forums",
        _ => fallback
    };

    private static async Task<T> ExecuteWithQuotaRetryAsync<T>(
        Func<Task<T>> operation,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        var retryDelays = new[]
        {
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60)
        };

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (GoogleApiException ex) when (IsQuotaLimit(ex) && attempt < retryDelays.Length)
            {
                var delay = retryDelays[attempt];
                status?.Invoke(
                    $"Gmail is temporarily limiting sync requests. Retrying in {delay.TotalSeconds:0} seconds.");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsQuotaLimit(GoogleApiException exception) =>
        exception.HttpStatusCode == HttpStatusCode.TooManyRequests
        || (exception.HttpStatusCode == HttpStatusCode.Forbidden
            && (exception.Error?.Errors?.Any(error =>
                    string.Equals(error.Reason, "rateLimitExceeded", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(error.Reason, "userRateLimitExceeded", StringComparison.OrdinalIgnoreCase)) == true
                || exception.Message.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase)));

    private static string SignatureHtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var withLines = Regex.Replace(html, "<(br\\s*/?|/p|/div|/li)>", Environment.NewLine, RegexOptions.IgnoreCase);
        var withoutMarkup = Regex.Replace(withLines, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(withoutMarkup)
            .Replace("\u00A0", " ")
            .ReplaceLineEndings(Environment.NewLine)
            .Trim();
    }

    private static string SignatureTextToHtml(string text) =>
        WebUtility.HtmlEncode(text.Trim())
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal);

    private static MailThreadSummary ToSummary(Google.Apis.Gmail.v1.Data.Thread thread)
    {
        var messages = thread.Messages ?? [];
        var latest = messages.OrderByDescending(message => message.InternalDate ?? 0).FirstOrDefault();
        var labels = messages.SelectMany(message => message.LabelIds ?? []).Distinct(StringComparer.Ordinal).ToList();
        return new MailThreadSummary(
            thread.Id,
            latest is null ? "Unknown sender" : Header(latest, "From", "Unknown sender"),
            latest is null ? "(no subject)" : Header(latest, "Subject", "(no subject)"),
            WebUtility.HtmlDecode(thread.Snippet ?? string.Empty),
            latest is null ? DateTimeOffset.MinValue : FromUnixMilliseconds(latest.InternalDate),
            labels.Contains("UNREAD", StringComparer.Ordinal),
            labels.Contains("STARRED", StringComparer.Ordinal),
            labels);
    }

    private static string Header(Message message, string name, string fallback) =>
        message.Payload?.Headers?.FirstOrDefault(header =>
            string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? fallback;

    private static string? HeaderOrNull(Message message, string name)
    {
        var value = Header(message, name, string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static DateTimeOffset FromUnixMilliseconds(long? value) =>
        value is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : DateTimeOffset.MinValue;

    private async Task<string> ExtractBodyAsync(Message message, CancellationToken cancellationToken) =>
        (await ExtractBodyContentAsync(message, cancellationToken)).Text;

    private async Task<MessageBodyContent> ExtractBodyContentAsync(
        Message message,
        CancellationToken cancellationToken)
    {
        var part = message.Payload;
        if (part is null)
        {
            return new MessageBodyContent(string.Empty, null);
        }

        var parts = GetBoundedParts(part, out var mimeLimitExceeded);
        if (mimeLimitExceeded)
        {
            return new MessageBodyContent(MimeStructureOmissionMarker, null);
        }

        var plain = parts.FirstOrDefault(candidate =>
            string.Equals(candidate.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase));
        var plainText = await ReadPartBodyAsync(message.Id, plain, cancellationToken);
        var html = parts.FirstOrDefault(candidate =>
            string.Equals(candidate.MimeType, "text/html", StringComparison.OrdinalIgnoreCase));
        var htmlText = await ReadPartBodyAsync(message.Id, html, cancellationToken);
        if (string.IsNullOrWhiteSpace(plainText) && !string.IsNullOrWhiteSpace(htmlText))
        {
            var withoutMarkup = Regex.Replace(htmlText, "<[^>]+>", " ");
            plainText = WebUtility.HtmlDecode(Regex.Replace(withoutMarkup, "\\s+", " ")).Trim();
        }

        if (string.IsNullOrWhiteSpace(plainText) && string.IsNullOrWhiteSpace(htmlText))
        {
            plainText = await ReadPartBodyAsync(message.Id, part, cancellationToken);
        }

        return new MessageBodyContent(
            plainText,
            string.IsNullOrWhiteSpace(htmlText) ? null : htmlText);
    }

    private async Task<string> ReadPartBodyAsync(
        string messageId,
        Google.Apis.Gmail.v1.Data.MessagePart? part,
        CancellationToken cancellationToken)
    {
        if (part?.Body?.Size is > MailResourceLimits.MaxDecodedBodyBytes)
        {
            return BodySizeOmissionMarker;
        }

        if (!string.IsNullOrWhiteSpace(part?.Body?.Data))
        {
            return DecodeBase64UrlText(part.Body.Data, MailResourceLimits.MaxDecodedBodyBytes)
                ?? BodySizeOmissionMarker;
        }

        if (string.IsNullOrWhiteSpace(part?.Body?.AttachmentId))
        {
            return string.Empty;
        }

        var body = await RequireClient().Users.Messages.Attachments
            .Get(UserId, messageId, part.Body.AttachmentId)
            .ExecuteAsync(cancellationToken);
        if (body.Size is > MailResourceLimits.MaxDecodedBodyBytes)
        {
            return BodySizeOmissionMarker;
        }

        return string.IsNullOrWhiteSpace(body.Data)
            ? string.Empty
            : DecodeBase64UrlText(body.Data, MailResourceLimits.MaxDecodedBodyBytes)
                ?? BodySizeOmissionMarker;
    }

    private static bool IsMoveFolderLabel(string labelId) =>
        string.Equals(labelId, "INBOX", StringComparison.Ordinal)
        || labelId.StartsWith("Label_", StringComparison.Ordinal);

    private static string? DecodeBase64UrlText(string value, int maxDecodedBytes)
    {
        if (EstimateDecodedLength(value) > maxDecodedBytes)
        {
            return null;
        }

        var bytes = DecodeBase64UrlBytes(value, maxDecodedBytes, "Message body");
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] DecodeBase64UrlBytes(string value, int maxDecodedBytes, string description)
    {
        if (EstimateDecodedLength(value) > maxDecodedBytes)
        {
            throw new InvalidDataException($"{description} exceeds GLook's safe decoding limit.");
        }

        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        var decoded = Convert.FromBase64String(normalized);
        if (decoded.Length > maxDecodedBytes)
        {
            throw new InvalidDataException($"{description} exceeds GLook's safe decoding limit.");
        }

        return decoded;
    }

    private static long EstimateDecodedLength(string value) => ((long)value.Length + 3) / 4 * 3;

    private static string EncodeBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private readonly record struct LabelUnreadCounts(
        int MessagesUnread,
        int ThreadsUnread,
        int MessagesTotal,
        int ThreadsTotal);

    private readonly record struct MessageBodyContent(string Text, string? Html);

    private sealed record LabelUnreadCountResult(string Id, LabelUnreadCounts Counts);
}
