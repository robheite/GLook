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

    public async Task StoreAsync<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var json = JsonConvert.SerializeObject(value);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        var path = GetPath(key);
        var backupPath = GetBackupPath(path);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(protectedBytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = GetPath(key);
        DeleteIfPresent(path);
        DeleteIfPresent(GetBackupPath(path));
        DeleteTemporaryFiles(path);

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

        foreach (var file in Directory.EnumerateFiles(rootPath, "*.bin.bak"))
        {
            File.Delete(file);
        }

        foreach (var file in Directory.EnumerateFiles(rootPath, "*.bin.*.tmp"))
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    public async Task<T> GetAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var path = GetPath(key);
        var backupPath = GetBackupPath(path);
        if (!File.Exists(path) && !File.Exists(backupPath))
        {
            return default!;
        }

        Exception? primaryFailure = null;
        if (File.Exists(path))
        {
            try
            {
                return await ReadAsync<T>(path).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsDataReadFailure(exception))
            {
                primaryFailure = exception;
            }
        }

        if (File.Exists(backupPath))
        {
            try
            {
                return await ReadAsync<T>(backupPath).ConfigureAwait(false);
            }
            catch (Exception backupFailure) when (IsDataReadFailure(backupFailure))
            {
                throw CreateReadException(path, primaryFailure, backupFailure);
            }
        }

        throw CreateReadException(path, primaryFailure);
    }

    private static async Task<T> ReadAsync<T>(string path)
    {
        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(plaintext))!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The decrypted data is not valid JSON.", exception);
        }
    }

    private static bool IsDataReadFailure(Exception exception) =>
        exception is CryptographicException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException;

    private static InvalidDataException CreateReadException(
        string path,
        Exception? primaryFailure,
        Exception? backupFailure = null)
    {
        var failures = new[] { primaryFailure, backupFailure }
            .Where(exception => exception is not null)
            .Cast<Exception>()
            .ToArray();
        Exception? innerException = failures.Length switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures)
        };

        return new InvalidDataException(
            $"The encrypted data for '{Path.GetFileName(path)}' is corrupt or unreadable.",
            innerException);
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteTemporaryFiles(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var temporaryPath in Directory.EnumerateFiles(
                     directory,
                     $"{Path.GetFileName(path)}.*.tmp"))
        {
            File.Delete(temporaryPath);
        }
    }

    private static string GetBackupPath(string path) => $"{path}.bak";

    private string GetPath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(rootPath, $"{hash}.bin");
    }
}
