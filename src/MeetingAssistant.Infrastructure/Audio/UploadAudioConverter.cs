using MeetingAssistant.Core.Abstractions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Convierte una grabación a 16 kHz, 16 bits, mono antes de subirla (paso B). La captura del
/// escritorio graba 44,1 kHz 16 bits mono (≈5,3 MB/min, medido en los WAV de esta PC); a 16 kHz son
/// ≈1,9 MB/min, así que el límite de 1 GiB de la API pasa de ≈3,4 h a ≈9,3 h de reunión y la subida
/// pesa 2,76 veces menos. Es el formato que sube el teléfono y que Deepgram ya probó en el spike.
///
/// Lee en flujo (nunca el archivo entero en memoria) con <see cref="AudioFileReader"/>, que abre
/// WAV, MP3 y lo que entienda Media Foundation: también sirve para "Procesar audio existente".
/// </summary>
public sealed class UploadAudioConverter : IAudioConverter
{
    public const int TargetSampleRate = 16_000;

    public Task<AudioConversion> ConvertForUploadAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Convert(sourcePath, targetPath, cancellationToken), cancellationToken);

    private static AudioConversion Convert(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        string sourceFormat = Describe(sourcePath);
        TimeSpan duration;
        using (var reader = new AudioFileReader(sourcePath))
        {
            duration = reader.TotalTime;
            ISampleProvider samples = reader;
            samples = samples.WaveFormat.Channels switch
            {
                1 => samples,
                2 => samples.ToMono(),
                _ => throw new NotSupportedException($"Audio de {samples.WaveFormat.Channels} canales: sólo se convierte mono o estéreo.")
            };
            if (samples.WaveFormat.SampleRate != TargetSampleRate)
            {
                samples = new WdlResamplingSampleProvider(samples, TargetSampleRate);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
            IWaveProvider pcm = samples.ToWaveProvider16();
            using var writer = new WaveFileWriter(targetPath, pcm.WaveFormat);
            byte[] buffer = new byte[pcm.WaveFormat.AverageBytesPerSecond];
            int read;
            while ((read = pcm.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(buffer, 0, read);
            }
        }

        return new AudioConversion(targetPath, duration, new FileInfo(sourcePath).Length, new FileInfo(targetPath).Length, sourceFormat);
    }

    private static string Describe(string path)
    {
        try
        {
            using var wave = new WaveFileReader(path);
            WaveFormat format = wave.WaveFormat;
            return $"{format.SampleRate} Hz, {format.BitsPerSample} bits, {format.Channels} canal(es), {format.Encoding}";
        }
        catch
        {
            return $"{Path.GetExtension(path)} (no WAV)";
        }
    }
}
