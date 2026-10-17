# GLook Deep-Dive Review and Test Outbrief

**Review date:** October 17, 2026

**Reviewed revision:** `19aefc61cb80657a9420fa2ef542e86f01c4d73d` (`v1.1.0`)

**Working branch:** `fix/account-notification-persistence`
**Verdict:** **Required fixes implemented locally; disposable-account and clean-machine release validation remain**

## Executive summary

GLook's core direction is sound: it is a native Windows Gmail client, Gmail remains authoritative, per-account data is separated, labels are presented as folders, incremental history synchronization exists, cached summaries survive ordinary restarts, and existing Move, Labels, and Board commands write their changes back to Gmail.

The review found several issues that should be resolved before expanding testing:

1. **The Gmail API unit meter and governor undercount full thread and draft reads.** A full thread currently costs 120 units under Google's current schedule, while GLook accounts for 40. Forty-eight full conversation opens can therefore display 1,920 units while actually consuming 5,760 units.
2. **Disabling automatic sync on every account does not stop automatic sync.** The timer falls back to refreshing the active mailbox when the filtered account list is empty.
3. **A separate Gmail-settings authorization is not bound to the selected mailbox.** If the wrong Google account is chosen during signature authorization, GLook can read or change that other account's signature.
4. **Long-offline or high-volume mailboxes can trigger expensive and destructive cache rebuild behavior.** History pagination is not bounded while it is being read, and a rebuild replaces the deeper local cache with a new 50-thread bootstrap snapshot.
5. **The requested folder-tree drag-and-drop behavior does not exist yet.** Existing menu-based Move, Labels, and Board actions do mirror changes to Gmail, but dragging an email onto a label currently has no source or target wiring.
6. **There are concurrency, persistence, and UI-state races** around message opening, account removal/reauthorization, encrypted account-file writes, notification-setting testing, and WebView lifecycle.

The review began as read-only. The user subsequently approved implementing the complete required-fix set in one pass; the implementation and validation status is recorded below. Existing account-notification persistence work was preserved and extended.

## Implementation update

All required code changes identified by this review have now been implemented in the local working tree:

| Area | Implemented result |
|---|---|
| Gmail units | Format-aware full-message/thread/draft costs, persistent encrypted rolling ledger, transient-read retries, and explicit local-estimate wording |
| History and cache | Page/event/time bounds, resumable per-record history checkpoints, atomic large-record processing under quota pacing, non-destructive bootstrap merge, cleared hydration provenance, and recent-window folder reconciliation |
| Message hydration | Metadata-first bounded message selection; only selected messages are fetched in full; forwarding hydrates only the latest message |
| Account security | Gmail-settings OAuth is staged until signed identity and audience checks match the selected mailbox exactly |
| Account lifecycle | Per-account remote work and its local projection share the same operation gate; removal and reauthorization wait for in-flight work |
| Encrypted storage | Same-directory temporary writes, write-through flush, atomic replace, last-known-good backup, and backup-aware credential migration |
| Automatic sync | No background Gmail work occurs when every mailbox has automatic sync disabled |
| Notification preferences | Per-account values persist independently, dialog closure waits for saves/actions, runtime delivery rechecks persisted policy, and notification activation opens the intended mailbox/conversation |
| Folder drag/drop | Conversation rows can be moved to eligible Gmail labels; Ctrl-drop adds a label; checked multi-selection, nested labels, same-account validation, keyboard-accessible menu equivalents, and stale-drag rejection are included |
| Message/UI state | Stale detail requests cannot win, detail cache is bounded, read/unread and selected states are distinct, checkbox actions are isolated, Delete uses checked selections, and folder counts are projected locally |
| MIME/WebView | Reply-To and declared charsets are respected, attachment bodies are excluded, remote fonts stay blocked, large document construction is off the UI thread, and WebView failure/unload recovery is present |
| Accessibility/layout | Higher light-theme contrast, reduced rigid sizing, UI Automation names/states, keyboard-adjustable/resettable dividers, and responsive account/Board sizing |
| Diagnostics | Privacy-safe JSON export includes account/sync/cache/quota/error state and excludes OAuth tokens and message bodies |
| Packages | Gmail API, HtmlAgilityPack, SQLite, and ProtectedData packages were updated within the existing Windows App SDK generation |

