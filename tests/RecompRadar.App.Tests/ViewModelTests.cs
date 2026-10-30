using System.ComponentModel;
using System.Text.Json;
using System.Windows.Data;
using RecompRadar.App.Services;
using RecompRadar.App.ViewModels;
using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.TestSupport;

namespace RecompRadar.App.Tests;

[Collection("WPF")]
public class AnalysisWorkflowTests
{
    private static int Count(ICollectionView view) => view.Cast<object>().Count();

    [Fact]
    public void Analyzing_a_file_fills_every_page_with_real_results()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            var path = h.Sample("synthetic_mips32_le_r5900_flag.elf");

            await h.Session.AnalyzeAsync(path);

            // Binary Analyzer
            Assert.True(h.Analyzer.HasResult);
            Assert.True(h.Analyzer.IsParsed);
            Assert.Equal("synthetic_mips32_le_r5900_flag.elf", h.Analyzer.FileName);
            Assert.Equal(64, h.Analyzer.Sha256.Length);
            Assert.Equal("MIPS (32-bit, little endian)", h.Analyzer.ArchitectureText);
            Assert.Equal("0x100000", h.Analyzer.EntryText);
            Assert.Contains(h.Analyzer.HeaderRows, r => r.Name == "Machine" && r.Value.StartsWith("MIPS", StringComparison.Ordinal) && r.Evidence == "Verified");
            Assert.Equal(2, h.Analyzer.Segments.Count);
            Assert.Contains(h.Analyzer.Sections, s => s.Name == ".text");
            Assert.NotEmpty(h.Analyzer.Regions);
            Assert.Equal(4, Count(h.Analyzer.SymbolsView));
            Assert.False(h.Analyzer.NoSymbols);

            // Architecture Inspector
            Assert.True(h.Architecture.HasResult);
            var target = Assert.Single(h.Architecture.SummaryRows, r => r.Name == "Target System");
            Assert.Equal("Inferred", target.Evidence);
            Assert.Contains("PlayStation 2", target.Value);
            Assert.Equal("Verified", h.Architecture.SummaryRows.Single(r => r.Name == "Architecture").Evidence);
            Assert.Equal("Not directly compatible", h.Architecture.SummaryRows.Single(r => r.Name == "Native Execution").Value);
            Assert.NotEmpty(h.Architecture.Candidates);
            Assert.Contains("not proof", h.Architecture.TargetDisclaimer);

            // Dependencies
            Assert.True(h.Dependencies.HasResult);
            Assert.Contains(h.Dependencies.ApiMatches, m => m.Name == "SifInitRpc" && m.Platforms.Contains("ps2"));
            Assert.False(h.Dependencies.NoSymbols);

            // Portability Report
            Assert.True(h.Report.HasResult);
            Assert.Equal(6, h.Report.Questions.Count);
            Assert.StartsWith("Further investigation required", h.Report.OverallStatus);
            Assert.Equal("warn", h.Report.StatusKind);
            Assert.Contains(h.Report.SummaryRows, r => r.Name == "CPU Translation" && r.Value == "Required");
            Assert.NotEmpty(h.Report.WorkSteps);
            Assert.Contains("not estimate compatibility percentages", h.Report.Disclaimer);

