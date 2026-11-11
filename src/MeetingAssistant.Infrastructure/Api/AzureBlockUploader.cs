using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MeetingAssistant.Core.Abstractions;

namespace MeetingAssistant.Infrastructure.Api;

/// <summary>
/// Sube un archivo a Azure Storage por el enlace SAS de un trabajo (<c>sp=cw</c>), bloque a bloque:
/// Put Block por cada uno y Put Block List al final. Es el <c>BlockUploader</c> del teléfono en C#:
/// mismos ids de bloque (<c>block-%08d</c> en base64, todos del mismo largo), misma versión de la
/// API de Storage. Sin el SDK de Azure a propósito: el enlace ya trae la autorización y bastan dos
/// PUT. <b>El enlace es una credencial: nunca se escribe en un log ni en un mensaje de error.</b>
/// </summary>
public sealed class AzureBlockUploader(HttpClient http) : IAudioUploader
{
    /// <summary>El límite de Azure de bloques confirmados por blob.</summary>
    public const int MaxBlocks = 50_000;

    private const string StorageVersion = "2021-12-02";

    public async Task<BlockUploadOutcome> UploadAsync(
        string uploadUrl,
        string filePath,
        int blockBytes,
        IReadOnlySet<int> alreadySent,
        Func<int, Task> onBlockSent,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockBytes);

        try
        {
            await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true);
            long size = file.Length;
            int count = BlockCount(size, blockBytes);
            if (count > MaxBlocks) throw new InvalidOperationException($"Azure acepta como mucho {MaxBlocks} bloques por blob; hace falta un bloque más grande.");

            byte[] buffer = new byte[blockBytes];
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (alreadySent.Contains(index)) continue;

                long offset = (long)index * blockBytes;
                int length = (int)Math.Min(blockBytes, size - offset);
                file.Position = offset;
                await file.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken);

                using var request = new HttpRequestMessage(HttpMethod.Put, $"{uploadUrl}&comp=block&blockid={Uri.EscapeDataString(BlockId(index))}")
                {
                    Content = new ByteArrayContent(buffer, 0, length)
                };
                request.Headers.Add("x-ms-version", StorageVersion);
                using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);

                switch (response.StatusCode)
                {
                    case HttpStatusCode.Created:
                        await onBlockSent(index);
                        break;
                    case HttpStatusCode.Forbidden:
                        return new BlockUploadOutcome.LinkRejected();
                    default:
                        return new BlockUploadOutcome.Failed((int)response.StatusCode);
                }
            }

            using var commit = new HttpRequestMessage(HttpMethod.Put, $"{uploadUrl}&comp=blocklist")
            {
                Content = new StringContent(BlockListXml(count), Encoding.UTF8)
            };
            commit.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            commit.Headers.Add("x-ms-version", StorageVersion);
            commit.Headers.Add("x-ms-blob-content-type", "audio/wav");
            using HttpResponseMessage committed = await http.SendAsync(commit, cancellationToken);

            return committed.StatusCode switch
            {
                HttpStatusCode.Created => new BlockUploadOutcome.Committed(),
                HttpStatusCode.Forbidden => new BlockUploadOutcome.LinkRejected(),
                _ => new BlockUploadOutcome.Failed((int)committed.StatusCode)
            };
        }
        catch (HttpRequestException exception)
        {
            return new BlockUploadOutcome.Interrupted(exception.HttpRequestError.ToString());
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BlockUploadOutcome.Interrupted("timeout");
        }
    }

    /// <summary>Todos los ids de un blob tienen que medir lo mismo: un contador de ancho fijo, en base64.</summary>
    public static string BlockId(int index) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"block-{index:D8}"));

    public static int BlockCount(long size, int blockBytes) => (int)((size + blockBytes - 1) / blockBytes);

    public static string BlockListXml(int count)
    {
        var builder = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
        for (int index = 0; index < count; index++) builder.Append("<Latest>").Append(BlockId(index)).Append("</Latest>");
        return builder.Append("</BlockList>").ToString();
    }
}
