using System.Net;
using Google;
using GLook.Models;

namespace GLook.Services;

internal enum GmailApiMethod
{
    GetProfile,
    HistoryList,
    LabelsList,
    LabelsGet,
    LabelsCreate,
    LabelsDelete,
    LabelsUpdate,
    ThreadsList,
    ThreadsGet,
    ThreadsGetFull,
    ThreadsModify,
    ThreadsTrash,
    ThreadsUntrash,
    MessagesList,
    MessagesGet,
    MessagesGetFull,
    MessageAttachmentsGet,
    MessagesSend,
    MessagesBatchDelete,
    DraftsCreate,
    DraftsDelete,
    DraftsGet,
    DraftsGetFull,
    DraftsSend,
    DraftsUpdate,
    SettingsSendAsList,
    SettingsSendAsUpdate
}

internal enum GmailRequestPriority
{
    Background,
    Interactive
}

/// <summary>
/// Keeps one Gmail account's estimated traffic below the documented rolling-minute
/// limit. The estimate is deliberately conservative and reserves headroom for
/// interactive actions while background synchronization is busy.
/// </summary>
internal sealed class GmailQuotaGovernor
{
    private const string UsageLedgerKey = "gmail-quota-ledger-v1";
    internal const int UserUnitsPerMinute = 6_000;
    internal const int BackgroundUnitsPerMinute = 4_500;
    private const int InteractiveUnitsPerMinute = 5_800;
    private const int MaximumAttempts = 4;
    private static readonly TimeSpan RollingMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RollingDay = TimeSpan.FromDays(1);
    private readonly object usageLock = new();
    private readonly Queue<UsageEntry> minuteUsage = new();
    private readonly Queue<UsageEntry> dayUsage = new();
    private readonly EncryptedDataStore? ledgerStore;
    private readonly SemaphoreSlim ledgerGate = new(1, 1);
    private bool ledgerLoaded;
    private GmailQuotaState state = GmailQuotaState.Normal;

    public GmailQuotaGovernor(EncryptedDataStore? ledgerStore = null)
    {
        this.ledgerStore = ledgerStore;
    }

    public event Action<GmailQuotaSnapshot>? SnapshotChanged;

    public GmailQuotaSnapshot Snapshot
    {
        get
        {
            lock (usageLock)
            {
                PruneUsage(DateTimeOffset.UtcNow);
                return CreateSnapshot();
            }
        }
    }

    public void MarkRebuildRequired() => SetState(GmailQuotaState.RebuildRequired);