            // Navigation shows the analyzer after a result was loaded
            Assert.IsType<BinaryAnalyzerViewModel>(h.Main.CurrentPage);
        });
    }

    [Fact]
    public void Entry_point_preview_shows_decoded_instructions_for_supported_architectures()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();

            await h.Session.AnalyzeAsync(h.Sample("poc_mips32_be_sum.elf"));
            await Harness.WaitUntilAsync(() => h.Architecture.PreviewLines.Count > 0);

            Assert.True(h.Architecture.HasPreview);
            Assert.Contains("addiu", h.Architecture.PreviewLines[0]);
            Assert.Contains("MIPS32", h.Architecture.PreviewTitle);
        });
    }

    [Fact]
    public void Entry_point_preview_is_absent_for_architectures_without_a_decoder()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();

            await h.Session.AnalyzeAsync(h.Sample("synthetic_x86_64_linux_dynamic.elf"));
            await Task.Delay(300);

            Assert.Empty(h.Architecture.PreviewLines);
            Assert.False(h.Architecture.HasPreview);
        });
    }

    [Fact]
    public void Symbol_page_filters_by_name_and_by_imports()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();

            await h.Session.AnalyzeAsync(h.Sample("synthetic_x86_64_linux_dynamic.elf"));
            var all = Count(h.Analyzer.SymbolsView);
            h.Analyzer.SymbolFilter = "sdl_";
            var sdl = Count(h.Analyzer.SymbolsView);
            h.Analyzer.SymbolFilter = string.Empty;
            h.Analyzer.ImportsOnly = true;
            var imports = Count(h.Analyzer.SymbolsView);

            Assert.True(all > imports);
            Assert.Equal(2, sdl); // SDL_Init and SDL_CreateWindow
            Assert.Equal(8, imports);
            Assert.All(h.Analyzer.SymbolsView.Cast<SymbolRow>(), s => Assert.Equal("Import", s.Kind));
        });
    }

    [Fact]
    public void Dependencies_page_lists_libraries_imports_and_hardware_hints()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();

            await h.Session.AnalyzeAsync(h.Sample("synthetic_x86_64_linux_dynamic.elf"));

            Assert.Equal(["libc.so.6", "libm.so.6", "libSDL2-2.0.so.0", "libGL.so.1"], h.Dependencies.Libraries.Select(l => l.Name));
            Assert.Contains(h.Dependencies.Libraries, l => l.Name == "libc.so.6" && l.Detail == "C standard library");
            Assert.Contains(h.Dependencies.HardwareHints, m => m.Name == "ioctl");
            Assert.Equal(8, Count(h.Dependencies.ImportsView));
            h.Dependencies.ImportFilter = "pthread";
            Assert.Equal(1, Count(h.Dependencies.ImportsView));
        });
    }

    [Fact]
    public void Stripped_file_shows_the_no_symbols_warning_on_both_pages()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();

            await h.Session.AnalyzeAsync(h.Sample("synthetic_aarch64_stripped.elf"));

            Assert.True(h.Analyzer.NoSymbols);
            Assert.Contains("no symbol tables", h.Analyzer.SymbolSummary);
            Assert.True(h.Dependencies.NoSymbols);
            Assert.Empty(h.Analyzer.Sections);
            Assert.Contains(h.Report.MissingInformation, m => m.Contains("Function and variable names"));
        });
    }

    [Fact]
    public void A_file_that_cannot_be_analyzed_explains_why_and_is_not_saved()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();

            await h.Session.AnalyzeAsync(h.Sample("not_an_elf.bin"));
            await h.Projects.RefreshAsync();

            Assert.True(h.Analyzer.HasResult);
            Assert.False(h.Analyzer.IsParsed);
            Assert.Equal("This file could not be analyzed", h.Analyzer.ProblemTitle);
            Assert.False(string.IsNullOrWhiteSpace(h.Analyzer.ProblemText));
            Assert.NotEmpty(h.Analyzer.Diagnostics);
            Assert.False(h.Architecture.HasResult);
            Assert.False(h.Dependencies.HasResult);
            Assert.True(h.Report.HasResult);
            Assert.Equal("bad", h.Report.StatusKind);
            Assert.True(h.Notifications.IsVisible);
            Assert.Equal(NotificationKind.Warning, h.Notifications.Kind);
            Assert.True(h.Projects.IsEmpty);
        });
    }

    [Fact]
    public void Damaged_elf_files_show_warnings_but_still_a_partial_result()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();

            await h.Session.AnalyzeAsync(h.Sample("corrupt_bad_offsets.elf"));

            Assert.True(h.Analyzer.IsParsed);
            Assert.Contains(h.Analyzer.Diagnostics, d => d.Severity == "Warning" && d.Explanation.Length > 0);
            Assert.Contains(h.Analyzer.HeaderRows, r => r.Name == "Program headers" && r.Value.Contains("declared"));
            Assert.NotEmpty(h.Report.Warnings);
        });
    }

    [Fact]
    public void Missing_files_and_files_above_the_limit_give_readable_messages()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await h.Session.AnalyzeAsync(h.DataPath("does-not-exist.elf"));
            Assert.Contains("does not exist", h.Analyzer.ProblemText);

            var big = ElfBuilder.X64Linux();
            big.WithCode(new byte[2 * 1024 * 1024]);
            var bigPath = h.DataPath("big.elf");
            await File.WriteAllBytesAsync(bigPath, big.Build());
            h.SettingsPage.MaxFileSizeText = "1";
            h.SettingsPage.SaveCommand.Execute(null);

            await h.Session.AnalyzeAsync(bigPath);

            Assert.False(h.Analyzer.IsParsed);
            Assert.Contains("above the configured limit", h.Analyzer.ProblemText);
        });
    }

    [Fact]
    public void Several_dropped_files_analyze_only_the_first()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            var first = h.Sample("poc_mips32_be_sum.elf");
            var second = h.Sample("poc_mips32_le_call.elf");

            await h.Analyzer.DropFilesCommand.ExecuteAsync(new[] { first, second });

            Assert.Equal("poc_mips32_be_sum.elf", h.Analyzer.FileName);
        });
    }

    [Fact]
    public void Dropping_onto_the_window_or_dashboard_or_choosing_a_file_all_start_an_analysis()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();

            await h.Main.DropFilesCommand.ExecuteAsync(new[] { h.Sample("poc_mips32_be_sum.elf") });
            Assert.Equal("poc_mips32_be_sum.elf", h.Analyzer.FileName);

            await h.Dashboard.DropFilesCommand.ExecuteAsync(new[] { h.Sample("poc_mips32_le_call.elf") });
            Assert.Equal("poc_mips32_le_call.elf", h.Analyzer.FileName);

            h.Dialogs.OpenPath = h.Sample("synthetic_aarch64_stripped.elf");
            await h.Analyzer.BrowseCommand.ExecuteAsync(null);
            Assert.Equal("synthetic_aarch64_stripped.elf", h.Analyzer.FileName);

            h.Dialogs.OpenPath = null; // the user cancels the dialog
            await h.Dashboard.BrowseCommand.ExecuteAsync(null);
            Assert.Equal("synthetic_aarch64_stripped.elf", h.Analyzer.FileName);
        });
    }

    [Fact]
    public void Navigation_offers_the_seven_pages_in_the_required_order()
    {
        Sta.Run(() =>
        {
            using var h = new Harness();

            Assert.Equal(
                ["Dashboard", "Binary Analyzer", "Architecture Inspector", "Dependencies", "Portability Report", "Projects", "Settings"],
                h.Main.NavItems.Select(n => n.Title));
            Assert.IsType<DashboardViewModel>(h.Main.CurrentPage);

            h.Main.Navigate("report");
            Assert.IsType<ReportViewModel>(h.Main.CurrentPage);
            Assert.Equal(["Portability Report"], h.Main.NavItems.Where(n => n.IsSelected).Select(n => n.Title));

            h.Main.NavItems.Single(n => n.Title == "Settings").IsSelected = true;
            Assert.IsType<SettingsViewModel>(h.Main.CurrentPage);
            return Task.CompletedTask;
        });
    }
}

