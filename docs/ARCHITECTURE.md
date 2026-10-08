# GLook architecture

## Product rule

Gmail is authoritative. GLook mirrors Gmail state and sends explicit user actions back to Gmail before presenting them as synchronized. The SQLite database is a read cache, not a second mailbox.

This avoids the ambiguity that causes many IMAP clients to duplicate work: Gmail messages can have multiple labels, while traditional mail folders normally imply one physical location.

## Current structure

```text
WinUI shell and dialogs
        |
MainViewModel
        |
        +-- GmailClientService ------ Google Gmail API
        |          |
        |          +-- EncryptedDataStore -- DPAPI-protected OAuth material
        |
        +-- LocalMailStore ---------- DPAPI-encrypted SQLite recent-thread cache
```

- `Views/MainPage.xaml`: native command bar, folder pane, conversation list, reading pane, responsive layout.
- `ViewModels/MainViewModel.cs`: user-visible state, selection, loading, and synchronization status.
- `Services/GmailClientService.cs`: OAuth and Gmail operations.
- `Services/EncryptedDataStore.cs`: per-user DPAPI encryption for Google token persistence.
- `Services/LocalMailStore.cs`: disposable, per-user DPAPI-encrypted local cache under `%LOCALAPPDATA%\GLook`; only purpose-scoped account/thread hashes and encrypted payloads are stored in SQLite.
- `Services/WindowsNotificationService.cs`: Windows App SDK notification registration and new-mail popup delivery.

## Gmail mapping

System folders use Gmail system label IDs (`INBOX`, `SENT`, `DRAFT`, `SPAM`, `TRASH`, and others). User folders use Gmail user-label IDs. A visual nested path such as `Clients/Contoso` remains one Gmail label name containing `/`; GLook also ensures the parent `Clients` label exists.

Mutations are deliberately conservative:

- deleting a message uses `threads.trash`, so Gmail retention and recovery behavior still applies;
- deleting a folder uses `labels.delete`, which removes the label and keeps the messages;
- reading a thread removes the `UNREAD` label;
- archive removes `INBOX`, Spam uses Gmail's `SPAM` label behavior, and star/read state applies to the conversation;
- folder-style Move is limited to Inbox and user-created labels; dedicated commands handle Trash and Spam;
- compose creates RFC-compliant MIME through MimeKit, saved drafts use Gmail draft IDs, and saved drafts are sent atomically through `drafts.send`;
- permanent deletion is isolated to the Trash folder's explicit **Empty Trash** command, requires a second confirmation, and uses a separately authorized `mail.google.com` client because Gmail does not permit permanent deletion with `gmail.modify`;

## Synchronization boundary

The current refresh is a quota-governed Gmail history sync:

1. Load visible labels on connection and refresh their metadata at a low frequency rather than on every timer tick.
2. Bootstrap a new or expired cache with at most 50 recent mailbox threads.
3. Persist the Gmail `historyId`; subsequent refreshes page `users.history.list` and fetch metadata only for changed threads.
4. Commit changed/deleted threads and cursor advancement in one SQLite transaction. Cached summaries are DPAPI-protected and partitioned by account.
5. Fetch full message bodies only when the user opens a thread. GLook selects at most the 50 newest messages from metadata, caps displayed body data at 8 MB per message and 24 MB per thread, and displays an omission notice when older or excess content is not loaded.

MIME traversal is iterative and limited to 24 nesting levels and 256 parts. Compose and forwarding allow at most 20 attachments, 15 MB each, and 18 MB total before MIME/base64 overhead. Local files are size-checked before asynchronous reads; remote Gmail attachment sizes are checked before decoding whenever the API supplies a declared size.

GLook stores a DPAPI-protected `historyId` beside each account's encrypted cache partition. Ordinary refreshes call `users.history.list`, retrieve only affected thread metadata, and commit thread changes plus cursor advancement in one SQLite transaction. An expired history cursor (HTTP 404) triggers a bounded 50-thread mailbox rebuild; it never causes a Gmail mutation. The per-account quota governor uses Google's published method weights, holds background work below an estimated 4,500 units per rolling minute, reserves capacity for interactive work, and applies exponential backoff with jitter after Gmail rate-limit responses. Its usage is an in-process estimate rather than Google Cloud billing telemetry.

If a read refresh fails, GLook falls back to cached summaries and labels the state as a local mirror. Mutating actions are not queued offline yet; they require an active Gmail connection so the UI cannot claim a change was synchronized when it was not.

While GLook is running, a configurable automatic-sync timer (five minutes by default) checks Gmail history sequentially for every sync-enabled mailbox. Notifications come from Inbox/unread candidates in that same history batch, are tagged with the originating Gmail address, and respect each account's notification preference. There is no duplicate unread-Inbox scan. The interval is persisted per Windows user and can be set from 1–60 minutes or disabled. Settings also provides a delivery probe that verifies the Windows notification setting and confirms the test notification appears in Notification Center. This is foreground/minimized-process polling, not closed-app background delivery. A future push implementation should use Gmail history IDs and a server-backed Pub/Sub channel or a persistent local helper.

Each connected Gmail account has a stable GUID, a DPAPI-protected registry entry, credentials below `secure/accounts/{accountId}`, an account-bound Gmail client, signature store, quota governor, cancellation lifetime, and SQLite cache scope. UI and Board operations carry the account GUID before carrying a Gmail label or thread ID. Removing one account clears only that account's credential directory and cache rows. The legacy single-account token is copied into the first account directory and removed from the old root only after the migrated session reconnects successfully.

The List and Board views project the same local mailbox mirror. A board definition stores editable display headings separately from each account's Gmail-label bindings, so renaming a heading never renames a Gmail label. Board cards expose explicit keyboard-accessible **Move to** and **Add label** commands. The first Board release displays one selected mailbox at a time; cross-account unified boards remain a later slice.

## Next delivery slices

1. **Offline depth** — paginate older mail and add a durable offline mutation queue with idempotency keys.
2. **Background experience** — closed-app synchronization, unread badge, startup options, and explicit network/backoff states.
3. **Mail productivity** — reopen existing drafts, snooze, importance, rules, and attachment download.
4. **Productization** — installer/update channel, diagnostics export, accessibility audit, localization, and Google OAuth verification for distribution.

Each slice should preserve the same rule: retained local state is a cache with provenance, while Gmail remains the mailbox of record.
