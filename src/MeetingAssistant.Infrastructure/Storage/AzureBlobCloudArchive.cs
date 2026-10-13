using System.Collections.Concurrent;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MeetingAssistant.Core.Abstractions;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Implementación de <see cref="ICloudArchive"/> sobre Azure Blob Storage.
///
/// Configuración, sección <c>AzureStorage</c>:
/// <list type="bullet">
/// <item><c>ConnectionString</c> — la credencial. Sin ella, archivar queda
/// apagado y nada más cambia.</item>
/// <item><c>AudioContainer</c> y <c>ReportsContainer</c> — opcionales; por
/// defecto <c>meeting-audio</c> y <c>meeting-reports</c>.</item>
/// </list>
///
/// Los contenedores se crean solos la primera vez, <b>privados</b>: sin acceso
/// anónimo. Lo que devuelve <see cref="UploadAsync"/> es la URL del blob sin
/// firma, que sólo sirve con la credencial.
/// </summary>
public sealed class AzureBlobCloudArchive : ICloudArchive
{
    public const string DefaultAudioContainer = "meeting-audio";
    public const string DefaultReportsContainer = "meeting-reports";

    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, bool> _ensuredContainers = new();

    public AzureBlobCloudArchive(IConfiguration configuration) => _configuration = configuration;

    // Se lee en cada uso y no en el constructor: la configuración se puede
    // recargar, y así una credencial agregada después no exige reconstruir nada.
    private string? ConnectionString => _configuration["AzureStorage:ConnectionString"];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

    private string ContainerNameFor(ArchiveKind kind) => kind == ArchiveKind.Audio
        ? _configuration["AzureStorage:AudioContainer"] ?? DefaultAudioContainer
        : _configuration["AzureStorage:ReportsContainer"] ?? DefaultReportsContainer;

    public async Task<long?> GetRemoteSizeAsync(
        ArchiveKind kind,
        string remoteName,
        CancellationToken cancellationToken = default)
    {
        string connectionString = ConnectionString
            ?? throw new InvalidOperationException("Falta AzureStorage:ConnectionString.");

        var blob = new BlobClient(connectionString, ContainerNameFor(kind), remoteName);
        try
        {
            BlobProperties properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            return properties.ContentLength;
        }
        catch (Azure.RequestFailedException exception) when (exception.Status == 404)
        {
            // Sólo "no existe" es null. Cualquier otro fallo —red, credencial—
            // se propaga: quien pregunta para decidir si borra una copia local
            // no puede confundir "no pude preguntar" con "no está".
            return null;
        }
    }

    public async Task<string> UploadAsync(
        ArchiveKind kind,
        string localPath,
        string remoteName,
        CancellationToken cancellationToken = default)
    {
        string connectionString = ConnectionString
            ?? throw new InvalidOperationException("Falta AzureStorage:ConnectionString.");

        string containerName = ContainerNameFor(kind);

        var container = new BlobContainerClient(connectionString, containerName);
        if (_ensuredContainers.TryAdd(containerName, true))
        {
            try
            {
                await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
            }
            catch
            {
                // Que el próximo intento vuelva a probar crear el contenedor.
                _ensuredContainers.TryRemove(containerName, out _);
                throw;
            }
        }

        BlobClient blob = container.GetBlobClient(remoteName);

        // Por bloques: un WAV de una hora pesa unos 320 MB y no puede depender
        // de que una sola petición llegue entera. Con bloques, un corte de red
        // repite un bloque de 8 MB y no el archivo.
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = kind == ArchiveKind.Audio ? "audio/wav" : "text/markdown; charset=utf-8"
            },
            TransferOptions = new StorageTransferOptions
            {
                InitialTransferSize = 8 * 1024 * 1024,
                MaximumTransferSize = 8 * 1024 * 1024,
                MaximumConcurrency = 2
            }
        };

        // Sin condición de "no existe": sobreescribe. El nombre remoto se fija al
        // encolar, así que un reintento tras una subida a medias pisa lo parcial.
        await blob.UploadAsync(localPath, options, cancellationToken);

        return blob.Uri.ToString();
    }
}