[Collection("WPF")]
public class ReportAndProjectTests
{
    [Fact]
    public void All_three_exports_write_valid_files_and_remember_the_report_path()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await h.Session.AnalyzeAsync(h.Sample("synthetic_x86_64_linux_dynamic.elf"));
            var jsonPath = h.DataPath("out.json");
            var mdPath = h.DataPath("out.md");
            var htmlPath = h.DataPath("out.html");

            h.Dialogs.SavePath = jsonPath;
            await h.Report.ExportJsonCommand.ExecuteAsync(null);
            h.Dialogs.SavePath = mdPath;
            await h.Report.ExportMarkdownCommand.ExecuteAsync(null);
            h.Dialogs.SavePath = htmlPath;
            await h.Report.ExportHtmlCommand.ExecuteAsync(null);

            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath)))
            {
                Assert.Equal("recompradar.export/1", doc.RootElement.GetProperty("schema").GetString());
                Assert.Equal("x86-64 (64-bit, little endian)", doc.RootElement.GetProperty("report").GetProperty("sourceArchitecture").GetString());
            }

            Assert.Contains("# RecompRadar report", await File.ReadAllTextAsync(mdPath));
            Assert.Contains("<table>", await File.ReadAllTextAsync(htmlPath));
            Assert.Equal("synthetic_x86_64_linux_dynamic.elf.report.html", h.Dialogs.LastSuggestedName);
            Assert.Equal(htmlPath, h.Session.LastExportPath);
            Assert.True(h.Report.HasExport);
            var stored = Assert.Single(await h.Store.ListAsync());
            Assert.Equal(htmlPath, stored.ReportPath);

            h.Report.RevealExportCommand.Execute(null);
            Assert.Equal([htmlPath], h.Shell.Revealed);
            Assert.True(h.Notifications.IsVisible);
            Assert.Equal(NotificationKind.Success, h.Notifications.Kind);
        });
    }

    [Fact]
    public void Cancelling_the_save_dialog_writes_nothing_and_a_bad_folder_gives_an_explained_error()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await h.Session.AnalyzeAsync(h.Sample("poc_mips32_be_sum.elf"));

            h.Dialogs.SavePath = null;
            await h.Report.ExportJsonCommand.ExecuteAsync(null);
            Assert.False(h.Report.HasExport);

            h.Dialogs.SavePath = h.DataPath(Path.Combine("missing-folder", "r.json"));
            await h.Report.ExportJsonCommand.ExecuteAsync(null);

            Assert.False(h.Report.HasExport);
            Assert.Equal(NotificationKind.Error, h.Notifications.Kind);
            Assert.Equal("The report could not be exported", h.Notifications.Title);
            Assert.Contains("does not exist", h.Notifications.Message);
            Assert.False(string.IsNullOrWhiteSpace(h.Notifications.Details));
        });
    }

    [Fact]
    public void Successful_analyses_are_saved_automatically_and_shown_in_projects_and_dashboard()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();

            await h.Session.AnalyzeAsync(h.Sample("poc_mips32_be_sum.elf"));
            await h.Session.AnalyzeAsync(h.Sample("synthetic_ppc64_be_ps3_like.elf"));
            await Harness.WaitUntilAsync(() => h.Projects.Projects.Count == 2 && h.Dashboard.ProjectCount == 2);

            Assert.Equal(2, h.Dashboard.RecentProjects.Count);
            Assert.Equal("PowerPC (64-bit, big endian)", h.Dashboard.LastArchitectureText);
            Assert.True(h.Report.IsSaved);
            Assert.Equal("2 project(s)", h.Projects.CountText);
            h.Projects.Filter = "ppc64"; // matches the name and the architecture text of exactly one project
            Assert.Equal(["synthetic_ppc64_be_ps3_like.elf"], h.Projects.Projects.Select(p => p.Name));
            h.Projects.Filter = "no such thing";
            Assert.Empty(h.Projects.Projects);
        });
    }

    [Fact]
    public void Turning_auto_save_off_keeps_results_out_of_the_project_list_until_saved_by_hand()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            h.SettingsPage.AutoSaveProjects = false;
            h.SettingsPage.SaveCommand.Execute(null);

            await h.Session.AnalyzeAsync(h.Sample("poc_mips32_be_sum.elf"));
            Assert.Empty(await h.Store.ListAsync());
            Assert.False(h.Report.IsSaved);
            Assert.True(h.Report.SaveProjectCommand.CanExecute(null));

            await h.Report.SaveProjectCommand.ExecuteAsync(null);

            Assert.Single(await h.Store.ListAsync());
            Assert.True(h.Report.IsSaved);
            Assert.False(h.Report.SaveProjectCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Saved_projects_can_be_reopened_renamed_annotated_and_deleted()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await h.Session.AnalyzeAsync(h.Sample("synthetic_mips32_le_psp_prx_like.elf"));
            await h.Session.AnalyzeAsync(h.Sample("poc_mips32_be_sum.elf"));
            await Harness.WaitUntilAsync(() => h.Projects.Projects.Count == 2);

            // Reopen the first project: every page shows its stored result without analyzing the file again.
            h.Projects.Selected = h.Projects.Projects.Single(p => p.FileName == "synthetic_mips32_le_psp_prx_like.elf");
            Assert.Equal("synthetic_mips32_le_psp_prx_like.elf", h.Projects.EditName);
            await h.Projects.OpenSelectedCommand.ExecuteAsync(null);
            Assert.Equal("synthetic_mips32_le_psp_prx_like.elf", h.Analyzer.FileName);
            Assert.Contains("PSP", h.Architecture.TargetSummary);
            Assert.Equal(h.Projects.Selected.Id, h.Session.CurrentProjectId);
            Assert.IsType<BinaryAnalyzerViewModel>(h.Main.CurrentPage);

            // Rename and annotate.
            h.Projects.EditName = "PSP module test";
            h.Projects.EditNotes = "checked flags";
            await h.Projects.SaveChangesCommand.ExecuteAsync(null);
            var renamed = Assert.Single(await h.Store.ListAsync(), p => p.Name == "PSP module test");
            Assert.Equal("checked flags", renamed.Notes);

            // Deleting asks first; "no" keeps it, "yes" removes only the stored result.
            h.Projects.Selected = h.Projects.Projects.Single(p => p.Name == "PSP module test");
            var analyzedFile = h.Sample("synthetic_mips32_le_psp_prx_like.elf");
            h.Confirm.Answer = false;
            await h.Projects.DeleteSelectedCommand.ExecuteAsync(null);
            Assert.Equal(2, (await h.Store.ListAsync()).Count);

            h.Confirm.Answer = true;
            await h.Projects.DeleteSelectedCommand.ExecuteAsync(null);
            Assert.Equal(2, h.Confirm.Asked);
            Assert.Single(await h.Store.ListAsync());
            Assert.True(File.Exists(analyzedFile));
        });
    }

    [Fact]
    public void A_project_whose_file_moved_can_still_be_opened()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            var path = h.Sample("synthetic_mips32_le_r5900_flag.elf");
            await h.Session.AnalyzeAsync(path);
            await Harness.WaitUntilAsync(() => h.Projects.Projects.Count == 1);
            File.Delete(path);

            h.Projects.Selected = h.Projects.Projects[0];
            Assert.True(h.Projects.FileMissing);
            await h.Projects.OpenSelectedCommand.ExecuteAsync(null);
            await Task.Delay(300);

            Assert.Equal("MIPS (32-bit, little endian)", h.Analyzer.ArchitectureText);
            Assert.Empty(h.Architecture.PreviewLines);
            Assert.Contains("no longer available", h.Architecture.PreviewNote);
        });
    }
}

