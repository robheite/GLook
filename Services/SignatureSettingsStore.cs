using GLook.Models;

namespace GLook.Services;

public sealed class SignatureSettingsStore
{
    private readonly EncryptedDataStore secureStore;

    public SignatureSettingsStore(EncryptedDataStore secureStore)
    {
        this.secureStore = secureStore;
    }

    public async Task<SignatureSettingsSnapshot?> LoadAsync(string accountEmail) =>
        await secureStore.GetAsync<SignatureSettingsSnapshot?>(Key(accountEmail));

    public Task SaveAsync(string accountEmail, SignatureSettingsSnapshot snapshot) =>
        secureStore.StoreAsync(Key(accountEmail), snapshot);

    private static string Key(string accountEmail) =>
        $"signature-settings-{accountEmail.Trim().ToUpperInvariant()}";
}
