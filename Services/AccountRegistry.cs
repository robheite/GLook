using GLook.Models;

namespace GLook.Services;

/// <summary>
/// Persists the list of Gmail accounts independently from their OAuth tokens.
/// The registry and every account credential store are DPAPI-protected by
/// <see cref="EncryptedDataStore"/> for the current Windows user.
/// </summary>
public sealed class AccountRegistry
{
    private const int CurrentSchemaVersion = 1;
    private const string RegistryKey = "gmail-account-registry";
    private static readonly string[] DefaultAccentKeys =
        ["Blue", "Teal", "Violet", "Orange", "Green", "Rose"];

    private readonly EncryptedDataStore registryStore;
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    public AccountRegistry(string secureRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secureRootPath);
        SecureRootPath = Path.GetFullPath(secureRootPath);
        AccountsRootPath = Path.Combine(SecureRootPath, "accounts");
        Directory.CreateDirectory(AccountsRootPath);
        registryStore = new EncryptedDataStore(Path.Combine(SecureRootPath, "account-registry"));
    }

    public string SecureRootPath { get; }

    public string AccountsRootPath { get; }

    public string GetCredentialDirectory(Guid accountId)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("An account ID is required.", nameof(accountId));
        }

        return Path.Combine(AccountsRootPath, accountId.ToString("D"));
    }

    public async Task<IReadOnlyList<GmailAccountRegistration>> GetAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await LoadSnapshotAsync().ConfigureAwait(false);
            return snapshot.Accounts
                .OrderBy(account => account.SortOrder)
                .ThenBy(account => account.EmailAddress, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<GmailAccountRegistration?> GetAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var accounts = await GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        return accounts.FirstOrDefault(account => account.AccountId == accountId);
    }

    public async Task<GmailAccountRegistration?> FindByEmailAsync(
        string emailAddress,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(emailAddress);
        var accounts = await GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        return accounts.FirstOrDefault(account =>
            string.Equals(account.EmailAddress, normalizedEmail, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<GmailAccountRegistration> AddAsync(
        Guid accountId,
        string emailAddress,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("An account ID is required.", nameof(accountId));
        }

        var normalizedEmail = NormalizeEmail(emailAddress);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await LoadSnapshotAsync().ConfigureAwait(false);
            if (snapshot.Accounts.Any(account => account.AccountId == accountId))
            {
                throw new InvalidOperationException($"Account ID {accountId:D} is already registered.");
            }

            if (snapshot.Accounts.Any(account =>
                    string.Equals(account.EmailAddress, normalizedEmail, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"{normalizedEmail} is already connected.");
            }

            var registration = new GmailAccountRegistration(
                accountId,
                normalizedEmail,
                NormalizeDisplayName(displayName, normalizedEmail),
                snapshot.Accounts.Count,
                DefaultAccentKeys[snapshot.Accounts.Count % DefaultAccentKeys.Length],
                AutomaticSyncEnabled: true,
                NotificationsEnabled: true,
                SyncState: GmailAccountSyncState.Ready);
            snapshot.Accounts.Add(registration);
            await SaveSnapshotAsync(snapshot).ConfigureAwait(false);
            return registration;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<GmailAccountRegistration> UpdateAsync(
        GmailAccountRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.AccountId == Guid.Empty)
        {
            throw new ArgumentException("An account ID is required.", nameof(registration));
        }

        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await LoadSnapshotAsync().ConfigureAwait(false);
            var index = snapshot.Accounts.FindIndex(account => account.AccountId == registration.AccountId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Account {registration.AccountId:D} is not registered.");
            }

            // Reordering is a separate all-account operation. Preserve the
            // stored position so an edit made from a stale settings row cannot
            // accidentally undo a newer reorder.
            var normalized = NormalizeRegistration(registration) with
            {
                SortOrder = snapshot.Accounts[index].SortOrder
            };
            if (snapshot.Accounts.Any(account =>
                    account.AccountId != normalized.AccountId
                    && string.Equals(account.EmailAddress, normalized.EmailAddress, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"{normalized.EmailAddress} is already connected.");
            }

            snapshot.Accounts[index] = normalized;
            NormalizeSortOrder(snapshot.Accounts);
            await SaveSnapshotAsync(snapshot).ConfigureAwait(false);
            return snapshot.Accounts.Single(account => account.AccountId == registration.AccountId);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<GmailAccountRegistration> UpdatePreferencesAsync(
        Guid accountId,
        string displayName,
        bool automaticSyncEnabled,
        bool notificationsEnabled,
        CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await LoadSnapshotAsync().ConfigureAwait(false);
            var index = snapshot.Accounts.FindIndex(account => account.AccountId == accountId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Account {accountId:D} is not registered.");
            }

            var current = snapshot.Accounts[index];
            var updated = current with
            {
                DisplayName = NormalizeDisplayName(displayName, current.EmailAddress),
                AutomaticSyncEnabled = automaticSyncEnabled,
                NotificationsEnabled = notificationsEnabled
            };
            snapshot.Accounts[index] = updated;
            await SaveSnapshotAsync(snapshot).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<GmailAccountRegistration> UpdateSyncStatusAsync(
        Guid accountId,
        GmailAccountSyncState syncState,
        DateTimeOffset? lastSuccessfulSyncAt,
        string? lastSyncError,
        CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await LoadSnapshotAsync().ConfigureAwait(false);
            var index = snapshot.Accounts.FindIndex(account => account.AccountId == accountId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Account {accountId:D} is not registered.");
            }

            var updated = snapshot.Accounts[index] with
            {
                SyncState = syncState,
                LastSuccessfulSyncAt = lastSuccessfulSyncAt,
                LastSyncError = string.IsNullOrWhiteSpace(lastSyncError) ? null : lastSyncError.Trim()
            };
            snapshot.Accounts[index] = updated;
            await SaveSnapshotAsync(snapshot).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<GmailAccountRegistration> UpdateConnectionStateAsync(
        Guid accountId,
        GmailAccountSyncState syncState,
        string? lastSyncError,
        CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await LoadSnapshotAsync().ConfigureAwait(false);
            var index = snapshot.Accounts.FindIndex(account => account.AccountId == accountId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Account {accountId:D} is not registered.");
            }

            var updated = snapshot.Accounts[index] with
            {
                SyncState = syncState,
                LastSyncError = string.IsNullOrWhiteSpace(lastSyncError) ? null : lastSyncError.Trim()
            };
            snapshot.Accounts[index] = updated;
            await SaveSnapshotAsync(snapshot).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<bool> RemoveAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await LoadSnapshotAsync().ConfigureAwait(false);
            var removed = snapshot.Accounts.RemoveAll(account => account.AccountId == accountId) > 0;
            if (!removed)
            {
                return false;
            }

            NormalizeSortOrder(snapshot.Accounts);
            await SaveSnapshotAsync(snapshot).ConfigureAwait(false);
            return true;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task SetOrderAsync(
        IReadOnlyList<Guid> accountIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await LoadSnapshotAsync().ConfigureAwait(false);
            if (accountIds.Count != snapshot.Accounts.Count
                || accountIds.Count != accountIds.Distinct().Count()
                || snapshot.Accounts.Any(account => !accountIds.Contains(account.AccountId)))
            {
                throw new ArgumentException(
                    "The order must contain every registered account exactly once.",
                    nameof(accountIds));
            }

            var byId = snapshot.Accounts.ToDictionary(account => account.AccountId);
            snapshot.Accounts.Clear();
            for (var index = 0; index < accountIds.Count; index++)
            {
                snapshot.Accounts.Add(byId[accountIds[index]] with { SortOrder = index });
            }

            await SaveSnapshotAsync(snapshot).ConfigureAwait(false);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private async Task<AccountRegistrySnapshot> LoadSnapshotAsync()
    {
        var snapshot = await registryStore
            .GetAsync<AccountRegistrySnapshot?>(RegistryKey)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return new AccountRegistrySnapshot(CurrentSchemaVersion, []);
        }

        if (snapshot.SchemaVersion is < 1 or > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported account registry schema version {snapshot.SchemaVersion}.");
        }

        snapshot.Accounts ??= [];
        for (var index = 0; index < snapshot.Accounts.Count; index++)
        {
            snapshot.Accounts[index] = NormalizeRegistration(snapshot.Accounts[index]);
        }

        if (snapshot.Accounts.Any(account => account.AccountId == Guid.Empty)
            || snapshot.Accounts.Select(account => account.AccountId).Distinct().Count() != snapshot.Accounts.Count
            || snapshot.Accounts
                .Select(account => account.EmailAddress)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != snapshot.Accounts.Count)
        {
            throw new InvalidDataException("The encrypted account registry contains duplicate or invalid accounts.");
        }

        NormalizeSortOrder(snapshot.Accounts);
        return snapshot;
    }

    private Task SaveSnapshotAsync(AccountRegistrySnapshot snapshot) =>
        registryStore.StoreAsync(RegistryKey, snapshot);

    private static GmailAccountRegistration NormalizeRegistration(GmailAccountRegistration registration)
    {
        var email = NormalizeEmail(registration.EmailAddress);
        var accent = string.IsNullOrWhiteSpace(registration.AccentKey)
            ? DefaultAccentKeys[0]
            : registration.AccentKey.Trim();
        return registration with
        {
            EmailAddress = email,
            DisplayName = NormalizeDisplayName(registration.DisplayName, email),
            AccentKey = accent,
            LastSyncError = string.IsNullOrWhiteSpace(registration.LastSyncError)
                ? null
                : registration.LastSyncError.Trim()
        };
    }

    private static string NormalizeEmail(string emailAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailAddress);
        var normalized = emailAddress.Trim().ToLowerInvariant();
        if (!normalized.Contains('@', StringComparison.Ordinal)
            || normalized.StartsWith('@')
            || normalized.EndsWith('@'))
        {
            throw new ArgumentException("A valid Gmail address is required.", nameof(emailAddress));
        }

        return normalized;
    }

    private static string NormalizeDisplayName(string? displayName, string emailAddress) =>
        string.IsNullOrWhiteSpace(displayName) ? emailAddress : displayName.Trim();

    private static void NormalizeSortOrder(List<GmailAccountRegistration> accounts)
    {
        var ordered = accounts
            .OrderBy(account => account.SortOrder)
            .ThenBy(account => account.EmailAddress, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        accounts.Clear();
        for (var index = 0; index < ordered.Length; index++)
        {
            accounts.Add(ordered[index] with { SortOrder = index });
        }
    }

    private sealed class AccountRegistrySnapshot
    {
        public AccountRegistrySnapshot(int schemaVersion, List<GmailAccountRegistration> accounts)
        {
            SchemaVersion = schemaVersion;
            Accounts = accounts;
        }

        public int SchemaVersion { get; set; }

        public List<GmailAccountRegistration> Accounts { get; set; }
    }
}
