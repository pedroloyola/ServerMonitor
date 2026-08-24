using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Infrastructure.Persistence;

public sealed class JsonServerRepository(
    ServerStorageOptions storageOptions,
    ILogger<JsonServerRepository> logger) : IServerRepository, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<Server>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(storageOptions.FilePath))
            {
                return [];
            }

            await using var stream = new FileStream(
                storageOptions.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            return await JsonSerializer.DeserializeAsync<List<Server>>(
                stream,
                SerializerOptions,
                cancellationToken) ?? [];
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "The local server configuration is invalid and was ignored.");
            return [];
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "The local server configuration could not be read.");
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAllAsync(
        IReadOnlyCollection<Server> servers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(storageOptions.FilePath)
                ?? throw new InvalidOperationException("The server storage path has no directory.");

            Directory.CreateDirectory(directory);
            var temporaryFile = storageOptions.FilePath + ".tmp";

            try
            {
                await using (var stream = new FileStream(
                    temporaryFile,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        servers,
                        SerializerOptions,
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(temporaryFile, storageOptions.FilePath, overwrite: true);
                logger.LogInformation("Saved {ServerCount} server configurations.", servers.Count);
            }
            finally
            {
                if (File.Exists(temporaryFile))
                {
                    File.Delete(temporaryFile);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