Post-implementation evidence on October 17, 2026:

- Release x64 analyzer build: passed with 0 warnings and 0 errors.
- Formatting and whitespace verification: passed.
- Read-only self-test: passed 15/15 with no mailbox mutations.
- Windows notification delivery: Windows accepted the test and reported it in Notification Center.
- Read-only Sync All integration test: passed; history applied 0 changes and reported 603/6,000 estimated rolling-minute units at the time of the check.
- Known-package vulnerability and deprecation audits: no findings at the time checked.
- Final single-file x64 release candidate: one `GLook.exe`; the packaged executable passed all 15 read-only checks with no mailbox mutations. The immutable v1.2.0 asset size and SHA-256 are recorded in the GitHub release after rebuilding from the release commit.

The remaining boundary is environmental proof, not an unimplemented required code fix: live label/message/signature/destructive mutations were not run against the user's mailbox; disposable-account mutation coverage, Narrator/high-DPI interaction, WebView process-failure interaction, upgrade/rollback, clean-machine installation, and release signing still require their respective test environments.

## Scope and review method

The review covered:

- Gmail authorization, synchronization, mutations, label handling, and quota accounting
- Multi-account state, notification preferences, credential and cache persistence
- Message list, selection, reading surface, Board view, resize behavior, and accessibility
- MIME parsing, HTML sanitization, remote content controls, and Windows notifications
- Package health, build health, self-test coverage, and release/runtime gaps
- The requested email-to-label drag-and-drop mirroring behavior

Four bounded review lanes were used: Gmail/data/quota, account/persistence, WinUI/UI/accessibility, and a formal security baseline. The formal security scan is sealed as scan `f8dffbfe-a7ce-49f6-b128-14fbefe9fc47` with three validated medium-severity findings.

This was deliberately read-only with respect to Gmail. It did not send mail, create or delete labels, modify signatures, empty Trash, or move live messages.

## Automated evidence

| Check | Result | Meaning and limit |
|---|---:|---|
| Release x64 project build with current analyzers | Pass | 0 warnings, 0 errors |
| Formatting verification | Pass | No formatter changes required |
| NuGet vulnerable-package audit, including transitives | Pass | No known vulnerable packages reported at review time |
| NuGet deprecated-package audit | Pass | No deprecated packages reported |
| Read-only packaged self-test | Pass, 15/15 | Verified local cache, silent reconnect, labels, timestamps, HTML controls, account isolation, Board isolation, Windows notification registration state, and read-only Gmail access |
| Conventional unit-test discovery | No test project found | `dotnet test` exited successfully but discovered no normal unit-test suite |
| Live Gmail mutation testing | Not run | A disposable Gmail integration fixture is still required |
| Interactive UI/accessibility/runtime testing | Not run | Narrator, notification activation, WebView failure recovery, high-DPI layout, and clean-machine installation remain unproven |

One solution-level Release x64 invocation could not resolve a matching solution configuration; the direct Release x64 project build passed. This is project/solution configuration hygiene, not a product build failure.

## Required fixes

### P0 — Correct Gmail quota accounting and enforcement

`GmailQuotaGovernor` uses a single 40-unit cost for `threads.get` and 20 units for `drafts.get`. Under Google's current quota schedule:

- `threads.get` is 40 units normally but **120 when `format=full`**.
- `drafts.get` is 20 units normally but **60 for full, raw, or HTML content**.
- `messages.get` is 20 units normally but 60 for full, raw, or HTML content.

GLook explicitly requests full threads for conversation display and forwarding, but records only 40 units. Draft retrieval omits the format, and the current Gmail discovery document defaults that request to full, while GLook records only 20 units. The existing self-test asserts the old 40-unit assumption and therefore passes the wrong contract.

**Impact:** the live status bar can materially understate consumption, the interactive/background governors can permit more work than intended, and users may see throttling even though GLook reports headroom.

**Required change:** make costs format-aware at the exact request site, update the self-test against the official method/format matrix, persist a short rolling ledger across restarts, and label the meter as an estimate generated by this local GLook process. Google Cloud project telemetry remains authoritative across installations.

