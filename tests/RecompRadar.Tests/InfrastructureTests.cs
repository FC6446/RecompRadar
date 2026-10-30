using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Infrastructure;
using RecompRadar.Portability;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

public class SqliteProjectStoreTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AnalysisBundle Bundle(string sample, DateTimeOffset? at = null)
    {
        var bytes = SampleFiles.All().Single(s => s.FileName == sample).Bytes;
        using var dir = new TempDir();
        var path = dir.Write(sample, bytes);
        var time = new FixedTime(at ?? new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        return new AnalysisPipeline(timeProvider: time).AnalyzeAsync(path, new AnalysisOptions()).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Saves_and_reopens_a_complete_analysis()
    {
        using var dir = new TempDir();
        var store = new SqliteProjectStore(dir.File("db.sqlite"));
        var bundle = Bundle("synthetic_x86_64_linux_dynamic.elf");

        var saved = await store.SaveAsync(bundle, "My project");
        var record = await store.GetAsync(saved.Id);

        Assert.NotNull(record);
        var summary = record.Summary;
        Assert.Equal("My project", summary.Name);
        Assert.Equal("synthetic_x86_64_linux_dynamic.elf", summary.FileName);
        Assert.Equal(bundle.Analysis.File.FullPath, summary.FilePath);
        Assert.Equal(bundle.Analysis.File.Sha256, summary.Sha256);
        Assert.Equal(bundle.Analysis.File.SizeBytes, summary.FileSizeBytes);
        Assert.Equal(bundle.Analysis.AnalyzedAtUtc, summary.AnalyzedAtUtc);
        Assert.Equal("x86-64 (64-bit, little endian)", summary.Architecture);
        Assert.Equal(bundle.Analysis.Format.DisplayName, summary.FileFormat);
        Assert.Equal(bundle.Report.OverallPortingStatus, summary.OverallStatus);
        Assert.Null(summary.ReportPath);
        Assert.Equal(bundle.Analysis.Elf!.Symbols.Count, record.Bundle.Analysis.Elf!.Symbols.Count);
        Assert.Equal(bundle.Analysis.Elf.Sections.Select(s => s.Name), record.Bundle.Analysis.Elf.Sections.Select(s => s.Name));
        Assert.Equal(bundle.Analysis.Dependencies!.Libraries, record.Bundle.Analysis.Dependencies!.Libraries);
        Assert.Equal(bundle.Analysis.Platform!.Summary, record.Bundle.Analysis.Platform!.Summary);
        Assert.Equal(bundle.Report.StatusCode, record.Bundle.Report.StatusCode);
        Assert.Equal(bundle.Report.WorkSteps.Count, record.Bundle.Report.WorkSteps.Count);
    }

    [Fact]
    public async Task Data_survives_opening_the_database_again()
    {
        using var dir = new TempDir();
        var path = dir.File("db.sqlite");
        var saved = await new SqliteProjectStore(path).SaveAsync(Bundle("poc_mips32_be_sum.elf"), null);

        var list = await new SqliteProjectStore(path).ListAsync();

        var only = Assert.Single(list);
        Assert.Equal(saved.Id, only.Id);
        Assert.Equal("poc_mips32_be_sum.elf", only.Name); // defaults to the file name
    }

    [Fact]
    public async Task Lists_newest_analysis_first()
    {
        using var dir = new TempDir();
        var store = new SqliteProjectStore(dir.File("db.sqlite"));
        await store.SaveAsync(Bundle("poc_mips32_be_sum.elf", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)), "old");
        await store.SaveAsync(Bundle("poc_mips32_le_call.elf", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)), "new");

        var list = await store.ListAsync();

        Assert.Equal(["new", "old"], list.Select(p => p.Name));
    }

    [Fact]
    public async Task Renames_updates_notes_and_report_path_and_deletes()
    {
        using var dir = new TempDir();
        var store = new SqliteProjectStore(dir.File("db.sqlite"));
        var saved = await store.SaveAsync(Bundle("poc_mips32_be_sum.elf"), "a");

        Assert.True(await store.RenameAsync(saved.Id, "  Renamed  "));
        Assert.True(await store.UpdateNotesAsync(saved.Id, "found something"));
        Assert.True(await store.SetReportPathAsync(saved.Id, @"C:\reports\r.html"));
        var changed = (await store.GetAsync(saved.Id))!.Summary;

        Assert.Equal("Renamed", changed.Name);
        Assert.Equal("found something", changed.Notes);
        Assert.Equal(@"C:\reports\r.html", changed.ReportPath);

        Assert.True(await store.DeleteAsync(saved.Id));
        Assert.Null(await store.GetAsync(saved.Id));
        Assert.False(await store.DeleteAsync(saved.Id));
        Assert.False(await store.RenameAsync(Guid.NewGuid(), "x"));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Rejects_empty_names_and_truncates_overlong_ones()
    {
        using var dir = new TempDir();
        var store = new SqliteProjectStore(dir.File("db.sqlite"));
        var saved = await store.SaveAsync(Bundle("poc_mips32_be_sum.elf"), new string('x', 500));

        Assert.Equal(200, saved.Name.Length);
        await Assert.ThrowsAsync<ProjectStoreException>(() => store.RenameAsync(saved.Id, "   "));
    }

    [Fact]
    public async Task Stores_failed_analyses_too()
    {
        using var dir = new TempDir();
        var store = new SqliteProjectStore(dir.File("db.sqlite"));
        var bundle = await T.AnalyzeAsync("plain text"u8.ToArray(), "x.txt");

        var saved = await store.SaveAsync(bundle, null);
        var record = await store.GetAsync(saved.Id);

        Assert.Equal("Unknown", saved.Architecture);
        Assert.Equal("Not analyzable", saved.OverallStatus);
        Assert.False(record!.Bundle.Analysis.Parsed);
        Assert.Contains("Unsupported format", saved.Hints);
    }

    [Fact]
    public async Task Does_not_copy_the_analyzed_file_into_the_database()
    {
        using var dir = new TempDir();
        var store = new SqliteProjectStore(dir.File("db.sqlite"));
        var builder = ElfBuilder.X64Linux();
        builder.WithCode(new byte[4 * 1024 * 1024]);
        var bundle = await T.AnalyzeAsync(builder.Build());

        await store.SaveAsync(bundle, null);
        SqliteConnection.ClearAllPools();
        var dbBytes = Directory.GetFiles(dir.Path, "db.sqlite*").Sum(p => new FileInfo(p).Length);

        Assert.True(bundle.Analysis.File.SizeBytes > 4_000_000);
        Assert.True(dbBytes < 300_000, $"Database is {dbBytes} bytes.");
    }

    [Fact]
    public async Task Concurrent_saves_of_different_files_are_all_stored()
    {
        using var dir = new TempDir();
        var store = new SqliteProjectStore(dir.File("db.sqlite"));
        var bundle = Bundle("poc_mips32_be_sum.elf");

        await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            store.SaveAsync(bundle with { Analysis = bundle.Analysis with { File = bundle.Analysis.File with { FullPath = $@"C:\files\{i}.elf" } } }, "p" + i)));

        var list = await store.ListAsync();
        Assert.Equal(12, list.Count);
        Assert.Equal(12, list.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task Analyzing_the_same_file_again_refreshes_the_project_and_keeps_user_edits()
    {
        using var dir = new TempDir();
        var store = new SqliteProjectStore(dir.File("db.sqlite"));
        var original = Bundle("poc_mips32_be_sum.elf", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var first = await store.SaveAsync(original, "first name");
        await store.RenameAsync(first.Id, "My renamed project");
        await store.UpdateNotesAsync(first.Id, "important notes");
        await store.SetReportPathAsync(first.Id, @"C:\reports\old.html");

        // The same file (same hash, same path) analyzed again later.
        var again = original with { Analysis = original.Analysis with { AnalyzedAtUtc = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) } };
        var second = await store.SaveAsync(again, "ignored name");

        var list = await store.ListAsync();
        var only = Assert.Single(list);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Id, only.Id);
        Assert.Equal("My renamed project", only.Name);
        Assert.Equal("important notes", only.Notes);
        Assert.Equal(@"C:\reports\old.html", only.ReportPath);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), only.AnalyzedAtUtc);
    }

    [Fact]
    public async Task Different_files_or_different_paths_are_separate_projects()
    {
        using var dir = new TempDir();
        var store = new SqliteProjectStore(dir.File("db.sqlite"));
        var a = Bundle("poc_mips32_be_sum.elf");
        var movedCopy = a with { Analysis = a.Analysis with { File = a.Analysis.File with { FullPath = @"C:\elsewhere\poc_mips32_be_sum.elf" } } };

        var first = await store.SaveAsync(a, null);
        var second = await store.SaveAsync(movedCopy, null);
        var other = await store.SaveAsync(Bundle("poc_mips32_le_call.elf"), null);

        Assert.Equal(3, new[] { first.Id, second.Id, other.Id }.Distinct().Count());
        Assert.Equal(3, (await store.ListAsync()).Count);
    }

    [Fact]
    public async Task Damaged_stored_data_gives_a_readable_error()
    {
        using var dir = new TempDir();
        var path = dir.File("db.sqlite");
        var store = new SqliteProjectStore(path);
        var saved = await store.SaveAsync(Bundle("poc_mips32_be_sum.elf"), "x");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE projects SET analysis_gz = X'00010203'";
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<ProjectStoreException>(() => store.GetAsync(saved.Id));

        Assert.Contains("damaged", error.Message);
        Assert.Single(await store.ListAsync()); // the list still works
    }

    [Fact]
    public async Task Refuses_a_database_from_a_newer_version()
    {
        using var dir = new TempDir();
        var path = dir.File("future.sqlite");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<ProjectStoreException>(() => new SqliteProjectStore(path).ListAsync());

        Assert.Contains("newer version", error.Message);
    }

    [Fact]
    public async Task An_unusable_database_location_gives_a_readable_error()
    {
        using var dir = new TempDir();

        var error = await Assert.ThrowsAsync<ProjectStoreException>(() => new SqliteProjectStore(dir.Path).ListAsync());

        Assert.Contains("database", error.Message);
    }

    [Fact]
    public async Task Creates_missing_folders_for_the_database()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a", "b", "c.sqlite");

        Assert.Empty(await new SqliteProjectStore(path).ListAsync());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqliteProjectStore(dir.File("c.sqlite")).ListAsync(cts.Token));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults_and_round_trips_changes()
    {
        using var dir = new TempDir();
        var store = new JsonSettingsStore(dir.File("settings.json"));

        Assert.Equal(new AppSettings(), store.Load());

        var changed = new AppSettings { MaxFileSizeMiB = 64, AutoSaveProjects = false, EnableAnimations = false, ExportDirectory = @"C:\out" };
        store.Save(changed);

        Assert.Equal(changed, store.Load());
        Assert.False(File.Exists(dir.File("settings.json.tmp")));
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{ nope");

        Assert.Equal(new AppSettings(), new JsonSettingsStore(dir.File("settings.json")).Load());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(512, 512)]
    [InlineData(1_000_000, 16_384)]
    public void Size_limit_is_clamped_to_a_sane_range(int mib, int expectedMib)
    {
        var limits = new AppSettings { MaxFileSizeMiB = mib }.ToLimits();

        Assert.Equal(expectedMib * 1024L * 1024L, limits.MaxFileSizeBytes);
    }

    [Fact]
    public void App_paths_honour_the_data_directory_override()
    {
        using var dir = new TempDir();
        var previous = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, dir.Path);
            var paths = AppPaths.Default();
            paths.EnsureCreated();

            Assert.Equal(dir.Path, paths.Root);
            Assert.True(Directory.Exists(paths.Logs));
            Assert.EndsWith("recompradar.db", paths.Database);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, previous);
        }
    }

    [Fact]
    public void Structured_logging_writes_json_lines()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        using (var factory = LoggingSetup.CreateLoggerFactory(paths))
        {
            factory.CreateLogger("test").LogInformation("Analysis finished for {FileName} in {ElapsedMs} ms", "a.elf", 12);
        }

        var file = Directory.GetFiles(paths.Logs, "recompradar-*.jsonl").Single();
        var line = File.ReadAllLines(file).Single(l => l.Contains("Analysis finished", StringComparison.Ordinal));
        using var doc = System.Text.Json.JsonDocument.Parse(line);
        Assert.Equal("a.elf", doc.RootElement.GetProperty("FileName").GetString());
        Assert.Equal(12, doc.RootElement.GetProperty("ElapsedMs").GetInt32());
        Assert.Equal("RecompRadar", doc.RootElement.GetProperty("Application").GetString());
    }
}
