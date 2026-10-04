# GLook

GLook is a native Windows mail client that treats Gmail as the source of truth. It is an unpackaged WinUI 3 executable—not a local web server or browser wrapper.

## Current foundation

The first working slice includes:

- native WinUI 3 shell with wide and compact reading layouts;
- Google desktop OAuth flow in the system browser;
- OAuth tokens encrypted with Windows DPAPI for the current Windows user;
- Gmail labels presented as folders;
- parenthesized unread-message totals for Gmail system folders, categories, and user labels;
- Gmail Primary, Promotions, Social, Updates, and Forums categories when Gmail marks them for label-list display;
- nested folder creation through slash-separated Gmail labels;
- guarded label deletion that checks the live Gmail conversation count and, after explicit confirmation, moves contained conversations to Trash before deleting the label;
- adjustable folder, conversation-list, and reading-pane sizes with persistent layout settings;
- reading pane on the right, on the bottom, or hidden, plus comfortable and compact list density;
- local-time message timestamps plus collapsible date/sender groups, configurable sorting, sender pictures, preview text, and a hideable message-list toolbar;
- explicit per-message checkboxes, select-all, and clear-selection support with bulk archive, Trash, read/unread, star, Spam, move, and label actions; a normal row click returns to one selected conversation;
- Inbox and label conversation loading;
- Gmail search within the selected folder;
- sanitized HTML message rendering in a script-disabled WebView2 reading surface, with remote images blocked until the user chooses **Show images** and a safe-text fallback if WebView2 is unavailable;
- bounded conversation, MIME, body, and attachment processing so unusually large mail cannot grow memory without limit;
- new mail, Gmail-threaded reply/reply-all/forward, To/Cc/Bcc, attachments, send, save draft, and discard;
- Gmail send-as signature editing, multiple encrypted GLook-only named signatures, and separate defaults for new messages, replies, and forwards;
- archive, Trash, read/unread, star/unstar, Spam, add/remove labels, and folder-style moves;
- opening an unread conversation marks it read in Gmail;
- Delete moves the conversation to Gmail Trash;
- right-clicking Gmail Trash exposes **Empty Trash…**, which shows the live message count and requires explicit confirmation before permanent deletion;
- DPAPI-encrypted SQLite cache for recent conversation summaries and offline fallback;
- true nested label navigation with preserved expansion and scroll position;
- Windows new-mail notifications while GLook is running, with a test control and configurable automatic sync (five minutes by default);
- light, dark, and high-contrast-aware resources.

This is an application foundation, not yet a complete Outlook replacement. Reopening existing Gmail drafts, snooze, rules, contacts/calendar, offline mutation queues, push/closed-app synchronization, and multiple simultaneous accounts remain future work.

Gmail exposes the active HTML signature for each send-as address through the API, but it does not expose Gmail Web's full library of named signature templates or its per-device default selectors. GLook therefore displays and edits each API-visible send-as signature, preserves its existing HTML when it is left unchanged, and lets users create additional named signatures that are stored locally and encrypted for the connected account. Those extra signatures work in GLook but do not appear in Gmail web. The first visit to **Signatures** requests Google's separate `gmail.settings.basic` permission.

## Connect a Gmail account

This build contains the GLook Google desktop OAuth client configuration. End users do not need to download or select a JSON file:

1. Start GLook and choose **Connect Gmail**.
2. Select the Google account to use.
3. Complete the permission prompt in the system browser.

Developers building from source must place the desktop OAuth JSON at `LocalConfig\GoogleOAuthClient.json`. It is embedded into the compiled assembly and remains excluded from source control. Like all installed-app OAuth credentials, it can still be extracted from a distributed binary, so it must identify the app rather than be treated as a private server secret.

Official references:

