using GLook.Models;

namespace GLook.Services;

/// <summary>
/// Owns account-bound Gmail clients and cache instances. Each session receives
/// its own OAuth store and its own LocalMailStore instance so changing one
/// account's scope cannot redirect another account's in-flight operations.
/// </summary>
public sealed class GmailAccountSessionCoordinator : IAsyncDisposable
{
    private readonly AccountRegistry accountRegistry;
    private readonly string databasePath;
    private readonly Dictionary<Guid, GmailAccountSession> sessions = [];
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private bool isInitialized;
    private bool isDisposed;

    public GmailAccountSessionCoordinator(string appDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataPath);
        var rootPath = Path.GetFullPath(appDataPath);
        Directory.CreateDirectory(rootPath);
        accountRegistry = new AccountRegistry(Path.Combine(rootPath, "secure"));
        databasePath = Path.Combine(rootPath, "mail.db");
    }

    public AccountRegistry Registry => accountRegistry;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (isInitialized)
            {
                return;
            }

            var initializer = new LocalMailStore(databasePath);
            await initializer.InitializeAsync().ConfigureAwait(false);
            _ = await accountRegistry.GetAccountsAsync(cancellationToken).ConfigureAwait(false);
            isInitialized = true;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public Task<IReadOnlyList<GmailAccountRegistration>> GetAccountsAsync(
        CancellationToken cancellationToken = default) =>
        accountRegistry.GetAccountsAsync(cancellationToken);

    /// <summary>
    /// Keeps the original single-account credential files available for an
    /// older GLook build. Files are copied only when the legacy location is
    /// empty, so this never replaces credentials written by another build.
    /// </summary>
    public Task PreserveLegacyCredentialSnapshotAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var credentialDirectory = accountRegistry.GetCredentialDirectory(accountId);
        if (!Directory.Exists(credentialDirectory))
        {
            return Task.CompletedTask;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(
                     credentialDirectory,
                     "*.bin",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = Path.Combine(
                accountRegistry.SecureRootPath,
                Path.GetFileName(sourcePath));
            if (!File.Exists(destinationPath))
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<GmailAccountSession> GetSessionAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (sessions.TryGetValue(accountId, out var existing))
            {
                return existing;
            }

            var registration = await accountRegistry
                .GetAccountAsync(accountId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Account {accountId:D} is not registered.");
            var session = CreateSession(registration);
            sessions.Add(accountId, session);
            return session;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public Task<GmailAccountRegistration> AddAccountAsync(
        CancellationToken cancellationToken = default) =>
        AddAccountCoreAsync(clientSecretsJson: null, cancellationToken);

    public Task<GmailAccountRegistration> AddAccountAsync(
        string clientSecretsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecretsJson);
        return AddAccountCoreAsync(clientSecretsJson, cancellationToken);
    }

    public async Task<GmailAccountProfile?> ReconnectAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(accountId, cancellationToken).ConfigureAwait(false);
        await session.ConnectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await session.Gmail.TryReconnectAsync(cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                await UpdateSessionConnectionStateAsync(
                    session,
                    GmailAccountSyncState.Disconnected,
                    lastSyncError: null,
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (!IsExpectedAccount(session.Registration, profile))
            {
                // A mismatched credential belongs only to this account
                // directory; clearing it cannot disconnect another account.
                await session.Gmail.DisconnectAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"The saved Gmail authorization belongs to {profile.EmailAddress}, not {session.Registration.EmailAddress}.");
            }

            await UpdateSessionConnectionStateAsync(
                session,
                GmailAccountSyncState.Ready,
                lastSyncError: null,
                cancellationToken).ConfigureAwait(false);
            return profile;
        }
        finally
        {
            session.ConnectionGate.Release();
        }
    }

    public async Task UpdateAccountAsync(
        GmailAccountRegistration registration,
        CancellationToken cancellationToken = default)
    {
        var updated = await accountRegistry.UpdateAsync(registration, cancellationToken).ConfigureAwait(false);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.TryGetValue(updated.AccountId, out var session))
            {
                session.UpdateRegistration(updated);
            }
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<GmailAccountRegistration> UpdateAccountPreferencesAsync(
        Guid accountId,
        string displayName,
        bool automaticSyncEnabled,
        bool notificationsEnabled,
        CancellationToken cancellationToken = default)
    {
        var updated = await accountRegistry
            .UpdatePreferencesAsync(
                accountId,
                displayName,
                automaticSyncEnabled,
                notificationsEnabled,
                cancellationToken)
            .ConfigureAwait(false);
        await UpdateCachedSessionRegistrationAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<GmailAccountRegistration> UpdateAccountSyncStatusAsync(
        Guid accountId,
        GmailAccountSyncState syncState,
        DateTimeOffset? lastSuccessfulSyncAt,
        string? lastSyncError,
        CancellationToken cancellationToken = default)
    {
        var updated = await accountRegistry
            .UpdateSyncStatusAsync(
                accountId,
                syncState,
                lastSuccessfulSyncAt,
                lastSyncError,
                cancellationToken)
            .ConfigureAwait(false);
        await UpdateCachedSessionRegistrationAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<bool> RemoveAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var registration = await accountRegistry
            .GetAccountAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        if (registration is null)
        {
            return false;
        }

        var session = await GetSessionAsync(accountId, cancellationToken).ConfigureAwait(false);
        session.CancelPendingOperations();

        // Do not remove the registry entry until account-scoped credentials and
        // cache rows are gone. A failed cleanup can therefore be retried.
        await session.OperationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        await session.ConnectionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await session.Gmail.DisconnectAsync().ConfigureAwait(false);
            await session.MailStore.ClearCurrentAccountAsync().ConfigureAwait(false);
            var legacyStore = new LocalMailStore(databasePath);
            legacyStore.SetAccountScope(registration.EmailAddress);
            await legacyStore.ClearCurrentAccountAsync().ConfigureAwait(false);
            _ = await accountRegistry.RemoveAsync(accountId, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            session.ConnectionGate.Release();
            session.OperationGate.Release();
        }

        await mutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            sessions.Remove(accountId);
            session.Dispose();
        }
        finally
        {
            mutationGate.Release();
        }

        TryDeleteCredentialDirectoryIfEmpty(accountRegistry.GetCredentialDirectory(accountId));
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }

        await mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            foreach (var session in sessions.Values)
            {
                session.Dispose();
            }

            sessions.Clear();
        }
        finally
        {
            mutationGate.Release();
            mutationGate.Dispose();
        }
    }

    private async Task<GmailAccountRegistration> AddAccountCoreAsync(
        string? clientSecretsJson,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var accountId = Guid.NewGuid();
            var credentialDirectory = accountRegistry.GetCredentialDirectory(accountId);
            var gmail = new GmailClientService(new EncryptedDataStore(credentialDirectory));
            var mailStore = CreateBoundMailStore(accountId);
            var temporarySession = new GmailAccountSession(
                new GmailAccountRegistration(
                    accountId,
                    "pending@localhost",
                    "Connecting account",
                    int.MaxValue,
                    "Blue",
                    AutomaticSyncEnabled: true,
                    NotificationsEnabled: true),
                credentialDirectory,
                gmail,
                mailStore);

            try
            {
                var profile = clientSecretsJson is null
                    ? await gmail.ConnectAsync(cancellationToken).ConfigureAwait(false)
                    : await gmail.ConnectAsync(clientSecretsJson, true, cancellationToken).ConfigureAwait(false);
                var duplicate = await accountRegistry
                    .FindByEmailAsync(profile.EmailAddress, cancellationToken)
                    .ConfigureAwait(false);
                if (duplicate is not null)
                {
                    throw new InvalidOperationException(
                        $"{profile.EmailAddress} is already connected as {duplicate.DisplayName}.");
                }

                var registration = await accountRegistry
                    .AddAsync(
                        accountId,
                        profile.EmailAddress,
                        displayName: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                temporarySession.UpdateRegistration(registration);
                sessions.Add(accountId, temporarySession);
                await PreserveLegacyCredentialSnapshotAsync(accountId, cancellationToken)
                    .ConfigureAwait(false);
                return registration;
            }
            catch
            {
                await gmail.DisconnectAsync().ConfigureAwait(false);
                await mailStore.ClearCurrentAccountAsync().ConfigureAwait(false);
                temporarySession.Dispose();
                TryDeleteCredentialDirectoryIfEmpty(credentialDirectory);
                throw;
            }
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private GmailAccountSession CreateSession(GmailAccountRegistration registration)
    {
        var credentialDirectory = accountRegistry.GetCredentialDirectory(registration.AccountId);
        var gmail = new GmailClientService(new EncryptedDataStore(credentialDirectory));
        return new GmailAccountSession(
            registration,
            credentialDirectory,
            gmail,
            CreateBoundMailStore(registration.AccountId));
    }

    private LocalMailStore CreateBoundMailStore(Guid accountId)
    {
        var store = new LocalMailStore(databasePath);
        store.SetAccountScope(accountId.ToString("D"));
        return store;
    }

    private async Task UpdateSessionConnectionStateAsync(
        GmailAccountSession session,
        GmailAccountSyncState syncState,
        string? lastSyncError,
        CancellationToken cancellationToken)
    {
        var updated = await accountRegistry
            .UpdateConnectionStateAsync(
                session.AccountId,
                syncState,
                lastSyncError,
                cancellationToken)
            .ConfigureAwait(false);
        session.UpdateRegistration(updated);
    }

    private async Task UpdateCachedSessionRegistrationAsync(
        GmailAccountRegistration registration,
        CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.TryGetValue(registration.AccountId, out var session))
            {
                session.UpdateRegistration(registration);
            }
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsExpectedAccount(
        GmailAccountRegistration registration,
        GmailAccountProfile profile) =>
        string.Equals(
            registration.EmailAddress,
            profile.EmailAddress,
            StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteCredentialDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (IOException)
        {
            // An empty directory is harmless and can be retried on the next removal.
        }
        catch (UnauthorizedAccessException)
        {
            // Credentials were already removed; do not report account removal as failed.
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(isDisposed, this);
}

public sealed class GmailAccountSession : IDisposable
{
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private bool isDisposed;

    internal GmailAccountSession(
        GmailAccountRegistration registration,
        string credentialDirectory,
        GmailClientService gmail,
        LocalMailStore mailStore)
    {
        Registration = registration;
        CredentialDirectory = credentialDirectory;
        Gmail = gmail;
        MailStore = mailStore;
        SignatureSettings = new SignatureSettingsStore(new EncryptedDataStore(credentialDirectory));
    }

    public GmailAccountRegistration Registration { get; private set; }

    public Guid AccountId => Registration.AccountId;

    public string CredentialDirectory { get; }

    public GmailClientService Gmail { get; }

    public LocalMailStore MailStore { get; }

    public SignatureSettingsStore SignatureSettings { get; }

    public CancellationToken LifetimeToken => lifetimeCancellation.Token;

    internal SemaphoreSlim ConnectionGate { get; } = new(1, 1);

    internal SemaphoreSlim OperationGate { get; } = new(1, 1);

    internal void UpdateRegistration(GmailAccountRegistration registration)
    {
        if (registration.AccountId != AccountId)
        {
            throw new InvalidOperationException("A session cannot be rebound to another account.");
        }

        Registration = registration;
    }

    internal void CancelPendingOperations()
    {
        if (!isDisposed)
        {
            lifetimeCancellation.Cancel();
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
        ConnectionGate.Dispose();
        OperationGate.Dispose();
    }
}
