using System.Text.Json;
using System.Text.Json.Serialization;
using GLook.Models;

namespace GLook.Services;

public sealed class BoardSettingsStore
{
    private const string SettingsFileName = "boards.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string settingsPath;
    private readonly SemaphoreSlim gate = new(1, 1);

    public BoardSettingsStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        settingsPath = Path.Combine(Path.GetFullPath(rootPath), SettingsFileName);
    }

    public static BoardSettingsStore CreateDefault() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GLook",
            "Boards"));

    public async Task<BoardSettingsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(settingsPath))
            {
                return BoardSettingsSnapshot.Empty;
            }

            await using var stream = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var snapshot = await JsonSerializer.DeserializeAsync<BoardSettingsSnapshot>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("The board settings file is empty.");
            BoardConfigurationValidator.Validate(snapshot);
            return snapshot;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The board settings file is not valid JSON.", exception);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        BoardSettingsSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        BoardConfigurationValidator.Validate(snapshot);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(settingsPath)
                ?? throw new InvalidOperationException("The board settings path has no parent directory.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(settingsPath))
            {
                File.Replace(temporaryPath, settingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, settingsPath);
            }

            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            gate.Release();
        }
    }
}