- [Gmail API authorization setup](https://developers.google.com/workspace/gmail/api/quickstart/java#authorize_credentials_for_a_desktop_application)
- [OAuth 2.0 for desktop applications](https://developers.google.com/identity/protocols/oauth2/native-app)
- [Enable Google Workspace APIs](https://developers.google.com/workspace/guides/enable-apis)

OAuth user tokens are encrypted under `%LOCALAPPDATA%\GLook\secure` with Windows DPAPI. Disconnecting from GLook removes that encrypted session from the PC without changing Gmail.

Normal mail operations use the narrower `gmail.modify` permission. Gmail requires the full `mail.google.com` scope for immediate permanent deletion, so GLook requests and stores that separate encrypted permission only after the user confirms **Empty Trash** for the first time. The authorized Google profile must match the account already connected to GLook.

## Build and run

Prerequisites are Visual Studio 2026 with the Windows App SDK C# components and .NET 10.

```powershell
dotnet build .\GLook.csproj -c Debug -p:Platform=x64
.\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\GLook.exe
```

The project is configured with `WindowsPackageType=None`, so the development build launches directly as an `.exe`.

## Create a clean distributable

Publish the architecture you intend to distribute:

```powershell
dotnet publish .\GLook.csproj -c Release -p:Platform=x64
```

The publish profiles create `dist\win-x64\GLook.exe` (or the matching x86/ARM64 directory). The executable is self-contained, so the many .NET, WinUI, SQLite, Gmail, MIME, and native runtime DLLs from `bin\` are bundled into one release file. It is intentionally not trimmed because the OAuth, settings, and cache serializers use reflection and trim warnings make a trimmed release unsafe without additional annotations and published-build integration testing.

The single executable still extracts its bundled native/runtime files into Windows' per-user temporary `.net\GLook` cache while running. This keeps the distributed folder clean; it does not mean those required runtime components disappeared. Do not distribute `bin\`, `obj\`, the source tree, or validation screenshots.

This is currently a portable unpackaged build, not a signed installer. A public release still needs code signing plus a tested installer/update channel. The checked-in MSIX manifest is development scaffolding and does not represent a signed package.

## Read-only self-test

Run the built executable with `--self-test` to validate the stored Gmail session without changing the mailbox:

```powershell
.\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\GLook.exe --self-test
```

The self-test verifies the local SQLite cache, Windows notification registration and enabled state, local signature identity, HTML formatting and active-content safeguards, encrypted-session reconnect, Gmail profile, labels, a five-conversation Inbox sample, one conversation-body fetch, and a cache round-trip. It never calls Gmail label creation/deletion, read-state, or Trash endpoints. The JSON result is written to `%LOCALAPPDATA%\GLook\self-test-report.json`.

Use `--self-test-sync-all` for the longer read-only integration check that downloads and caches the ten newest conversations for every Gmail folder and label currently displayed by GLook. A selected-folder sync still loads its normal forty-conversation view. The check writes `%LOCALAPPDATA%\GLook\sync-all-test-report.json` and may take several minutes for accounts with many labels.

To send a real Windows test notification and confirm that Windows retained it in Notification Center, use **Settings > Send test notification** or run:

```powershell
.\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\GLook.exe --self-test-notification
```

The notification test report is written to `%LOCALAPPDATA%\GLook\notification-test-report.json`.

For an explicitly mutating integration check, `--self-test-label-lifecycle` creates a uniquely named temporary parent/child label through the app model, confirms both in Gmail, renames the parent and nested path, then deletes both through the app model and confirms their removal. Exact-name cleanup runs even if a check fails. Its report is written to `%LOCALAPPDATA%\GLook\label-lifecycle-report.json`.

## Context menus

Right-click a folder or label to open it, create a nested label, rename or delete a user label, expand or collapse one branch or the full tree, or sync the selected folder. Gmail system folders remain protected. Before deleting a user label, GLook checks Gmail directly. An empty label can be deleted immediately; a non-empty label requires explicit confirmation that every contained conversation will be moved to Gmail Trash before the label is removed. Right-click Gmail Trash for **Empty Trash…**; GLook shows the current message count and requires a second destructive-action confirmation before calling Gmail's permanent-delete API.

Right-click a conversation for state-aware mail actions: open, reply, reply all, forward, archive, move to Trash, restore from Trash, read or unread, star or unstar, move, apply labels, spam or not spam, and open the conversation in Gmail.

`--self-test-mail-actions` performs a reversible compose integration test: it creates an unsent Gmail draft, reads and updates that same draft with a small MIME attachment, then deletes it and confirms it is gone. It does not send mail. Its report is written to `%LOCALAPPDATA%\GLook\mail-action-report.json`.

## Notifications

GLook registers with the Windows app notification system and automatically refreshes the selected folder and checks unread Inbox mail every five minutes by default while the process is running, including while the window is minimized. Change or disable this interval in **Settings**; supported intervals are 1–60 minutes. The first poll establishes a baseline so existing unread mail does not create a burst of old notifications. Closing GLook stops polling; closed-app delivery will require a future background helper or a server-backed Gmail push channel.

## Gmail behavior contract

| GLook action | Gmail result |
| --- | --- |
| Create `Projects/GLook` | Creates Gmail labels `Projects` and `Projects/GLook` as needed |
| Delete an empty user folder | Deletes the Gmail label |
| Delete a non-empty user folder | Confirms the live conversation count, moves every contained thread to Gmail Trash, then deletes the label |
| Open an unread conversation | Removes the Gmail `UNREAD` label |
| Delete a conversation | Moves the Gmail thread to Trash on all devices |
| Empty Trash | Permanently deletes every message currently carrying Gmail's Trash label after confirmation; this cannot be undone |
| Archive a conversation | Removes the Gmail `INBOX` label |
| Mark as Spam | Applies Gmail Spam behavior to the whole conversation |
| Move to a folder | Adds the target user label or Inbox and removes the current folder label when appropriate |
| Add/remove a label | Updates Gmail labels without treating them as exclusive folders |
| Send or save a draft | Uses Gmail MIME messages and Gmail's draft/send endpoints |
| Search | Sends the query through Gmail search within the selected label |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the synchronization model and roadmap.