[Collection("WPF")]
public class SettingsTests
{
    [Fact]
    public void Invalid_values_are_rejected_with_a_message_and_valid_values_are_saved_to_disk()
    {
        Sta.Run(() =>
        {
            using var h = new Harness();

            foreach (var bad in new[] { "abc", "0", "-5", "99999", "", "12.5" })
            {
                h.SettingsPage.MaxFileSizeText = bad;
                h.SettingsPage.SaveCommand.Execute(null);
                Assert.Contains("between 1 and 16384", h.SettingsPage.ValidationMessage);
                Assert.Equal(2048, h.Settings.Current.MaxFileSizeMiB);
            }

            h.SettingsPage.MaxFileSizeText = "64";
            h.SettingsPage.AutoSaveProjects = false;
            h.SettingsPage.EnableAnimations = false;
            h.SettingsPage.ExportDirectory = @"C:\exports";
            h.SettingsPage.SaveCommand.Execute(null);

            Assert.Equal(string.Empty, h.SettingsPage.ValidationMessage);
            var reloaded = new RecompRadar.Infrastructure.JsonSettingsStore(h.Paths.Settings).Load();
            Assert.Equal(64, reloaded.MaxFileSizeMiB);
            Assert.False(reloaded.AutoSaveProjects);
            Assert.False(reloaded.EnableAnimations);
            Assert.Equal(@"C:\exports", reloaded.ExportDirectory);

            h.SettingsPage.ResetCommand.Execute(null);
            Assert.Equal(new AppSettings(), h.Settings.Current);
            Assert.Equal("2048", h.SettingsPage.MaxFileSizeText);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Folder_pickers_and_shell_buttons_use_the_chosen_folders()
    {
        Sta.Run(() =>
        {
            using var h = new Harness();
            h.Dialogs.Folder = @"C:\reports";

            h.SettingsPage.BrowseExportDirectoryCommand.Execute(null);
            h.SettingsPage.BrowseKnowledgeDirectoryCommand.Execute(null);
            h.SettingsPage.OpenDataFolderCommand.Execute(null);
            h.SettingsPage.OpenLogFolderCommand.Execute(null);
            h.SettingsPage.OpenKnowledgeFolderCommand.Execute(null);

            Assert.Equal(@"C:\reports", h.SettingsPage.ExportDirectory);
            Assert.Equal(@"C:\reports", h.SettingsPage.ExtraKnowledgeDirectory);
            Assert.Equal([h.Paths.Root, h.Paths.Logs, h.Paths.Knowledge], h.Shell.Opened);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Extra_knowledge_folder_from_the_settings_is_used_by_the_analysis()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            var knowledge = h.DataPath("my-knowledge");
            Directory.CreateDirectory(knowledge);
            await File.WriteAllTextAsync(Path.Combine(knowledge, "mine.json"),
                """{ "rules": [ { "id": "mine", "kind": "symbol", "match": "exact", "patterns": ["SifInitRpc"], "category": "Custom", "apiFamily": "My own family", "description": "mine" } ] }""");
            h.SettingsPage.ExtraKnowledgeDirectory = knowledge;
            h.SettingsPage.SaveCommand.Execute(null);

            await h.Session.AnalyzeAsync(h.Sample("synthetic_mips32_le_r5900_flag.elf"));

            Assert.Contains(h.Dependencies.ApiMatches, m => m.ApiFamily == "My own family");
        });
    }

    [Fact]
    public void Notifications_keep_errors_until_dismissed()
    {
        Sta.Run(() =>
        {
            var notifications = new NotificationService();

            notifications.Error("Title", "Message", "Details");
            Assert.True(notifications.IsVisible);
            Assert.Equal("Details", notifications.Details);
            Assert.Equal(NotificationKind.Error, notifications.Kind);

            notifications.DismissCommand.Execute(null);
            Assert.False(notifications.IsVisible);
            return Task.CompletedTask;
        });
    }
}
