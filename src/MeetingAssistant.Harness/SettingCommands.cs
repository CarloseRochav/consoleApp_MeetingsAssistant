using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Llm;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using MeetingAssistant.Infrastructure.Transcription;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

/// <summary>--set-setting: escribe un ajuste en la base local REAL.</summary>
internal static class SettingCommands
{
    /// <summary>
    /// Escribe (o borra, con el valor omitido) un ajuste en la base <b>real</b>, la
    /// misma que usa la app instalada. El flag de secreto no se pasa por parametro:
    /// lo decide <c>SettingKeyPolicy</c>, igual que en SettingsPage y en el
    /// importador, para que no haya tres respuestas distintas a "esto se cifra".
    ///
    /// Es el complemento de escritura de --verify-db, y existe por la misma razon:
    /// poder tocar la base real sin depender de tener instalado un cliente de SQLite
    /// — y, sobre todo, sin depender de que la app arranque. Un ajuste guardado mal
    /// que impida el arranque se arregla desde aca o con una variable de entorno; la
    /// UI no sirve si la app no abre.
    /// </summary>
    public static int Set(string key, string? value)
    {
        string databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetingAssistant",
            "meetings.db");

        if (!File.Exists(databasePath))
        {
            Console.Error.WriteLine($"No existe la base: {databasePath}");
            return 1;
        }

        var factory = new SqliteConnectionFactory(databasePath);
        ISettingsStore store = new SqliteSettingsStore(factory, new DpapiSecretProtector());
        bool isSecret = SettingKeyPolicy.IsSecret(key);

        store.SetAsync(key, value, isSecret).GetAwaiter().GetResult();

        // Nunca se imprime el valor: esta salida termina en scrollback de terminal y
        // la clave puede ser una credencial.
        Console.WriteLine(value is null || value.Length == 0
            ? $"Borrado '{key}' (vuelve a mandar el valor empaquetado)."
            : $"Guardado '{key}' ({(isSecret ? "cifrado con DPAPI" : "en claro")}), {value.Length} caracter(es).");

        SqliteConnection.ClearAllPools();
        return 0;
    }

}
