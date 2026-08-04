// Spike de captura de audio — Fase 0
// Objetivo: validar que WASAPI loopback (audio de salida/speaker) y captura de
// micrófono funcionan simultáneamente en tu máquina, y producen un .wav utilizable.
//
// Uso: dotnet run -- 15

using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

int durationSeconds = args.Length > 0 && int.TryParse(args[0], out var d) ? d : 15;

string outputDir = Path.Combine(AppContext.BaseDirectory, "spike-output");
Directory.CreateDirectory(outputDir);
string outputPath = Path.Combine(outputDir, $"meeting-spike-{DateTime.Now:yyyyMMdd-HHmmss}.wav");

Console.WriteLine("=== Spike de captura de audio (Fase 0) ===");
Console.WriteLine($"Grabando {durationSeconds} segundos de: loopback (system output) + micrófono default");
Console.WriteLine("Habla algo y reproduce audio de otra fuente (ej. un video) para probar ambas fuentes.");
Console.WriteLine();

using var defaultRenderDevice = GetDefaultDevice(DataFlow.Render);
using var defaultMicDevice = GetDefaultDevice(DataFlow.Capture);
using var loopbackCapture = new WasapiLoopbackCapture(defaultRenderDevice);
using var micCapture = new WasapiCapture(defaultMicDevice);

Console.WriteLine($"Loopback device: {defaultRenderDevice.FriendlyName}");
Console.WriteLine($"Mic device:      {defaultMicDevice.FriendlyName}");
Console.WriteLine();

var loopbackSampleProvider = new WaveInProvider(loopbackCapture).ToSampleProvider();
var micSampleProvider = new WaveInProvider(micCapture).ToSampleProvider();
var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);

ISampleProvider NormalizeToTarget(ISampleProvider input)
{
    var mono = input.WaveFormat.Channels > 1 ? input.ToMono() : input;
    return mono.WaveFormat.SampleRate == targetFormat.SampleRate
        ? mono
        : new WdlResamplingSampleProvider(mono, targetFormat.SampleRate);
}

var normalizedLoopback = NormalizeToTarget(loopbackSampleProvider);
var normalizedMic = NormalizeToTarget(micSampleProvider);

var mixer = new MixingSampleProvider(targetFormat) { ReadFully = true };
mixer.AddMixerInput(normalizedLoopback);
mixer.AddMixerInput(normalizedMic);

var mixedWaveProvider = new SampleToWaveProvider16(mixer);
using var writer = new WaveFileWriter(outputPath, mixedWaveProvider.WaveFormat);

const int blockDurationMilliseconds = 20;
int blockBytes = mixedWaveProvider.WaveFormat.AverageBytesPerSecond * blockDurationMilliseconds / 1000;
var buffer = new byte[blockBytes];

loopbackCapture.StartRecording();
micCapture.StartRecording();

var recordingClock = Stopwatch.StartNew();
var nextProgressReport = TimeSpan.FromSeconds(1);
long nextBlockStartMilliseconds = 0;
Console.WriteLine("Grabando...");

while (recordingClock.Elapsed < TimeSpan.FromSeconds(durationSeconds))
{
    long delayMilliseconds = nextBlockStartMilliseconds - recordingClock.ElapsedMilliseconds;
    if (delayMilliseconds > 0)
    {
        Thread.Sleep((int)delayMilliseconds);
    }

    int bytesRead = mixedWaveProvider.Read(buffer, 0, buffer.Length);
    if (bytesRead > 0)
    {
        writer.Write(buffer, 0, bytesRead);
    }

    if (recordingClock.Elapsed >= nextProgressReport)
    {
        Console.Write(".");
        nextProgressReport += TimeSpan.FromSeconds(1);
    }

    nextBlockStartMilliseconds += blockDurationMilliseconds;
}

loopbackCapture.StopRecording();
micCapture.StopRecording();

Console.WriteLine();
Console.WriteLine($"Listo. Archivo generado: {outputPath}");
Console.WriteLine();
Console.WriteLine("=== Checklist de validación manual ===");
Console.WriteLine("[ ] El archivo .wav se reproduce sin errores");
Console.WriteLine("[ ] Se escucha claramente el audio del speaker (loopback)");
Console.WriteLine("[ ] Se escucha claramente tu voz por el micrófono");
Console.WriteLine("[ ] Ninguna de las dos fuentes se recorta o distorsiona");
Console.WriteLine("[ ] La sincronización entre ambas fuentes es razonable (no hay desfase notorio)");

static MMDevice GetDefaultDevice(DataFlow flow)
{
    var enumerator = new MMDeviceEnumerator();
    return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
}
