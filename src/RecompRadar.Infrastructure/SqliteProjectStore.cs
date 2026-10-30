using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.Infrastructure;

public sealed class ProjectStoreException : Exception
{
    public ProjectStoreException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Stores analyses as projects in a local SQLite database. Only metadata and the analysis results are kept
/// (gzip-compressed JSON); the analyzed file itself is never copied.
/// </summary>
public sealed class SqliteProjectStore : IProjectStore
{
    private const int SchemaVersion = 1;
    private const int MaxNameLength = 200;
    private const int MaxNotesLength = 20_000;
    private const long MaxUnpackedBytes = 512L * 1024 * 1024;

    private const string SummaryColumns =
        "id, name, file_name, file_path, file_size, file_sha256, analyzed_at, architecture, file_format, overall_status, hints, notes, report_path";

    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteProjectStore(string databasePath, ILogger<SqliteProjectStore>? logger = null, TimeProvider? timeProvider = null)
    {
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();
    }

    public async Task<ProjectSummary> SaveAsync(AnalysisBundle bundle, string? name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var analysis = bundle.Analysis;
        var now = _time.GetUtcNow();
        var projectName = Truncate(string.IsNullOrWhiteSpace(name) ? analysis.File.FileName : name.Trim(), MaxNameLength);
        if (projectName.Length == 0)
        {
            projectName = "Untitled analysis";
        }

        var summary = new ProjectSummary
        {
            Id = Guid.NewGuid(),
            Name = projectName,
            FileName = analysis.File.FileName,
            FilePath = analysis.File.FullPath,
            FileSizeBytes = analysis.File.SizeBytes,
            Sha256 = analysis.File.Sha256,
            AnalyzedAtUtc = analysis.AnalyzedAtUtc,
            Architecture = analysis.Architecture?.Description ?? "Unknown",
            FileFormat = analysis.Format.DisplayName,
            OverallStatus = bundle.Report.OverallPortingStatus,
            Hints = BuildHints(analysis),
        };

        var analysisBlob = Pack(analysis);
        var reportBlob = Pack(bundle.Report);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Analyzing the same file again (same SHA-256 at the same path) refreshes the existing project instead of
        // piling up copies; its id, name, notes and last report path are kept.
        command.CommandText = """
            INSERT INTO projects (id, name, file_name, file_path, file_size, file_sha256, analyzed_at, architecture, file_format,
                                  overall_status, hints, notes, report_path, analysis_gz, report_gz, created_at, updated_at)
            VALUES ($id, $name, $file_name, $file_path, $file_size, $sha, $analyzed_at, $architecture, $format,
                    $status, $hints, '', NULL, $analysis, $report, $now, $now)
            ON CONFLICT(file_sha256, file_path) DO UPDATE SET
                file_size = excluded.file_size,
                analyzed_at = excluded.analyzed_at,
                architecture = excluded.architecture,
                file_format = excluded.file_format,
                overall_status = excluded.overall_status,
                hints = excluded.hints,
                analysis_gz = excluded.analysis_gz,
                report_gz = excluded.report_gz,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", summary.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", summary.Name);
        command.Parameters.AddWithValue("$file_name", summary.FileName);
        command.Parameters.AddWithValue("$file_path", summary.FilePath);
        command.Parameters.AddWithValue("$file_size", summary.FileSizeBytes);
        command.Parameters.AddWithValue("$sha", summary.Sha256);
        command.Parameters.AddWithValue("$analyzed_at", summary.AnalyzedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$architecture", summary.Architecture);
        command.Parameters.AddWithValue("$format", summary.FileFormat);
        command.Parameters.AddWithValue("$status", summary.OverallStatus);
        command.Parameters.AddWithValue("$hints", summary.Hints);
        command.Parameters.AddWithValue("$analysis", analysisBlob);
        command.Parameters.AddWithValue("$report", reportBlob);
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Read the row back: on a conflict it carries the id and user-edited fields of the existing project.
            await using var read = connection.CreateCommand();
            read.CommandText = $"SELECT {SummaryColumns} FROM projects WHERE file_sha256 = $sha AND file_path = $file_path";
            read.Parameters.AddWithValue("$sha", summary.Sha256);
            read.Parameters.AddWithValue("$file_path", summary.FilePath);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                summary = ReadSummary(reader);
            }
        }
        catch (SqliteException ex)
        {
            throw new ProjectStoreException($"The project could not be saved: {ex.Message}", ex);
        }

        _logger.LogInformation("Project {ProjectId} saved for {FileName} ({Sha256})", summary.Id, summary.FileName, summary.Sha256);
        return summary;
    }

    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SummaryColumns} FROM projects ORDER BY analyzed_at DESC, created_at DESC";
        var result = new List<ProjectSummary>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(ReadSummary(reader));
            }
        }
        catch (SqliteException ex)
        {
            throw new ProjectStoreException($"The project list could not be read: {ex.Message}", ex);
        }

        return result;
    }

    public async Task<ProjectRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SummaryColumns}, analysis_gz, report_gz FROM projects WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var summary = ReadSummary(reader);
            var analysis = Unpack<AnalysisResult>((byte[])reader["analysis_gz"]);
            var report = Unpack<PortabilityReport>((byte[])reader["report_gz"]);
            return new ProjectRecord(summary, new AnalysisBundle(analysis, report));
        }
        catch (SqliteException ex)
        {
            throw new ProjectStoreException($"The project could not be read: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new ProjectStoreException("The stored analysis is damaged and cannot be opened. You can delete this project and analyze the file again.", ex);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync("DELETE FROM projects WHERE id = $id", id, cancellationToken);

    public Task<bool> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = Truncate(name?.Trim() ?? string.Empty, MaxNameLength);
        if (trimmed.Length == 0)
        {
            throw new ProjectStoreException("A project name cannot be empty.");
        }

        return ExecuteUpdateAsync("UPDATE projects SET name = $value, updated_at = $now WHERE id = $id", id, cancellationToken, ("$value", trimmed));
    }

    public Task<bool> UpdateNotesAsync(Guid id, string notes, CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync("UPDATE projects SET notes = $value, updated_at = $now WHERE id = $id", id, cancellationToken,
            ("$value", Truncate(notes ?? string.Empty, MaxNotesLength)));

    public Task<bool> SetReportPathAsync(Guid id, string reportPath, CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync("UPDATE projects SET report_path = $value, updated_at = $now WHERE id = $id", id, cancellationToken, ("$value", reportPath));

    private async Task<bool> ExecuteUpdateAsync(string sql, Guid id, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        if (sql.Contains("$now", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        }

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        catch (SqliteException ex)
        {
            throw new ProjectStoreException($"The project database could not be updated: {ex.Message}", ex);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (SqliteException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new ProjectStoreException($"The project database could not be opened: {ex.Message}", ex);
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(_databasePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode = WAL;";
                await wal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            long version;
            await using (var read = connection.CreateCommand())
            {
                read.CommandText = "PRAGMA user_version;";
                version = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            if (version > SchemaVersion)
            {
                throw new ProjectStoreException($"The project database was created by a newer version of RecompRadar (schema {version}). Please update the application.");
            }

            if (version < 1)
            {
                await using var create = connection.CreateCommand();
                create.CommandText = $"""
                    CREATE TABLE IF NOT EXISTS projects (
                        id TEXT PRIMARY KEY NOT NULL,
                        name TEXT NOT NULL,
                        file_name TEXT NOT NULL,
                        file_path TEXT NOT NULL,
                        file_size INTEGER NOT NULL,
                        file_sha256 TEXT NOT NULL,
                        analyzed_at TEXT NOT NULL,
                        architecture TEXT NOT NULL,
                        file_format TEXT NOT NULL,
                        overall_status TEXT NOT NULL,
                        hints TEXT NOT NULL DEFAULT '',
                        notes TEXT NOT NULL DEFAULT '',
                        report_path TEXT,
                        analysis_gz BLOB NOT NULL,
                        report_gz BLOB NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_identity ON projects(file_sha256, file_path);
                    CREATE INDEX IF NOT EXISTS ix_projects_analyzed_at ON projects(analyzed_at);
                    PRAGMA user_version = {SchemaVersion};
                    """;
                try
                {
                    await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException ex)
                {
                    throw new ProjectStoreException($"The project database could not be created: {ex.Message}", ex);
                }
            }

            _initialized = true;
        }
        catch (SqliteException ex)
        {
            throw new ProjectStoreException($"The project database could not be opened: {ex.Message}", ex);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static ProjectSummary ReadSummary(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        Name = r.GetString(1),
        FileName = r.GetString(2),
        FilePath = r.GetString(3),
        FileSizeBytes = r.GetInt64(4),
        Sha256 = r.GetString(5),
        AnalyzedAtUtc = DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Architecture = r.GetString(7),
        FileFormat = r.GetString(8),
        OverallStatus = r.GetString(9),
        Hints = r.GetString(10),
        Notes = r.GetString(11),
        ReportPath = r.IsDBNull(12) ? null : r.GetString(12),
    };

    private static string BuildHints(AnalysisResult analysis)
    {
        var lines = analysis.Diagnostics
            .Where(d => d.Severity >= Severity.Warning)
            .Take(5)
            .Select(d => $"{d.Severity}: {d.Message}")
            .ToList();
        if (analysis.Platform is { Candidates.Count: > 0 } platform)
        {
            lines.Add(platform.Summary);
        }

        return Truncate(string.Join("\n", lines), 2000);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static byte[] Pack<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, RecompRadarJson.Compact);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(json);
        }

        return output.ToArray();
    }

    private static T Unpack<T>(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxUnpackedBytes)
            {
                throw new InvalidDataException("The stored data is unreasonably large.");
            }

            output.Write(buffer, 0, read);
        }

        output.Position = 0;
        return JsonSerializer.Deserialize<T>(output, RecompRadarJson.Compact) ?? throw new InvalidDataException("The stored data is empty.");
    }
}
