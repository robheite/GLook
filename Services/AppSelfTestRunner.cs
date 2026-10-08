using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Google;
using GLook.Models;
using GLook.ViewModels;
using Microsoft.Windows.AppNotifications;

namespace GLook.Services;

public sealed record AppSelfTestCheck(
    string Name,
    string Status,
    string Detail,
    long DurationMilliseconds);

public sealed record AppSelfTestReport(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Passed,
    bool MailboxMutationsPerformed,
    IReadOnlyList<AppSelfTestCheck> Checks);

public static class AppSelfTestRunner
{
    public static string ReportPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GLook",
        "self-test-report.json");

    public static string LabelLifecycleReportPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GLook",
        "label-lifecycle-report.json");

    public static string MailActionReportPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GLook",
        "mail-action-report.json");

    public static string NotificationReportPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GLook",
        "notification-test-report.json");

    public static string SyncAllReportPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GLook",
        "sync-all-test-report.json");

    public static async Task<AppSelfTestReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.Now;
        var checks = new List<AppSelfTestCheck>();
        var appDataPath = Path.GetDirectoryName(ReportPath)!;
        var context = await CreateSelfTestContextAsync(appDataPath, cancellationToken);
        var localStore = context.LocalStore;
        var gmail = context.Gmail;

        await RunCheckAsync(checks, "Local cache opens", async () =>
        {
            await localStore.InitializeAsync();
            return "SQLite cache schema is available.";
        });

        await RunCheckAsync(checks, "Startup reconnect stays non-interactive", async () =>
        {
            var testDirectory = Path.Combine(
                Path.GetTempPath(),
                $"GLook-silent-reconnect-self-test-{Guid.NewGuid():N}");
            try
            {
                var disconnectedClient = new GmailClientService(new EncryptedDataStore(testDirectory));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var profile = await disconnectedClient.TryReconnectAsync(timeout.Token);
                if (profile is not null)
                {
                    throw new InvalidOperationException("An empty credential store unexpectedly restored a Gmail account.");
                }
            }
            finally
            {
                if (Directory.Exists(testDirectory))
                {
                    Directory.Delete(testDirectory, recursive: true);
                }
            }

            return "Startup checks only encrypted saved credentials; browser OAuth requires an explicit Connect or Reauthorize action.";
        });

        await RunCheckAsync(checks, "History cursor and quota safeguards", async () =>
        {
            if (GmailQuotaGovernor.UnitsFor(GmailApiMethod.HistoryList) != 2
                || GmailQuotaGovernor.UnitsFor(GmailApiMethod.ThreadsGet) != 40
                || GmailQuotaGovernor.BackgroundUnitsPerMinute != 4_500)
            {
                throw new InvalidOperationException("Gmail quota weights or the background reserve changed unexpectedly.");
            }

            var testDirectory = Path.Combine(
                Path.GetTempPath(),
                $"GLook-history-self-test-{Guid.NewGuid():N}");
            try
            {
                var testStore = new LocalMailStore(Path.Combine(testDirectory, "history.db"));
                await testStore.InitializeAsync();
                testStore.SetAccountScope("history-self-test@example.invalid");
                var thread = new MailThreadSummary(
                    "history-thread",
                    "GLook",
                    "History transaction",
                    string.Empty,
                    DateTimeOffset.UtcNow,
                    true,
                    false,
                    ["INBOX", "UNREAD"]);
                await testStore.ApplyHistorySyncAsync(
                    new GmailHistorySyncResult(100, 101, [thread], [], [thread]));
                if (await testStore.GetHistoryCursorAsync() != 101
                    || (await testStore.LoadThreadsAsync("INBOX", 5)).SingleOrDefault()?.Id != thread.Id)
                {
                    throw new InvalidOperationException("The encrypted history transaction did not persist its cursor and thread together.");
                }

                await testStore.ApplyHistorySyncAsync(
                    new GmailHistorySyncResult(101, 102, [], [thread.Id], []));
                if (await testStore.GetHistoryCursorAsync() != 102
                    || (await testStore.LoadThreadsAsync("INBOX", 5)).Count != 0)
                {
                    throw new InvalidOperationException("The encrypted history transaction did not apply removal and cursor advancement together.");
                }
            }
            finally
            {
                if (Directory.Exists(testDirectory))
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    Directory.Delete(testDirectory, recursive: true);
                }
            }

            return "Encrypted history applies advance the cursor atomically; official history/thread quota weights and the 4,500-unit background budget are active.";
        });

        await RunCheckAsync(checks, "Multi-account and Board isolation", async () =>
        {
            var testDirectory = Path.Combine(
                Path.GetTempPath(),
                $"GLook-account-board-self-test-{Guid.NewGuid():N}");
            try
            {
                var registry = new AccountRegistry(Path.Combine(testDirectory, "secure"));
                var first = await registry.AddAsync(Guid.NewGuid(), "first@example.invalid", "First mailbox");
                var second = await registry.AddAsync(Guid.NewGuid(), "second@example.invalid", "Second mailbox");
                await registry.SetOrderAsync([second.AccountId, first.AccountId]);
                var accounts = await registry.GetAccountsAsync();
                if (accounts.Count != 2 || accounts[0].AccountId != second.AccountId)
                {
                    throw new InvalidOperationException("Account ordering or stable identities were not preserved.");
                }

                var coordinator = new GmailAccountSessionCoordinator(testDirectory);
                var firstCredentialDirectory = registry.GetCredentialDirectory(first.AccountId);
                Directory.CreateDirectory(firstCredentialDirectory);
                var credentialMarkerPath = Path.Combine(firstCredentialDirectory, "rollback-marker.bin");
                await File.WriteAllBytesAsync(credentialMarkerPath, [1, 2, 3], cancellationToken);
                await coordinator.PreserveLegacyCredentialSnapshotAsync(first.AccountId, cancellationToken);
                var legacyMarkerPath = Path.Combine(testDirectory, "secure", "rollback-marker.bin");
                if (!File.Exists(legacyMarkerPath)
                    || !File.ReadAllBytes(legacyMarkerPath).SequenceEqual(new byte[] { 1, 2, 3 }))
                {
                    throw new InvalidOperationException("The legacy credential rollback snapshot was not preserved.");
                }

                await coordinator.DisposeAsync();

                var registryBytes = Directory.EnumerateFiles(
                        Path.Combine(testDirectory, "secure", "account-registry"),
                        "*.bin")
                    .SelectMany(File.ReadAllBytes)
                    .ToArray();
                if (Encoding.UTF8.GetString(registryBytes).Contains("first@example.invalid", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The account registry contained a plaintext email address.");
                }

                var boardStore = new BoardSettingsStore(Path.Combine(testDirectory, "boards"));
                var (snapshot, board) = BoardConfigurationEditor.AddDefaultBoard(BoardSettingsSnapshot.Empty);
                var inbox = board.Columns.Single(column => column.DisplayTitle == "Inbox");
                snapshot = BoardConfigurationEditor.BindColumnToGmailLabel(
                    snapshot,
                    board.BoardId,
                    first.AccountId,
                    inbox.ColumnId,
                    "INBOX");
                snapshot = BoardConfigurationEditor.RenameColumn(
                    snapshot,
                    board.BoardId,
                    inbox.ColumnId,
                    "Incoming work");
                await boardStore.SaveAsync(snapshot);
                var loaded = await boardStore.LoadAsync();
                var binding = loaded.AccountBindings.Single().ColumnBindings.Single();
                var renamed = loaded.Boards.Single().Columns.Single(column => column.ColumnId == inbox.ColumnId);
                if (renamed.DisplayTitle != "Incoming work" || binding.GmailLabelId != "INBOX")
                {
                    throw new InvalidOperationException("Renaming a Board heading changed or lost its Gmail label binding.");
                }
            }
            finally
            {
                if (Directory.Exists(testDirectory))
                {
                    Directory.Delete(testDirectory, recursive: true);
                }
            }

            return "Account identities and ordering persist in the encrypted registry; Board headings remain independent from per-account Gmail label bindings.";
        });

        await RunCheckAsync(checks, "Windows notification registration", () =>
        {
            using var notifications = new WindowsNotificationService();
            if (!notifications.Register())
            {
                throw new InvalidOperationException(
                    notifications.RegistrationError ?? "Windows rejected notification registration.");
            }

            if (notifications.Setting != AppNotificationSetting.Enabled)
            {
                throw new InvalidOperationException(notifications.StatusText);
            }

            return Task.FromResult(
                "GLook registered successfully, and Windows reports notifications are enabled.");
        });

        await RunCheckAsync(checks, "Message timestamps use local time", () =>
        {
            var receivedAtUtc = DateTimeOffset.UtcNow;
            var summary = new MailThreadSummary(
                "timezone-self-test",
                "GLook",
                "Timezone check",
                string.Empty,
                receivedAtUtc,
                false,
                false,
                []);
            var expected = receivedAtUtc.ToLocalTime().ToString("h:mm tt");
            if (!string.Equals(summary.ReceivedText, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected local time {expected}, but the message list produced {summary.ReceivedText}.");
            }

            return Task.FromResult(
                $"UTC timestamps render at the current Windows offset " +
                $"({receivedAtUtc.ToLocalTime():zzz}; {TimeZoneInfo.Local.DisplayName}).");
        });

        await RunCheckAsync(checks, "Folder counts and collapsible message groups", () =>
        {
            var folder = new MailFolder("TEST", "Test", "Test", "\uE8B7", false, true, 12);
            if (folder.CountText != "(12)")
            {
                throw new InvalidOperationException($"Expected unread badge '(12)', but got '{folder.CountText}'.");
            }

            var receivedAt = DateTimeOffset.UtcNow;
            var sampleThreads = new[]
            {
                new MailThreadSummary("one", "One", "First", string.Empty, receivedAt, true, false, []),
                new MailThreadSummary("two", "Two", "Second", string.Empty, receivedAt, false, false, [])
            };
            var collapsed = new MailThreadGroup("date:Today", "Today  ·  2", sampleThreads, true);
            var expanded = new MailThreadGroup("date:Today", "Today  ·  2", sampleThreads);
            if (collapsed.Count != 0 || collapsed.TotalCount != 2 || !collapsed.IsCollapsed
                || expanded.Count != 2 || expanded.IsCollapsed)
            {
                throw new InvalidOperationException("Message group expansion state did not preserve its full item count.");
            }

            return Task.FromResult(
                "Unread badges use parenthesized message counts, and groups retain their items while collapsed.");
        });

        await RunCheckAsync(checks, "HTML mail rendering safeguards", () =>
        {
            var message = new MailMessage(
                "html-self-test",
                "Sender <sender@example.com>",
                "recipient@example.com",
                "HTML check",
                DateTimeOffset.UtcNow,
                "Formatted message")
            {
                BodyHtml = "<html><body><script>alert(1)</script><table><tr><td><b>Formatted</b></td></tr></table><img src=\"https://tracker.example/pixel.png\"></body></html>"
            };
            var detail = new MailThreadDetail("html-thread", "HTML check", [message]);
            var protectedDocument = MailHtmlRenderer.BuildDocument(detail, allowRemoteContent: false);
            if (protectedDocument.Contains("<script", StringComparison.OrdinalIgnoreCase)
                || protectedDocument.Contains("https://tracker.example", StringComparison.OrdinalIgnoreCase)
                || !protectedDocument.Contains("<table", StringComparison.OrdinalIgnoreCase)
                || !MailHtmlRenderer.ContainsRemoteContent(detail))
            {
                throw new InvalidOperationException("HTML formatting or remote-content protection did not behave as expected.");
            }

            var localSignature = new MailSignature(string.Empty, "Work", string.Empty, "Regards", false, false)
            {
                Id = "local:self-test",
                IsGmailBacked = false
            };
            if (localSignature.DisplayLabel != "Work" || localSignature.SourceLabel != "GLook-only signature")
            {
                throw new InvalidOperationException("Local signature identity was not preserved.");
            }

            return Task.FromResult(
                "Email tables and formatting are retained, active content is removed, remote images are blocked by default, and local signatures remain distinct from Gmail signatures.");
        });

        GmailAccountProfile? profile = null;
        await RunCheckAsync(checks, "Stored Gmail session reconnects", async () =>
        {
            profile = await gmail.TryReconnectAsync(cancellationToken);
            if (profile is null)
            {
                throw new InvalidOperationException("No encrypted Gmail session was found for this Windows account.");
            }

            localStore.SetAccountScope(context.AccountScope ?? profile.EmailAddress);

            return $"Gmail profile loaded for {MaskEmail(profile.EmailAddress)}.";
        });

        if (profile is not null)
        {
            await RunCheckAsync(checks, "Legacy rollback credential reconnects", async () =>
            {
                var legacyClient = new GmailClientService(new EncryptedDataStore(
                    Path.Combine(appDataPath, "secure")));
                var legacyProfile = await legacyClient.TryReconnectAsync(cancellationToken);
                if (legacyProfile is null
                    || !string.Equals(
                        legacyProfile.EmailAddress,
                        profile.EmailAddress,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The preserved single-account credential does not match the active mailbox.");
                }

                return $"Older single-account builds can still restore {MaskEmail(legacyProfile.EmailAddress)}.";
            });
        }

        IReadOnlyList<MailFolder> folders = [];
        IReadOnlyList<MailThreadSummary> threads = [];

        if (profile is not null)
        {
            await RunCheckAsync(checks, "Gmail labels load", async () =>
            {
                folders = await gmail.GetFoldersAsync(cancellationToken);
                if (!folders.Any(folder => folder.Id == "INBOX"))
                {
                    throw new InvalidOperationException("Gmail did not return the Inbox system label.");
                }

                var categories = folders
                    .Where(folder => folder.Id.StartsWith("CATEGORY_", StringComparison.Ordinal))
                    .Select(folder => folder.Name)
                    .ToList();
                var categoryDetail = categories.Count == 0
                    ? "Gmail currently exposes no categories configured for label-list display."
                    : $"Visible Gmail categories: {string.Join(", ", categories)}.";
                return $"Loaded {folders.Count} visible folders and labels, including Inbox. {categoryDetail}";
            });

            await RunCheckAsync(checks, "Trash count loads read-only", async () =>
            {
                var messageCount = await gmail.GetFolderMessageCountAsync("TRASH", cancellationToken);
                return $"Gmail reports {messageCount:N0} message{(messageCount == 1 ? string.Empty : "s")} currently in Trash; no messages were changed.";
            });

            await RunCheckAsync(checks, "Inbox summaries load", async () =>
            {
                threads = await gmail.GetThreadsAsync(
                    "INBOX",
                    query: null,
                    maxResults: 5,
                    cancellationToken);
                return $"Loaded {threads.Count} recent Inbox conversation summaries.";
            });

            if (threads.Count > 0)
            {
                await RunCheckAsync(checks, "Conversation content loads read-only", async () =>
                {
                    var detail = await gmail.GetThreadAsync(threads[0].Id, cancellationToken);
                    if (detail.Id != threads[0].Id)
                    {
                        throw new InvalidDataException("The returned conversation did not match the request.");
                    }

                    return $"Loaded one conversation containing {detail.Messages.Count} message(s).";
                });
            }
            else
            {
                checks.Add(new AppSelfTestCheck(
                    "Conversation content loads read-only",
                    "Skipped",
                    "The Inbox sample was empty, so there was no conversation to open.",
                    0));
            }

            await RunCheckAsync(checks, "Local mirror round-trip", async () =>
            {
                await localStore.SaveThreadsAsync(threads);
                var cached = await localStore.LoadThreadsAsync("INBOX", 5);
                return $"Saved the sample and loaded {cached.Count} cached Inbox conversation(s).";
            });
        }
        else
        {
            AddSkippedGmailChecks(checks);
        }

        var report = new AppSelfTestReport(
            startedAt,
            DateTimeOffset.Now,
            checks.All(check => check.Status != "Failed"),
            MailboxMutationsPerformed: false,
            checks);

        Directory.CreateDirectory(appDataPath);
        await File.WriteAllTextAsync(
            ReportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        return report;
    }

    public static async Task<AppSelfTestReport> RunSyncAllAsync(
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.Now;
        var checks = new List<AppSelfTestCheck>();
        var appDataPath = Path.GetDirectoryName(SyncAllReportPath)!;
        var context = await CreateSelfTestContextAsync(appDataPath, cancellationToken);
        var viewModel = new MainViewModel(context.Gmail, context.LocalStore, context.SignatureStore);

        await RunCheckAsync(checks, "Sync Gmail history", async () =>
        {
            await viewModel.InitializeAsync(cancellationToken);
            if (!viewModel.IsConnected)
            {
                throw new InvalidOperationException(viewModel.SyncStatusDetail);
            }

            var visibleFolderCount = viewModel.Folders.Count;
            if (!await viewModel.SyncAllAsync(cancellationToken))
            {
                throw new InvalidOperationException(viewModel.SyncStatusDetail);
            }

            if (viewModel.IsBusy || viewModel.IsSyncingAll)
            {
                throw new InvalidOperationException("The sync operation did not return to an idle state.");
            }

            if (visibleFolderCount > 0 && Math.Abs(viewModel.SyncProgressValue - 100d) > 0.01d)
            {
                throw new InvalidOperationException(
                    $"Expected 100% progress, but the operation reported {viewModel.SyncProgressValue:0.##}%.");
            }

            return $"{viewModel.SyncStatusDetail} The selected folder remained {viewModel.SelectedFolder?.FullName ?? "available"}.";
        });

        var report = new AppSelfTestReport(
            startedAt,
            DateTimeOffset.Now,
            checks.All(check => check.Status != "Failed"),
            MailboxMutationsPerformed: false,
            checks);

        Directory.CreateDirectory(appDataPath);
        await File.WriteAllTextAsync(
            SyncAllReportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        return report;
    }

    public static async Task<AppSelfTestReport> RunNotificationDeliveryAsync(
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.Now;
        var checks = new List<AppSelfTestCheck>();

        await RunCheckAsync(checks, "Windows notification delivery", async () =>
        {
            using var notifications = new WindowsNotificationService();
            var result = await notifications.ShowTestAsync(cancellationToken);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Message);
            }

            return result.Message;
        });

        var report = new AppSelfTestReport(
            startedAt,
            DateTimeOffset.Now,
            checks.All(check => check.Status != "Failed"),
            MailboxMutationsPerformed: false,
            checks);

        Directory.CreateDirectory(Path.GetDirectoryName(NotificationReportPath)!);
        await File.WriteAllTextAsync(
            NotificationReportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        return report;
    }

    public static async Task<AppSelfTestReport> RunLabelLifecycleAsync(
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.Now;
        var checks = new List<AppSelfTestCheck>();
        var appDataPath = Path.GetDirectoryName(LabelLifecycleReportPath)!;
        var context = await CreateSelfTestContextAsync(appDataPath, cancellationToken);
        var gmail = context.Gmail;
        var viewModel = new MainViewModel(gmail, context.LocalStore, context.SignatureStore);
        var uniqueRoot = $"GLook Verification {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..54];
        var childPath = $"{uniqueRoot}/Nested label";
        var renamedRoot = $"{uniqueRoot} renamed";
        var renamedChildPath = $"{renamedRoot}/Nested label";

        await RunCheckAsync(checks, "Connect app model", async () =>
        {
            await viewModel.InitializeAsync();
            if (!viewModel.IsConnected)
            {
                throw new InvalidOperationException(viewModel.SyncStatusDetail);
            }

            return "The app model connected using the packaged OAuth client.";
        });

        if (viewModel.IsConnected)
        {
            try
            {
                await RunCheckAsync(checks, "Create nested label from app", async () =>
                {
                    await viewModel.CreateFolderAsync(childPath);
                    if (!viewModel.Folders.Any(folder => folder.FullName == uniqueRoot)
                        || !viewModel.Folders.Any(folder => folder.FullName == childPath))
                    {
                        throw new InvalidOperationException("The new parent and child were not both present in the app model.");
                    }

                    return "The app created and displayed both the parent and nested child label.";
                });

                await RunCheckAsync(checks, "Created labels appear in Gmail", async () =>
                {
                    var folders = await gmail.GetFoldersAsync(cancellationToken);
                    if (!folders.Any(folder => folder.FullName == uniqueRoot)
                        || !folders.Any(folder => folder.FullName == childPath))
                    {
                        throw new InvalidOperationException("Gmail did not return both newly created labels.");
                    }

                    return "Gmail returned both labels after the app created them.";
                });

                await RunCheckAsync(checks, "Rename label hierarchy from app", async () =>
                {
                    var root = viewModel.Folders.FirstOrDefault(folder => folder.FullName == uniqueRoot)
                        ?? throw new InvalidOperationException("The parent label was not present before rename.");
                    await viewModel.RenameFolderAsync(root, $"{root.Name} renamed");
                    if (!viewModel.Folders.Any(folder => folder.FullName == renamedRoot)
                        || !viewModel.Folders.Any(folder => folder.FullName == renamedChildPath)
                        || viewModel.Folders.Any(folder => folder.FullName == uniqueRoot || folder.FullName == childPath))
                    {
                        throw new InvalidOperationException("The renamed parent and child paths were not reflected in the app model.");
                    }

                    var gmailFolders = await gmail.GetFoldersAsync(cancellationToken);
                    if (!gmailFolders.Any(folder => folder.FullName == renamedRoot)
                        || !gmailFolders.Any(folder => folder.FullName == renamedChildPath)
                        || gmailFolders.Any(folder => folder.FullName == uniqueRoot || folder.FullName == childPath))
                    {
                        throw new InvalidOperationException("Gmail did not reflect the renamed label hierarchy.");
                    }

                    return "The app and Gmail both reflected the parent rename and nested child path.";
                });

                await DeleteAndVerifyAsync(viewModel, gmail, checks, renamedChildPath, cancellationToken);
                await DeleteAndVerifyAsync(viewModel, gmail, checks, renamedRoot, cancellationToken);
            }
            finally
            {
                await CleanupLabelAsync(gmail, childPath, CancellationToken.None);
                await CleanupLabelAsync(gmail, uniqueRoot, CancellationToken.None);
                await CleanupLabelAsync(gmail, renamedChildPath, CancellationToken.None);
                await CleanupLabelAsync(gmail, renamedRoot, CancellationToken.None);
            }
        }

        var report = new AppSelfTestReport(
            startedAt,
            DateTimeOffset.Now,
            checks.Count > 0 && checks.All(check => check.Status != "Failed"),
            MailboxMutationsPerformed: true,
            checks);

        Directory.CreateDirectory(appDataPath);
        await File.WriteAllTextAsync(
            LabelLifecycleReportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        return report;
    }

    public static async Task<AppSelfTestReport> RunMailActionLifecycleAsync(
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.Now;
        var checks = new List<AppSelfTestCheck>();
        var appDataPath = Path.GetDirectoryName(MailActionReportPath)!;
        var context = await CreateSelfTestContextAsync(appDataPath, cancellationToken);
        var gmail = context.Gmail;
        GmailAccountProfile? profile = null;
        string? draftId = null;

        await RunCheckAsync(checks, "Connect with packaged OAuth client", async () =>
        {
            profile = await gmail.TryReconnectAsync(cancellationToken)
                ?? throw new InvalidOperationException("No encrypted Gmail session was found for this Windows account.");
            return $"Connected to {MaskEmail(profile.EmailAddress)}.";
        });

        if (profile is not null)
        {
            try
            {
                var subject = $"GLook reversible draft verification {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
                await RunCheckAsync(checks, "Create Gmail draft", async () =>
                {
                    var result = await gmail.SaveDraftAsync(
                        new ComposeMailRequest
                        {
                            To = profile.EmailAddress,
                            Subject = subject,
                            BodyText = "This unsent draft was created by GLook's reversible mail-action self-test."
                        },
                        cancellationToken: cancellationToken);
                    draftId = result.DraftId;
                    if (string.IsNullOrWhiteSpace(draftId))
                    {
                        throw new InvalidDataException("Gmail did not return a draft ID.");
                    }

                    return "Gmail created an unsent draft without sending mail.";
                });

                if (!string.IsNullOrWhiteSpace(draftId))
                {
                    await RunCheckAsync(checks, "Read and update draft with attachment", async () =>
                    {
                        var current = await gmail.GetDraftAsync(draftId, cancellationToken);
                        if (current.DraftId != draftId)
                        {
                            throw new InvalidDataException("Gmail returned a different draft than requested.");
                        }

                        var updated = await gmail.SaveDraftAsync(
                            new ComposeMailRequest
                            {
                                To = profile.EmailAddress,
                                Subject = subject + " updated",
                                BodyText = "Updated draft body with a small text attachment.",
                                Attachments =
                                [
                                    new MailAttachmentInput(
                                        "glook-verification.txt",
                                        Encoding.UTF8.GetBytes("GLook attachment verification"),
                                        "text/plain")
                                ]
                            },
                            draftId,
                            cancellationToken);
                        if (updated.DraftId != draftId)
                        {
                            throw new InvalidDataException("Updating the draft did not preserve its Gmail draft ID.");
                        }

                        return "Gmail retrieved and replaced the same draft with MIME attachment content.";
                    });

                    await RunCheckAsync(checks, "Delete Gmail draft", async () =>
                    {
                        await gmail.DeleteDraftAsync(draftId, cancellationToken);
                        try
                        {
                            await gmail.GetDraftAsync(draftId, cancellationToken);
                        }
                        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
                        {
                            draftId = null;
                            return "The temporary draft no longer exists in Gmail.";
                        }

                        throw new InvalidOperationException("The temporary draft remained in Gmail after deletion.");
                    });
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(draftId))
                {
                    try
                    {
                        await gmail.DeleteDraftAsync(draftId, CancellationToken.None);
                    }
                    catch
                    {
                        // The report contains the original failure; cleanup is best effort and draft-ID scoped.
                    }
                }
            }
        }

        var report = new AppSelfTestReport(
            startedAt,
            DateTimeOffset.Now,
            checks.Count > 0 && checks.All(check => check.Status != "Failed"),
            MailboxMutationsPerformed: true,
            checks);

        Directory.CreateDirectory(appDataPath);
        await File.WriteAllTextAsync(
            MailActionReportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        return report;
    }

    private static async Task<SelfTestContext> CreateSelfTestContextAsync(
        string appDataPath,
        CancellationToken cancellationToken)
    {
        var secureRoot = Path.Combine(appDataPath, "secure");
        var registry = new AccountRegistry(secureRoot);
        var account = (await registry.GetAccountsAsync(cancellationToken)).FirstOrDefault();
        var secureStore = new EncryptedDataStore(account is null
            ? secureRoot
            : registry.GetCredentialDirectory(account.AccountId));
        var localStore = new LocalMailStore(Path.Combine(appDataPath, "mail.db"));
        var accountScope = account?.AccountId.ToString("D");
        if (accountScope is not null)
        {
            localStore.SetAccountScope(accountScope);
        }

        return new SelfTestContext(
            new GmailClientService(secureStore),
            localStore,
            new SignatureSettingsStore(secureStore),
            accountScope);
    }

    private static async Task RunCheckAsync(
        ICollection<AppSelfTestCheck> checks,
        string name,
        Func<Task<string>> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var detail = await operation();
            checks.Add(new AppSelfTestCheck(name, "Passed", detail, stopwatch.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            checks.Add(new AppSelfTestCheck(
                name,
                "Failed",
                ex.GetBaseException().Message,
                stopwatch.ElapsedMilliseconds));
        }
    }

    private static void AddSkippedGmailChecks(ICollection<AppSelfTestCheck> checks)
    {
        foreach (var name in new[]
        {
            "Gmail labels load",
            "Inbox summaries load",
            "Conversation content loads read-only",
            "Local mirror round-trip"
        })
        {
            checks.Add(new AppSelfTestCheck(
                name,
                "Skipped",
                "A stored Gmail session is required for this check.",
                0));
        }
    }

    private static async Task DeleteAndVerifyAsync(
        MainViewModel viewModel,
        GmailClientService gmail,
        ICollection<AppSelfTestCheck> checks,
        string fullName,
        CancellationToken cancellationToken)
    {
        await RunCheckAsync(checks, $"Delete {fullName} from app", async () =>
        {
            var folder = viewModel.Folders.FirstOrDefault(item => item.FullName == fullName)
                ?? throw new InvalidOperationException("The label was not present in the app model before deletion.");
            viewModel.SelectedFolder = folder;
            await viewModel.DeleteSelectedFolderAsync();

            if (viewModel.Folders.Any(item => item.FullName == fullName))
            {
                throw new InvalidOperationException("The deleted label remained in the app model.");
            }

            var gmailFolders = await gmail.GetFoldersAsync(cancellationToken);
            if (gmailFolders.Any(item => item.FullName == fullName))
            {
                throw new InvalidOperationException("The deleted label remained in Gmail.");
            }

            return "The label disappeared from both the app model and Gmail.";
        });
    }

    private static async Task CleanupLabelAsync(
        GmailClientService gmail,
        string fullName,
        CancellationToken cancellationToken)
    {
        try
        {
            var folder = (await gmail.GetFoldersAsync(cancellationToken))
                .FirstOrDefault(item => item.FullName == fullName && item.CanDelete);
            if (folder is not null)
            {
                await gmail.DeleteFolderAsync(folder.Id, cancellationToken);
            }
        }
        catch
        {
            // The report contains the original failure; cleanup is best effort and exact-name scoped.
        }
    }

    private static string MaskEmail(string email)
    {
        var separator = email.IndexOf('@');
        if (separator <= 0)
        {
            return "the connected account";
        }

        return $"{email[0]}***{email[separator..]}";
    }

    private sealed record SelfTestContext(
        GmailClientService Gmail,
        LocalMailStore LocalStore,
        SignatureSettingsStore SignatureStore,
        string? AccountScope);
}
