using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Transcription;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Autotest de los terminos clave de Deepgram: como se lee la lista, que
/// costo suma y donde puede vivir. Offline: no llama a Deepgram (la prueba
/// real es <c>--transcribe-file</c> con <c>Deepgram:Keyterms</c> puesto).
/// </summary>
internal static class KeytermCommands
{
    public static int Verify()
    {
        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        Console.WriteLine("=== Terminos clave de Deepgram ===");

        // --- La lista -----------------------------------------------------------
        Console.WriteLine($"{Environment.NewLine}--- Lectura de la lista ---");
        Check("vacia o en blanco no da ningun termino", DeepgramKeyterms.Parse(null).Count == 0 && DeepgramKeyterms.Parse("  ;\n ").Count == 0);
        Check("separa por ';' y por saltos de linea, y recorta",
            DeepgramKeyterms.Parse(" stored procedure ;endpoint\r\nNeon\n") is ["stored procedure", "endpoint", "Neon"]);
        Check("una coma NO separa: es parte del termino",
            DeepgramKeyterms.Parse("Deepgram, Inc.") is ["Deepgram, Inc."]);
        Check("descarta repetidos sin distinguir mayusculas, y se queda el primero",
            DeepgramKeyterms.Parse("Neon;neon;NEON;Azure") is ["Neon", "Azure"]);
        string tooMany = string.Join(";", Enumerable.Range(1, 150).Select(i => $"term{i}"));
        IReadOnlyList<string> capped = DeepgramKeyterms.Parse(tooMany);
        Check("corta en 100 (Deepgram rechazaria la peticion entera)", capped.Count == 100 && capped[^1] == "term100");
        Check("se guarda en una linea con '; ' y vuelve igual",
            DeepgramKeyterms.Format(["stored procedure", "Neon"]) == "stored procedure; Neon" &&
            DeepgramKeyterms.Parse(DeepgramKeyterms.Format(["stored procedure", "Neon"])) is ["stored procedure", "Neon"]);
        Check("una lista vacia se guarda como nada (borra la clave)", DeepgramKeyterms.Format([]) is null);

        // --- El costo -----------------------------------------------------------
        Console.WriteLine($"{Environment.NewLine}--- Costo ---");
        decimal? Cost(Dictionary<string, string?> values) =>
            new ConfigPricingCostEstimator(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
                .EstimateTranscriptionCostUsd("Deepgram", TimeSpan.FromMinutes(10));

        Check("sin terminos cobra solo el minuto base",
            Cost(new() { ["Pricing:Deepgram:PerMinute"] = "0.0052", ["Pricing:Deepgram:KeytermPerMinute"] = "0.0013" }) == 0.052m);
        Check("con terminos suma el extra por minuto",
            Cost(new() { ["Pricing:Deepgram:PerMinute"] = "0.0052", ["Pricing:Deepgram:KeytermPerMinute"] = "0.0013", ["Deepgram:Keyterms"] = "Neon" }) == 0.065m);
        Check("con terminos y sin precio del extra: null, no un costo incompleto",
            Cost(new() { ["Pricing:Deepgram:PerMinute"] = "0.0052", ["Deepgram:Keyterms"] = "Neon" }) is null);

        // --- Donde vive ---------------------------------------------------------
        Console.WriteLine($"{Environment.NewLine}--- Nube ---");
        Check("la lista puede vivir en la nube (es de las reuniones, no de la maquina)",
            SettingKeyPolicy.CanLiveInCloud(DeepgramKeyterms.SettingKey) && !SettingKeyPolicy.IsSecret(DeepgramKeyterms.SettingKey));
        Check("la API key de Deepgram sigue sin poder", !SettingKeyPolicy.CanLiveInCloud("Deepgram:ApiKey"));

        Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
        return failures == 0 ? 0 : 1;
    }
}