    public async Task<T> ExecuteAsync<T>(
        GmailApiMethod method,
        GmailRequestPriority priority,
        Func<Task<T>> operation,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var units = UnitsFor(method);

        for (var attempt = 0; ; attempt++)
        {
            await ReserveAsync(units, priority, status, cancellationToken);
            try
            {
                var result = await operation();
                SetState(GmailQuotaState.Normal);
                return result;
            }
            catch (GoogleApiException ex) when (IsQuotaLimit(ex) && attempt < MaximumAttempts - 1)
            {
                var exponentialSeconds = Math.Min(Math.Pow(2, attempt + 1), 64);
                var delay = TryGetRetryAfter(ex)
                    ?? TimeSpan.FromSeconds(exponentialSeconds)
                        + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1_001));
                SetState(GmailQuotaState.WaitingForGmail);
                status?.Invoke(
                    $"Gmail is temporarily limiting requests. Retrying in {delay.TotalSeconds:0} seconds.");
                await Task.Delay(delay, cancellationToken);
            }
            catch (GoogleApiException ex) when (
                IsTransientServerFailure(ex)
                && IsSafeToRetry(method)
                && attempt < MaximumAttempts - 1)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt + 1), 16))
                    + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 751));
                SetState(GmailQuotaState.WaitingForGmail);
                status?.Invoke(
                    $"Gmail returned a temporary server error. Retrying in {delay.TotalSeconds:0} seconds.");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task ReserveAsync(
        int units,
        GmailRequestPriority priority,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        await EnsureLedgerLoadedAsync(cancellationToken);
        while (true)
        {
            var delay = TimeSpan.Zero;
            GmailQuotaSnapshot snapshot;
            var reserved = false;
            lock (usageLock)
            {
                var now = DateTimeOffset.UtcNow;
                PruneUsage(now);
                var limit = priority == GmailRequestPriority.Background
                    ? BackgroundUnitsPerMinute
                    : InteractiveUnitsPerMinute;
                var used = minuteUsage.Sum(entry => entry.Units);
                if (used + units <= limit)
                {
                    var entry = new UsageEntry(now, units);
                    minuteUsage.Enqueue(entry);
                    dayUsage.Enqueue(entry);
                    state = GmailQuotaState.Normal;
                    snapshot = CreateSnapshot();
                    reserved = true;
                }
                else
                {
                    var oldest = minuteUsage.Peek().Timestamp;
                    delay = oldest + RollingMinute - now
                        + TimeSpan.FromMilliseconds(Random.Shared.Next(100, 751));
                    state = GmailQuotaState.Throttled;
                    snapshot = CreateSnapshot();
                }
            }

            SnapshotChanged?.Invoke(snapshot);
            if (reserved)
            {
                await PersistLedgerAsync(cancellationToken);
                return;
            }

            var pausePoint = priority == GmailRequestPriority.Background
                ? BackgroundUnitsPerMinute
                : InteractiveUnitsPerMinute;
            status?.Invoke(
                $"Quota guard is pacing Gmail requests ({snapshot.RollingMinuteUnits:N0}/{UserUnitsPerMinute:N0} units in the rolling minute; {priority.ToString().ToLowerInvariant()} pause point {pausePoint:N0}). Resuming in {Math.Max(1, delay.TotalSeconds):0} seconds.");
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private async Task EnsureLedgerLoadedAsync(CancellationToken cancellationToken)
    {
        if (ledgerLoaded || ledgerStore is null)
        {
            ledgerLoaded = true;
            return;
        }

        await ledgerGate.WaitAsync(cancellationToken);
        try
        {
            if (ledgerLoaded)
            {
                return;
            }

            IReadOnlyList<UsageEntry>? retained = null;
            try
            {
                retained = await ledgerStore.GetAsync<List<UsageEntry>>(UsageLedgerKey);
            }
            catch (InvalidDataException)
            {
                await ledgerStore.DeleteAsync<List<UsageEntry>>(UsageLedgerKey);
            }

            var now = DateTimeOffset.UtcNow;
            lock (usageLock)
            {
                foreach (var entry in (retained ?? []).Where(entry =>
                             entry.Units > 0 && now - entry.Timestamp >= TimeSpan.Zero && now - entry.Timestamp < RollingDay))
                {
                    dayUsage.Enqueue(entry);
                    if (now - entry.Timestamp < RollingMinute)
                    {
                        minuteUsage.Enqueue(entry);
                    }
                }

                PruneUsage(now);
            }

            ledgerLoaded = true;
        }
        finally
        {
            ledgerGate.Release();
        }
    }

    private async Task PersistLedgerAsync(CancellationToken cancellationToken)
    {
        if (ledgerStore is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await ledgerGate.WaitAsync(cancellationToken);
        try
        {
            UsageEntry[] entries;
            lock (usageLock)
            {
                entries = dayUsage.ToArray();
            }

            await ledgerStore.StoreAsync(UsageLedgerKey, entries);
        }
        finally
        {
            ledgerGate.Release();
        }
    }

    private void SetState(GmailQuotaState newState)
    {
        GmailQuotaSnapshot snapshot;
        lock (usageLock)
        {
            PruneUsage(DateTimeOffset.UtcNow);
            state = newState;
            snapshot = CreateSnapshot();
        }

        SnapshotChanged?.Invoke(snapshot);
    }

    private void PruneUsage(DateTimeOffset now)
    {
        while (minuteUsage.TryPeek(out var entry) && now - entry.Timestamp >= RollingMinute)
        {
            minuteUsage.Dequeue();
        }

        while (dayUsage.TryPeek(out var entry) && now - entry.Timestamp >= RollingDay)
        {
            dayUsage.Dequeue();
        }
    }

    private GmailQuotaSnapshot CreateSnapshot() => new(
        state,
        minuteUsage.Sum(entry => entry.Units),
        dayUsage.Sum(entry => entry.Units),
        BackgroundUnitsPerMinute,
        DateTimeOffset.UtcNow);

    internal static int UnitsFor(GmailApiMethod method) => method switch
    {
        GmailApiMethod.GetProfile => 1,
        GmailApiMethod.HistoryList => 2,
        GmailApiMethod.LabelsList => 1,
        GmailApiMethod.LabelsGet => 1,
        GmailApiMethod.LabelsCreate => 5,
        GmailApiMethod.LabelsDelete => 5,
        GmailApiMethod.LabelsUpdate => 5,
        GmailApiMethod.ThreadsList => 10,
        GmailApiMethod.ThreadsGet => 40,
        GmailApiMethod.ThreadsGetFull => 120,
        GmailApiMethod.ThreadsModify => 10,
        GmailApiMethod.ThreadsTrash => 20,
        GmailApiMethod.ThreadsUntrash => 10,
        GmailApiMethod.MessagesList => 5,
        GmailApiMethod.MessagesGet => 20,
        GmailApiMethod.MessagesGetFull => 60,
        GmailApiMethod.MessageAttachmentsGet => 20,
        GmailApiMethod.MessagesSend => 100,
        GmailApiMethod.MessagesBatchDelete => 50,
        GmailApiMethod.DraftsCreate => 10,
        GmailApiMethod.DraftsDelete => 10,
        GmailApiMethod.DraftsGet => 20,
        GmailApiMethod.DraftsGetFull => 60,
        GmailApiMethod.DraftsSend => 100,
        GmailApiMethod.DraftsUpdate => 15,
        GmailApiMethod.SettingsSendAsList => 1,
        GmailApiMethod.SettingsSendAsUpdate => 100,
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null)
    };

    private static bool IsSafeToRetry(GmailApiMethod method) => method is
        GmailApiMethod.GetProfile
        or GmailApiMethod.HistoryList
        or GmailApiMethod.LabelsList
        or GmailApiMethod.LabelsGet
        or GmailApiMethod.ThreadsList
        or GmailApiMethod.ThreadsGet
        or GmailApiMethod.ThreadsGetFull
        or GmailApiMethod.MessagesList
        or GmailApiMethod.MessagesGet
        or GmailApiMethod.MessagesGetFull
        or GmailApiMethod.MessageAttachmentsGet
        or GmailApiMethod.DraftsGet
        or GmailApiMethod.DraftsGetFull
        or GmailApiMethod.SettingsSendAsList;

    private static bool IsTransientServerFailure(GoogleApiException exception) =>
        exception.HttpStatusCode is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static bool IsQuotaLimit(GoogleApiException exception) =>
        exception.HttpStatusCode == HttpStatusCode.TooManyRequests
        || (exception.HttpStatusCode == HttpStatusCode.Forbidden
            && (exception.Error?.Errors?.Any(error =>
                    string.Equals(error.Reason, "rateLimitExceeded", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(error.Reason, "userRateLimitExceeded", StringComparison.OrdinalIgnoreCase)) == true
                || exception.Message.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase)));

    private static TimeSpan? TryGetRetryAfter(GoogleApiException exception)
    {
        foreach (var key in new[] { "Retry-After", "RetryAfter" })
        {
            if (!exception.Data.Contains(key))
            {
                continue;
            }

            var value = exception.Data[key];
            if (value is TimeSpan delay && delay > TimeSpan.Zero)
            {
                return delay;
            }

            if (double.TryParse(
                    Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var seconds)
                && seconds > 0)
            {
                return TimeSpan.FromSeconds(Math.Min(seconds, 300));
            }

            if (DateTimeOffset.TryParse(
                    Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var retryAt))
            {
                var untilRetry = retryAt - DateTimeOffset.UtcNow;
                return untilRetry > TimeSpan.Zero ? untilRetry : null;
            }
        }

        return null;
    }

    private readonly record struct UsageEntry(DateTimeOffset Timestamp, int Units);
}
