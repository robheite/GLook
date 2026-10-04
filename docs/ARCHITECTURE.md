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
- permanent deletion is not implemented.

## Synchronization boundary

The current refresh is a bounded recent-thread sync:

1. Fetch visible system labels and all user labels.
2. List up to 40 threads for the selected label and Gmail search query.
3. Fetch thread metadata with a six-request concurrency cap.
4. Reconcile the selected folder's recent snapshot and store summaries as DPAPI-protected payloads in an account-scoped SQLite cache so cached previews cannot cross Gmail-account boundaries or be read directly from the database.
5. Fetch full message bodies only when the user opens a thread. GLook selects at most the 50 newest messages from metadata, caps displayed body data at 8 MB per message and 24 MB per thread, and displays an omission notice when older or excess content is not loaded.

MIME traversal is iterative and limited to 24 nesting levels and 256 parts. Compose and forwarding allow at most 20 attachments, 15 MB each, and 18 MB total before MIME/base64 overhead. Local files are size-checked before asynchronous reads; remote Gmail attachment sizes are checked before decoding whenever the API supplies a declared size.

If a read refresh fails, GLook falls back to cached summaries and labels the state as a local mirror. Mutating actions are not queued offline yet; they require an active Gmail connection so the UI cannot claim a change was synchronized when it was not.

While GLook is running, a configurable automatic-sync timer (five minutes by default) refreshes the selected folder, reads up to ten unread Inbox conversations, and sends Windows notifications for IDs that appear after the initial baseline. The interval is persisted per Windows user and can be set from 1–60 minutes or disabled. Settings also provides a delivery probe that verifies the Windows notification setting and confirms the test notification appears in Notification Center. This is foreground/minimized-process polling, not closed-app background delivery. A future push implementation should use Gmail history IDs and a server-backed Pub/Sub channel or a persistent local helper.

## Next delivery slices

1. **Incremental sync** — persist Gmail `historyId`, consume history deltas, reconcile external deletes and label changes, paginate older mail, and add a durable offline mutation queue with idempotency keys.
2. **Background experience** — closed-app synchronization, unread badge, startup options, and explicit network/backoff states.
3. **Mail productivity** — reopen existing drafts, bulk selection, snooze, importance, signatures, rules, attachment download, and permanent deletion from Trash.
4. **Productization** — multiple simultaneous accounts, installer/update channel, diagnostics export, accessibility audit, localization, and Google OAuth verification for distribution.

Each slice should preserve the same rule: retained local state is a cache with provenance, while Gmail remains the mailbox of record.
