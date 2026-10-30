using System.Text.Json;
using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Portability.Export;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

public class ExportTests
{
    private static AnalysisBundle Sample(string name) =>
        T.Analyze(SampleFiles.All().Single(s => s.FileName == name).Bytes, name);

    [Fact]
    public void Json_export_contains_analysis_and_report_and_round_trips()
    {
        var bundle = Sample("synthetic_x86_64_linux_dynamic.elf");

        var json = new JsonReportExporter().Render(bundle);
        var document = JsonSerializer.Deserialize<ExportDocument>(json, RecompRadarJson.Options)!;

        Assert.Equal("recompradar.export/1", document.Schema);
        Assert.Equal(bundle.Analysis.File.Sha256, document.Analysis.File.Sha256);
        Assert.Equal(bundle.Analysis.Elf!.Symbols.Count, document.Analysis.Elf!.Symbols.Count);
        Assert.Equal(bundle.Analysis.Elf.Header.Entry, document.Analysis.Elf.Header.Entry);
        Assert.Equal(bundle.Analysis.Dependencies!.Libraries, document.Analysis.Dependencies!.Libraries);
        Assert.Equal(bundle.Report.StatusCode, document.Report.StatusCode);
        Assert.Equal(bundle.Report.Questions.Count, document.Report.Questions.Count);
        Assert.Equal(bundle.Analysis.Platform!.Summary, document.Analysis.Platform!.Summary);
        Assert.Equal(bundle.Analysis.Dependencies.ApiMatches.Count, document.Analysis.Dependencies.ApiMatches.Count);
        Assert.Contains("\"statusCode\": \"InstructionSetMatches\"", json);
    }

    [Fact]
    public void Json_export_is_valid_json_for_every_sample()
    {
        foreach (var sample in SampleFiles.All())
        {
            var json = new JsonReportExporter().Render(T.Analyze(sample.Bytes, sample.FileName));

            using var doc = JsonDocument.Parse(json);
            Assert.Equal("RecompRadar", doc.RootElement.GetProperty("tool").GetString());
        }
    }

    [Fact]
    public void Markdown_export_has_the_expected_sections()
    {
        var bundle = Sample("poc_mips32_be_sum.elf");

        var md = new MarkdownReportExporter().Render(bundle);

        Assert.Contains("# RecompRadar report: poc_mips32_be_sum.elf", md);
        Assert.Contains("## Summary", md);
        Assert.Contains("| Property | Value |", md);
        Assert.Contains("MIPS (32-bit, big endian)", md);
        Assert.Contains("Further investigation required", md);
        Assert.Contains("## Segments", md);
        Assert.Contains("## Suggested work plan", md);
        Assert.Contains("## Warnings and notes", md);
        Assert.Contains(bundle.Analysis.File.Sha256, md);
    }

    [Fact]
    public void Html_export_is_a_self_contained_page_with_tables()
    {
        var bundle = Sample("synthetic_x86_64_linux_dynamic.elf");

        var html = new HtmlReportExporter().Render(bundle);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("Overall porting status", html);
        Assert.Contains("libSDL2-2.0.so.0", html);
        Assert.Contains("prefers-color-scheme", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=", html);
        Assert.DoesNotContain("src=", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void Html_export_encodes_hostile_names_from_the_file()
    {
        var builder = ElfBuilder.X64Linux();
        builder.WithCode([0xC3]);
        builder.NeededLibraries.Add("<script>alert(1)</script>");
        builder.Symbols.Add(SymbolSpec.Function("\"><img src=x onerror=alert(2)>", 0x400000));

        var html = new HtmlReportExporter().Render(T.Analyze(builder.Build()));

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void Exports_work_for_unanalyzable_input()
    {
        var bundle = T.Analyze("not an executable"u8.ToArray(), "note.txt");

        foreach (var exporter in new ReportExportService().Exporters)
        {
            var text = exporter.Render(bundle);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains("Not analyzable", text);
        }
    }

    [Fact]
    public void Large_tables_are_capped_in_html_but_complete_in_json()
    {
        var builder = ElfBuilder.X64Linux();
        builder.WithCode([0xC3]);
        for (var i = 0; i < 3000; i++)
        {
            builder.Symbols.Add(SymbolSpec.Import($"unknown_import_{i}"));
        }

        var bundle = T.Analyze(builder.Build());

        var html = new HtmlReportExporter().Render(bundle);
        var json = new JsonReportExporter().Render(bundle);

        Assert.Contains("Showing the first 2000 of 3000 rows", html);
        Assert.Contains("unknown_import_2999", json);
    }

    [Fact]
    public async Task Export_service_writes_files_atomically()
    {
        using var dir = new TempDir();
        var service = new ReportExportService();
        var bundle = Sample("poc_mips32_be_sum.elf");

        foreach (var format in new[] { "json", "markdown", "html" })
        {
            var path = dir.File("report-" + format + service.Get(format).FileExtension);
            var written = await service.ExportAsync(bundle, format, path);

            Assert.Equal(Path.GetFullPath(path), written);
            Assert.Equal(service.Get(format).Render(bundle), await File.ReadAllTextAsync(written));
        }

        Assert.DoesNotContain(Directory.GetFiles(dir.Path, "*", SearchOption.TopDirectoryOnly), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_service_overwrites_an_existing_report()
    {
        using var dir = new TempDir();
        var path = dir.Write("r.html", "old"u8.ToArray());

        await new ReportExportService().ExportAsync(Sample("poc_mips32_be_sum.elf"), "html", path);

        Assert.StartsWith("<!DOCTYPE html>", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Export_service_rejects_bad_targets_with_readable_errors()
    {
        var service = new ReportExportService();
        var bundle = Sample("poc_mips32_be_sum.elf");
        using var dir = new TempDir();

        var missingFolder = await Assert.ThrowsAsync<ExportException>(() => service.ExportAsync(bundle, "json", Path.Combine(dir.Path, "missing", "r.json")));
        var empty = await Assert.ThrowsAsync<ExportException>(() => service.ExportAsync(bundle, "json", ""));
        var unknownFormat = await Assert.ThrowsAsync<ExportException>(() => service.ExportAsync(bundle, "pdf", dir.File("r.pdf")));

        Assert.Contains("does not exist", missingFolder.Message);
        Assert.Contains("No target file", empty.Message);
        Assert.Contains("not available", unknownFormat.Message);
    }

    [Fact]
    public void Suggested_file_names_are_safe()
    {
        var bundle = Sample("poc_mips32_be_sum.elf");
        var odd = bundle with { Analysis = bundle.Analysis with { File = bundle.Analysis.File with { FileName = "a<b>:c|d.elf" } } };
        var service = new ReportExportService();

        Assert.Equal("poc_mips32_be_sum.elf.report.html", service.SuggestFileName(bundle, "html"));
        Assert.Equal("a_b__c_d.elf.report.json", service.SuggestFileName(odd, "json"));
        Assert.Equal(-1, service.SuggestFileName(odd, "json").IndexOfAny(Path.GetInvalidFileNameChars()));
    }
}
