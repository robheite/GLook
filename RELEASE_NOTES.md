# GLook v1.1.0

GLook v1.1.0 is a substantial reliability, efficiency, and mail-workflow update to the native Windows Gmail client. Gmail remains the source of truth, while the local encrypted cache now provides a faster and more resilient starting point between builds and syncs.

This remains a prerelease build for hands-on testing. It is a portable, unpackaged, unsigned Windows x64 executable.

## Gmail synchronization and API efficiency

- Reduced conversation-opening calls by loading a complete Gmail thread in one request and reusing cached details where possible.
- Reused validated Gmail sessions instead of repeatedly requesting the account profile.
- Changed mail actions to update the encrypted local cache immediately and defer confirmation to the next history sync, avoiding unnecessary folder-wide rereads.
- Limited label-count calls to labels that are actually displayed.
- Added a live bottom-bar Gmail API usage indicator and clarified that the figure is the active rolling-minute estimate against Google's 6,000 units-per-user-per-project limit.
- Retained the 4,500-unit background-work pause point so foreground actions keep headroom.

## Local cache and account recovery

- Added encrypted folder snapshots and per-folder hydration state to SQLite.
- Loads cached folders and conversations before reconnecting so retained mail remains visible while Gmail changes are checked.
- Migrates legacy and email-keyed cache data into each account's stable account ID without discarding existing encrypted credentials.
- Recovers retained DPAPI-protected account sessions and keeps the previous credential available if reauthorization fails.
- Added SQLite integrity checks with recoverable backup/rebuild behavior for a damaged cache.
- Improved Gmail client and session disposal.

## Reading experience

- Reworked HTML mail rendering around a protected WebView2 document origin so large formatted messages render without the prior blank-pane limitation.
- Keeps scripts and unsafe navigation disabled and preserves the safe-text fallback.
- Added a persistent **Always download external pictures** setting while retaining per-message image controls.
- Preserves the message-list scroll position when opening mail.
- Adds a clear, theme-aware active-conversation highlight.
- Distinguishes unread mail with a tinted row and stronger sender, subject, and time typography; read mail uses the normal weight.

## Selection and mail actions

- Decoupled checkboxes from WinUI's recycled list selection state to prevent stuck or visually stale checks.
- Checkbox selection now drives the bulk-action toolbar consistently, including after scrolling and recycled-row reuse.
- Normal row clicks select and open one conversation; bulk selection remains explicit through checkboxes.
- Clears bulk selection when changing mailbox, folder, or search scope while leaving an opened conversation intact when appropriate.
- Makes Archive, Delete, Read/Unread, Star, Move, and Labels available whenever checked conversations exist.
- Applies mail actions to the local view immediately, then reconciles through Gmail history sync.

## Folder counts and visual states

- Sidebar counts now represent Gmail's unread-message count for Inbox, system folders, categories, and user labels rather than the total number of messages.
- Added light, dark, and high-contrast resources for selected and unread conversation states.

## Notifications and settings

- Registers the embedded GLook icon for Windows notifications.
- Makes test notifications unique, visible, high priority, and replace-safe in Notification Center.
- Retains per-account automatic-sync and notification preferences.

## Authentication safeguards

- Reauthorization verifies that the returned Google identity matches the selected mailbox.
- Restores the prior encrypted token if reauthorization is canceled, fails, or returns the wrong account.

## Validation and security

- Expanded self-tests for retained-cache migration, folder snapshot/hydration behavior, and large HTML documents.
- Updated `System.Security.Cryptography.Pkcs` to 10.0.13 to remove the vulnerable 10.0.0 transitive dependency reported by NuGet.

## Upgrade behavior

Existing `%LOCALAPPDATA%\GLook` settings, encrypted OAuth sessions, signatures, and SQLite cache are retained. The first v1.1.0 start may migrate older cache partitions into the stable per-account layout. No Gmail reconnection should be required for a valid retained session.

The release executable is self-contained and should be run directly; do not distribute the project's `bin` or `obj` folders.
