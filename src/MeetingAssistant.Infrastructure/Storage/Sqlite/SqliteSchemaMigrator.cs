using Microsoft.Data.Sqlite;

namespace MeetingAssistant.Infrastructure.Storage.Sqlite;

/// <summary>
/// Lleva el esquema de la base desde la versión que tenga hasta la última,
/// aplicando en orden los pasos que falten.
///
/// Usa <c>PRAGMA user_version</c>, un entero que SQLite guarda en la cabecera
/// del archivo, en vez de una tabla de migraciones propia: no hay que crear
/// nada para poder leer el estado, y una base recién creada arranca en 0 sola.
/// Es lo que permite no meter EF Core sólo por tener migraciones.
///
/// Cada paso corre en una transacción junto con su propio bump de versión, así
/// que un fallo a mitad deja la base en la versión anterior, entera. Nunca a
/// medias.
/// </summary>
public sealed class SqliteSchemaMigrator
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteSchemaMigrator(SqliteConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    /// <summary>
    /// Los pasos, en orden. <b>Nunca se edita uno ya publicado</b> — se agrega
    /// el siguiente. Editar el v1 después de que alguien lo aplicó significa que
    /// su base y la del código dejan de coincidir sin que nada avise.
    /// </summary>
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, Version1Sql)
    ];

    public static int LatestVersion => Migrations[^1].Version;

    /// <summary>
    /// Aplica lo que falte y devuelve una línea para el log de diagnóstico.
    /// </summary>
    public string Migrate()
    {
        _connectionFactory.EnsureDirectoryExists();

        using SqliteConnection connection = _connectionFactory.Open();
        int current = ReadUserVersion(connection);
        if (current >= LatestVersion) return $"esquema ya en v{current}, sin cambios";

        int applied = 0;
        foreach ((int version, string sql) in Migrations)
        {
            if (version <= current) continue;

            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // user_version no admite parámetros — es un PRAGMA, no una
                // consulta. Interpolar es seguro sólo porque el valor es un int
                // constante definido acá arriba, nunca entrada externa.
                command.CommandText = $"{sql}\npragma user_version = {version};";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            applied++;
        }

        return $"esquema migrado de v{current} a v{LatestVersion} ({applied} paso(s))";
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "pragma user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// v1 — sesiones, transcripts, reportes y ajustes.
    ///
    /// Notas de diseño que no se ven en el SQL:
    ///
    /// - <b>Las fechas van en TEXT ISO-8601 UTC.</b> Ordenan bien
    ///   lexicográficamente y se leen a ojo en DB Browser, que fue parte de por
    ///   qué se eligió SQLite. Y son UTC a propósito: hoy el .wav se nombra en
    ///   hora local y el reporte en UTC, así que una reunión de la noche parece
    ///   del día siguiente. Guardar UTC y convertir sólo al mostrar corta eso de
    ///   raíz.
    /// - <b>El costo va en micro-dólares enteros</b>, no en REAL. Guardar dinero
    ///   en punto flotante es pedir deriva justo en la métrica que Fase 4 quiere
    ///   sumar. La granularidad de 1e-6 no pierde nada: es exactamente la que ya
    ///   muestra el frontmatter (<c>cost-usd</c> se renderiza con F6).
    /// - <b>Una sesión puede tener varios reportes.</b> El catálogo ya permite
    ///   re-correr el mismo transcript con otro prompt, y hoy eso se pierde.
    /// - <b>structured_json es una columna, no un juego de tablas.</b> Sólo
    ///   assignment-meeting produce un MeetingReport estructurado; los demás
    ///   prompts dan Markdown suelto. Normalizar TaskItem/Insights pelearía con
    ///   el diseño del catálogo.
    /// - <b>vault_path deja el .md del vault como lo que es</b>: una
    ///   exportación, no el registro. El vault sigue siendo el producto.
    /// </summary>
    private const string Version1Sql =
        """
        create table session (
            id                integer primary key autoincrement,
            started_at_utc    text    not null,
            ended_at_utc      text,
            audio_path        text,
            duration_seconds  real,
            source            text    not null
        );

        create index ix_session_started on session(started_at_utc desc);

        create table transcript (
            session_id      integer primary key references session(id) on delete cascade,
            text            text    not null,
            provider        text,
            model           text,
            cost_micro_usd  integer,
            created_at_utc  text    not null
        );

        create table report (
            id               integer primary key autoincrement,
            session_id       integer not null references session(id) on delete cascade,
            prompt_id        text    not null,
            prompt_version   text,
            markdown         text    not null,
            structured_json  text,
            llm_provider     text,
            llm_model        text,
            tokens_input     integer,
            tokens_output    integer,
            cost_micro_usd   integer,
            vault_path       text,
            created_at_utc   text    not null
        );

        create index ix_report_session on report(session_id);
        create index ix_report_created on report(created_at_utc desc);
        create index ix_report_prompt   on report(prompt_id, prompt_version);

        create table setting (
            key             text primary key,
            value           text,
            is_secret       integer not null default 0,
            updated_at_utc  text    not null
        );

        create virtual table transcript_fts using fts5(
            text,
            content='transcript',
            content_rowid='session_id',
            tokenize='unicode61 remove_diacritics 2'
        );

        create trigger transcript_ai after insert on transcript begin
            insert into transcript_fts(rowid, text) values (new.session_id, new.text);
        end;

        create trigger transcript_ad after delete on transcript begin
            insert into transcript_fts(transcript_fts, rowid, text)
                values ('delete', old.session_id, old.text);
        end;

        create trigger transcript_au after update on transcript begin
            insert into transcript_fts(transcript_fts, rowid, text)
                values ('delete', old.session_id, old.text);
            insert into transcript_fts(rowid, text) values (new.session_id, new.text);
        end;
        """;
}
