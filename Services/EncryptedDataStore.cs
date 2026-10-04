using System.Security.Cryptography;
using System.Text;
using Google.Apis.Util.Store;
using Newtonsoft.Json;

namespace GLook.Services;

public sealed class EncryptedDataStore : IDataStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GLook.GmailTokens.v1");
    private readonly string rootPath;

    public EncryptedDataStore(string rootPath)
    {
        this.rootPath = rootPath;
        Directory.CreateDirectory(rootPath);
    }

    public Task StoreAsync<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var json = JsonConvert.SerializeObject(value);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        return File.WriteAllBytesAsync(GetPath(key), protectedBytes);
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = GetPath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        if (!Directory.Exists(rootPath))
        {
            return Task.CompletedTask;
        }

        foreach (var file in Directory.EnumerateFiles(rootPath, "*.bin"))
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    public async Task<T> GetAsync<T>(string key)
    {
        var path = GetPath(key);
        if (!File.Exists(path))
        {
            return default!;
        }

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(plaintext))!;
        }
        catch (CryptographicException)
        {
            return default!;
        }
    }

    private string GetPath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(rootPath, $"{hash}.bin");
    }
}
