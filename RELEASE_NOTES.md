# GLook v1.2.0

GLook v1.2.0 is a reliability, security, synchronization-efficiency, and usability release for the native Windows Gmail client. It implements the required fixes from the October 17 deep-dive review while retaining Gmail as the source of truth and preserving existing encrypted local account data.

This remains a prerelease build for hands-on testing. It is a portable, unpackaged, unsigned Windows x64 executable.

The full review, implementation matrix, test evidence, and remaining validation boundaries are documented in [`docs/reviews/2026-10-17-deep-dive-review.md`](docs/reviews/2026-10-17-deep-dive-review.md).

## Per-account notifications and settings persistence

- Fixed the reported multi-account notification-toggle regression. Automatic-sync and Windows-notification settings now persist independently for every Gmail account.
- Reads the actual toggle control state before saving, avoiding the WinUI event-order race that could write the previous value.
- Serializes saves per account and makes **Done** wait for outstanding preference, add-account, removal, and reauthorization actions.
- Keeps the last persisted registration separate from editable UI values, so opening an account action cannot incorrectly mark unsaved changes as saved.
- Rechecks the encrypted persisted notification policy at delivery time and fails closed when the account cannot be resolved.
- Notification activation now selects the originating mailbox and opens the cached conversation when available.
- The notification test verifies that Windows accepted the notification and retained it in Notification Center.

## Gmail synchronization and API-unit efficiency

- Corrected Gmail API unit estimates for full thread, message, and draft reads.
- Added an encrypted rolling unit ledger that survives process restarts and clarifies that the status-bar figure is a local estimate, not authoritative Google Cloud project telemetry.
- Added bounded retries for transient read-only Gmail failures without retrying sends or destructive mutations.
- Reworked history catch-up into resumable checkpoints. Large backlogs advance only through fully processed history records instead of repeatedly rebuilding the same 50-thread snapshot.
- Processes a single oversized atomic Gmail history record intact while the rolling quota governor paces its summary reads.
- Enforces page, event, changed-thread, and wall-clock bounds while reading history.
- Keeps deeper cached summaries during expired-history recovery instead of replacing the account cache with a small bootstrap page.
- Uses versioned folder-hydration provenance. Complete folder pages can be retained; partial 50-thread pages remain eligible for explicit revalidation and use a verified recent-window boundary in the active session.
- Automatic background sync no longer rehydrates a partial selected folder on every timer tick, and performs no Gmail work when every account has automatic sync disabled.
- Empty Trash now confirms current Trash membership through paginated list snapshots rather than individually rereading every message.
- Label unread-count results are cached longer, known send/mutation state is projected locally, and scheduled history reconciliation replaces unnecessary immediate rereads.

## Cache, credentials, and account lifecycle

- Encrypted data files now use same-directory temporary writes, write-through flushing, atomic replacement, and a last-known-good backup.
- Missing, unreadable, and corrupt encrypted data are handled separately instead of treating every failure as an empty registry.
- Legacy credential migration and rollback preservation include both primary `.bin` files and `.bin.bak` recovery files.
- Account-scoped Gmail work and its local cache projection share one operation gate. Removal and reauthorization wait for in-flight message, label, signature, draft, and synchronization work.
- The bounded detail cache now evicts older conversation bodies instead of growing without limit.
- Added privacy-safe diagnostics export covering accounts, synchronization, cache, quota, and error state without OAuth tokens or message bodies.

## Authorization and message safety

- Gmail signature/settings authorization is staged until Google identity, token signature, audience, and normalized email are verified against the selected mailbox.
- Mismatched settings authorization is rejected before the credential can be persisted or used.
- Conversation display is metadata-first and hydrates only the bounded messages needed for the reading pane.
- Forwarding fetches only the latest selected message instead of downloading the complete conversation.
- Reply and reply-all now retain and respect `Reply-To`.
- MIME selection avoids text attachments, honors declared character sets, and keeps decoded body/attachment limits.
- Remote fonts remain blocked even when the user permits remote email images.
- HTML document construction runs away from the UI thread, and WebView process failure/unload paths now provide safe fallback and cleanup.

## Folder drag-and-drop and mail actions

- Conversations can be dragged from the message list to eligible Gmail user labels, nested labels, or Inbox.
- Drop defaults to folder-style **Move**; holding Ctrl performs **Add label** without removing the current folder label.
- A checked multi-selection moves together; otherwise only the dragged row is changed.
- Cross-account and unsafe system-label targets are rejected.
- Drag payloads use per-drag tokens so canceled or stale drags cannot act on an earlier selection.
- The existing keyboard-accessible Move and Labels commands remain equivalent alternatives.

## UI, accessibility, and resilience

- Rapid A-to-B conversation opening now uses cancellation and a selection generation so an older request cannot replace the active message.
- Read, unread, active, checked, focused, and hover states have distinct presentation.
- Delete and toolbar commands use the checked action selection consistently.
- Folder unread counts and Trash/Spam/restore transitions are projected immediately in the local mirror.
- Improved light-theme muted-text contrast and reduced rigid dialog/Board sizing.
- Added UI Automation names and help text for account cards, folder rows, conversation rows, checkboxes, Board controls, status, and commands.
- Pane dividers support keyboard resizing and double-click reset in addition to pointer dragging.
- WebView failure recovery, notification activation, and account-dialog completion paths are now explicit.

## Dependency maintenance

- Updated Google.Apis.Gmail.v1 to 1.77.0.4295.
- Updated HtmlAgilityPack to 1.13.1.
- Updated Microsoft.Data.Sqlite and System.Security.Cryptography.ProtectedData to 10.0.13.
- Kept the existing Windows App SDK generation to avoid combining these reliability changes with a major framework migration.

## Verification

- Release x64 analyzer build: passed with 0 warnings and 0 errors.
- Formatting and whitespace verification: passed.
- Packaged read-only self-test: passed 15/15 with `MailboxMutationsPerformed=false`.
- Windows notification delivery test: passed and was retained in Notification Center.
- Read-only **Sync All** integration test: passed.
- NuGet vulnerable and deprecated package checks: no findings at publication time.
- Independent regression re-review and scoped security-fix validation: passed with no remaining material finding in the reviewed surfaces.

Live destructive Gmail mutations were intentionally not run against the maintainer's personal account. Disposable-account label/message/signature mutation coverage, Narrator/high-DPI interaction, WebView process-failure interaction, upgrade/rollback on a second machine, code signing, and clean-machine installation remain environmental validation work rather than claims made by this release.

## Upgrade behavior

Existing `%LOCALAPPDATA%\GLook` account settings, encrypted OAuth sessions, signatures, quota ledger, and SQLite cache are retained. Versioned cache-hydration markers invalidate older completion assumptions without deleting retained mail. Valid saved Gmail sessions should reconnect silently; browser OAuth remains limited to explicit Connect or Reauthorize actions.

The release executable is self-contained and should be run directly. Do not distribute the project's `bin` or `obj` folders.