References: [Gmail API usage limits](https://developers.google.com/workspace/gmail/api/reference/quota), [Gmail API release notes](https://developers.google.com/workspace/gmail/release-notes).

### P0 — Bind Gmail-settings authorization to the intended account

The separate `gmail.settings.basic` authorization used for signatures is accepted without verifying its Google identity against the selected GLook mailbox. The permanent-delete authorization path already performs this type of identity check, demonstrating the correct pattern.

**Impact:** choosing the wrong Google account in the browser can expose or modify that account's signature while the UI appears to target another mailbox.

**Required change:** resolve the authorized credential's primary identity before any SendAs read/write or credential persistence; require an exact normalized match to the expected mailbox; revoke/delete a mismatch and show both the expected and selected account.

### P1 — Respect “automatic sync off” for all accounts

The timer filters accounts to those with automatic sync enabled. When the result is empty, it falls back to refreshing the active mailbox.

**Impact:** an explicit per-account setting is violated, Gmail units continue to be consumed, and notification-related reads may continue when users expect the mailbox to be idle.

**Required change:** when no account is enabled for automatic sync, perform no automatic Gmail work. Manual Sync folder and Sync all must remain available.

### P1 — Bound incremental history while paging

The incremental-history loop follows every page and accumulates all affected thread IDs before applying the 100-thread rebuild threshold.

**Impact:** a busy or long-offline mailbox can use excessive API units, memory, and time before the safeguard acts.

**Required change:** enforce thread, history-event, page, unit, and wall-clock ceilings during pagination. If any ceiling is reached, do not advance the stored history cursor; schedule a bounded reconciliation instead.

### P1 — Preserve the deeper local cache during reconciliation

When history is expired or the changed-thread threshold is exceeded, GLook performs a 50-thread bootstrap and replaces the account cache. The cache replacement deletes previously stored summaries that are not in that small snapshot.

**Impact:** after days offline or heavy activity, locally retained mail can disappear until it is fetched again. This conflicts with the product expectation that configuration and cached mail survive upgrades and only require incremental updating.

**Required change:** reconcile a fresh bounded snapshot into the existing cache, mark unmatched records stale until authoritative deletion/label evidence arrives, and advance provenance/cursors only after a successful transaction. Add explicit retention and storage-limit policies rather than treating a rebuild as cache erasure.

### P1 — Do not download full conversations before applying limits

Conversation display and forwarding request `Format.Full` for the entire Gmail thread. Only after the response is materialized does GLook keep the last messages and enforce its decoded-size limit.

**Impact:** an oversized conversation can consume more quota, network, memory, parsing time, and UI time than the advertised limits allow.

**Required change:** request metadata first, choose the bounded message IDs needed, then hydrate only those messages. Forwarding should fetch only the latest selected message. Add response-size, time, and cancellation guards.

### P1 — Serialize account lifecycle operations with account work

Account removal waits on account gates, but ordinary send, draft, signature, label, and message mutation paths can bypass those gates. Reauthorization also directly replaces account state.

**Impact:** account credentials or services can be disposed/cleared while a remote operation is in flight, creating intermittent failures or writes associated with stale state.

**Required change:** route all account-scoped Gmail work and credential replacement/removal through one account operation scheduler. Account deletion and reauthorization must wait for or explicitly cancel prior operations before replacing state.

### P1 — Make encrypted registry writes atomic and distinguish corruption

The encrypted account/settings file is overwritten directly. Cryptographic or data errors are converted into the same default result as “file does not exist,” after which the registry can appear empty.

**Impact:** interruption or corruption can make all accounts appear lost and can lead to an empty state being persisted over recoverable data.

**Required change:** write to a same-directory temporary file, flush, atomically replace the primary, retain a last-known-good backup, and distinguish missing, corrupt, and unreadable states in both UI and diagnostics.

### P1 — Prevent stale conversation loads from winning

Rapidly opening message A and then B starts overlapping detail requests. There is no selection version or per-selection cancellation check before the result is committed, and each request independently clears the busy state.

**Impact:** A can finish after B and replace the reading pane with the wrong conversation while B remains highlighted.

**Required change:** use a monotonically increasing selection generation plus cancellation. Commit a result and clear its busy state only if it still owns the current generation.

### P1 — Implement actual folder-tree drag-and-drop mirroring

The normal folder tree is not an `AllowDrop` target and the message list does not expose a drag source. No folder/message drag handlers exist. Board “drag” behavior is currently implemented as action/menu logic, not pointer drag-and-drop.

The good news is that the Gmail write-through foundation already exists:

- **Move** calls the Gmail thread-modify path and updates labels.
- **Labels** adds/removes Gmail labels.
- **Board** applies board-label changes through the same Gmail service.

**Recommended behavior:**

- Dragging to a user or nested label defaults to **Move**.
- Holding Ctrl while dropping performs **Add label** without removing the current label.
- If the dragged row is part of a checked selection, operate on all checked messages; otherwise operate only on the dragged row.
- Permit Inbox as a defined move target. Reject category, virtual, and unsafe system targets unless a dedicated command defines their semantics.
- Reject cross-account drops with a clear explanation; cross-account transfer is a separate import/forward feature.
- Provide a keyboard equivalent and screen-reader announcement.
- Use the existing Gmail mutation path, which costs 10 units per thread modification; do not reread the thread solely to perform the drop.

Live proof remains pending because the feature is absent and this review did not mutate the user's mailbox. It should be validated through a fake Gmail transport first and then a disposable Gmail account.

## Gmail unit-use analysis and savings

Google changed Gmail API quotas on May 1, 2026. For new projects, the current per-user/project limit is 6,000 units per minute and the per-project limit is 1,200,000 units per minute. Google's documentation describes a daily threshold of 80 million units and says standard use below that threshold has no additional charge; planned charging above it is separate from the per-minute throttles. GLook's bottom-bar number is therefore a local estimate of active per-minute requests, not a session total, daily total, or authoritative project-wide counter.

References: [Gmail API usage limits](https://developers.google.com/workspace/gmail/api/reference/quota), [Gmail synchronization guidance](https://developers.google.com/workspace/gmail/api/guides/sync).

### Highest-value savings

| Area | Current pattern | Recommended change | Expected benefit |
|---|---|---|---|
| Full conversation reads | Counted as 40 but cost 120 | Format-aware governor; metadata-first bounded hydration | Correct enforcement and up to 80 units of hidden cost identified per full thread read |
| Bootstrap then Inbox refresh | Initial 50 summaries can be followed immediately by another Inbox list plus up to 40 thread reads | Reuse bootstrap summaries for the selected Inbox | Avoid up to about 1,600 duplicate units on cold start |
| Empty Trash safety recheck | Each confirmed Trash message is individually reread before batch deletion | Take a fresh paginated Trash membership snapshot and intersect it with the confirmed IDs | For a 2,782-message Trash, roughly 30 list units instead of about 55,640 individual recheck units, before deletion costs |
| Folder unread counts | Label list plus one `labels.get` per displayed label | Refresh active, expanded, and visible counts first; age the rest | Reduces recurring O(number of labels) work |
| Search | List plus up to 40 thread reads | Debounce and cache identical query/result pages briefly | Avoids repeated 1,610-unit worst-case searches while typing/reopening |
| Send follow-up | Send, then history and metadata refresh | Optimistically project the sent item and reconcile on scheduled history | Usually saves the immediate follow-up read work |
| Label create/rename/delete | Mutation followed by full folder/count refresh | Project the known label change locally and defer non-visible counts | Avoids label-count fan-out after known mutations |
| Repeated opens | Detail cache exists but is unbounded | Add an account-aware size/age LRU | Preserves repeat-open savings without unbounded memory |
| History processing | Good low-cost history polling, but unbounded page traversal | Keep history-based sync; add in-loop budgets and cursor-safe fallback | Maintains low-cost steady state without burst runaway |

The fresh Trash membership snapshot is a design recommendation, not an implemented fix. It preserves the current intent—do not permanently delete items newly placed in Trash or restored after confirmation—at the same snapshot race boundary, but it requires destructive-path integration tests before release.

### Keep polling; do not switch protocols solely for quota

Incremental Gmail history synchronization is the right baseline for a native client. Google's sync guide recommends storing full-sync results and using history for partial updates. Gmail push notifications require a Cloud Pub/Sub-backed service, and Google's push guide still recommends polling for user-owned devices. Moving to IMAP or deploying a backend solely to reduce units is not justified by this review.

References: [Gmail synchronization guidance](https://developers.google.com/workspace/gmail/api/guides/sync), [Gmail push notifications](https://developers.google.com/workspace/gmail/api/guides/push).

### Meter wording

Recommended bottom-bar label:

> Gmail API units/min — this GLook process: 317 / 6,000 estimated

Add a tooltip explaining that the number is a rolling local estimate for the current process/account activity, may reset on restart, and is not the Google Cloud project's authoritative combined usage.

## Additional correctness and reliability findings

### Message and label state

- **Reply can ignore `Reply-To`.** API-only reply composition respects it, but replying after loading conversation detail uses `From` because the local message model does not retain `Reply-To`.
- **Deleted empty labels can reappear after restart.** The in-memory list changes, but the folder snapshot can remain stale behind the 30-minute cache.
- **Unread badges can remain stale after read/unread changes.** Row state updates immediately, but folder counts do not necessarily reconcile until a later refresh.
- **Trash, Spam, and Restore transitions remove cached rows instead of projecting the new system-label state.** This avoids showing a trashed item in an ordinary label, but other local views can remain incomplete until history reconciliation.
- **Partial label-count failures become zero.** Individual failures are swallowed, making “unknown” look like a real zero unread count.
- **Refresh cancellation disposes a token source that can still be observed by the outgoing refresh.** Use ownership-safe cancellation and disposal after completion.

### MIME and message bodies

- Body selection walks MIME parts too simply and assumes UTF-8. It can select an attached `text/html` as the body, mishandle multipart alternatives, and produce mojibake for declared non-UTF-8 charsets.
- The in-memory detail cache has no size or age bound even though a single detail can approach the configured body limit.
- Safe read retries cover quota errors but not transient 500/502/503/504 failures. Add bounded jittered retries only for idempotent reads; do not blindly retry send or destructive actions.

### Notification settings and testing

The current uncommitted notification work is directionally correct: it reads the actual toggle value, serializes per-row saves, performs a persisted lookup at notification time, and fails closed when policy cannot be resolved.

However, the new self-test calls preference methods and the registry directly. It does not drive the WinUI toggle event, dialog completion, application restart, and notification publish path that previously failed. Add an integration test that changes two accounts independently through the same handler path, closes/reopens the view, reloads the registry, and verifies only enabled accounts publish notifications.

Notification activation arguments contain account and thread identifiers, but the activation handler currently only brings the window forward. It does not select the mailbox and open the requested conversation.

### Window and WebView lifecycle

- WebView event handlers are attached when content is shown, but there is no complete failure recovery/unsubscribe/close path. The page unload path stops timers but does not dispose the coordinator/WebView lifecycle comprehensively.
- HTML sanitization and document construction can perform large synchronous work on the UI thread, including a byte copy before navigation.
- “Always download pictures” also permits remote fonts and CSS URLs. Either rename the setting to “remote content” or enforce image-only behavior.

## UI and accessibility findings

- The muted light-theme foreground fails the WCAG AA 4.5:1 contrast target on several commonly used backgrounds, including the selected message background.
- The thread list exposes no selection to UI Automation because `SelectionMode=None`; opened/read/unread state is primarily visual, and row checkboxes use indistinguishable accessible names.
- The Delete keyboard accelerator only checks the singular opened message and can ignore a checkbox-only multi-selection even though toolbar actions operate on checked items.
- Several dialogs/panes have large fixed minimum widths or heights and can clip at 800×600 or high display scaling.
- Resizer thumbs are pointer-discoverable but lack an equivalent keyboard adjustment and a reset-to-default command.
- The active/opened row, checkbox selection, unread state, and keyboard focus should each have distinct visuals rather than sharing one color cue.

Positive controls already present include light/dark theme dictionaries, defensive HTML sanitization and Content Security Policy, disabled WebView scripting/web messages/dialogs/developer tools, navigation cancellation with restricted external-launch schemes, remote-image blocking by default, destructive confirmations, and checkbox-state synchronization for recycled rows.

## Maintainability and architecture recommendations

1. **Introduce an `IGmailTransport` boundary.** A deterministic fake should model pages, history expiry, quota errors, transient errors, identity mismatch, partial mutation failure, and MIME bodies. This enables safe automated testing without real mailbox mutations.
2. **Create a per-account operation scheduler.** Give operations priority, idempotency, estimated unit cost, cancellation semantics, and reconciliation behavior. This becomes the single owner of account lifecycle and quota admission.
3. **Add a durable pending-mutation journal.** Optimistically update local state, record the intended Gmail mutation, reconcile through history, and recover cleanly after a crash or process restart.
4. **Track cache provenance.** Store authoritative history ID, last verification time, stale state, and pending local mutations instead of treating every cached row as equally current.
5. **Bound local storage.** Use account-aware limits for hydrated bodies, attachments, and detail cache entries while preserving summary/offline retention.
6. **Add a diagnostics export.** Include cursor age, last sync result, current error, cache row counts, pending mutations, requests by Gmail method, and estimated units—never tokens or message bodies by default.
7. **Split oversized classes.** `MainPage.xaml.cs`, `GmailClientService.cs`, `MainViewModel.cs`, and the self-test runner currently combine too many responsibilities to test or reason about safely.
8. **Sign and verify releases.** Add release signing, hashes, provenance, and clean-machine installation/update tests before wider distribution.
9. **Localize user-facing strings.** Current literal strings make future language support and consistent terminology harder.

## Package observations

No known vulnerable or deprecated packages were reported. Minor updates were available for Google.Apis.Gmail.v1, HtmlAgilityPack, Microsoft.Data.Sqlite, and ProtectedData. A major Windows App SDK version was also listed, but it should be evaluated as a planned migration rather than applied blindly.

Package updates should follow the reliability fixes and run through the same build, UI, cache migration, and clean-machine checks.

## Test plan required before the next release

### Deterministic automated tests

- Format-specific Gmail quota contracts for every request method used
- No automatic Gmail call when all account auto-sync settings are off
- Wrong-account signature authorization rejection
- Concurrent account removal/reauthorization with sends, drafts, and mutations
- Atomic encrypted-store interruption/corruption recovery
- Multi-page history bounds and cursor retention
- Cache reconciliation that preserves deeper local summaries
- Reply and reply-all with `Reply-To` and aliases
- Folder-count projection after open/read/unread/move/delete/restore
- MIME corpus: alternatives, nested multiparts, attachments, encodings, malformed payloads
- Safe 5xx retry policy and non-retryable mutation behavior
- Rapid A→B→A selection races and cancellation
- Bounded detail-cache eviction
- Drag/drop single and multi-select, Move and Ctrl/Add-label, nested labels, partial failures, invalid targets, keyboard equivalent, and cross-account rejection
- Two-account notification preference persistence through handler, restart, and publish paths

### Disposable Gmail integration tests

- Create a nested label in GLook and verify it in Gmail
- Rename and delete the label, including the confirmed “delete contained mail” behavior
- Drag one and multiple emails to a label; verify Gmail label membership and local reconciliation
- Move versus Add-label semantics
- Read/unread, star, archive, Trash, restore, empty Trash, send, reply, forward, draft, signature, and notification flows
- History expiry, high-volume changes, and quota throttling

### Interactive Windows tests

- Fresh install, upgrade, rollback, and retained DPAPI credentials/cache
- Cold and warm notification delivery plus activation into the correct account/thread
- Narrator, keyboard-only operation, focus order, accessible states, and announcements
- 800×600, 100–200% scaling, light/dark/high-contrast themes
- WebView process failure, app suspend/resume, network loss, and single-instance activation

## Recommended delivery order

1. Correct the quota model and all-disabled auto-sync behavior.
2. Bind signature authorization to the intended account, serialize account operations, and make secure-store writes atomic.
3. Preserve cache depth during reconciliation; bound history paging and full-message hydration.
4. Fix Reply-To, label/count projection, MIME handling, selection races, and bounded caches.
5. Implement folder-tree drag-and-drop on the existing Gmail mutation foundation and validate it with a fake transport plus disposable account.
6. Address accessibility, responsive layout, WebView lifecycle, diagnostics, and class decomposition.
7. Run the full clean-machine/release-signing matrix and publish only after the live integration suite passes.

## Validation boundary

The build, formatting audit, package audit, static review, formal security scan, and 15/15 read-only self-test are meaningful evidence, but they are not proof of live Gmail mutations, real Windows notification delivery, assistive-technology behavior, clean-machine installation, or crash recovery. Those gaps are intentionally listed above rather than inferred as passing.
