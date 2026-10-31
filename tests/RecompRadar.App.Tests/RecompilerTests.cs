using RecompRadar.App.Services;
using RecompRadar.App.ViewModels;
using RecompRadar.Recompilation.Build;
using RecompRadar.Recompilation.Compatibility;
using RecompRadar.Recompilation.Pipeline;

namespace RecompRadar.App.Tests;

[Collection("WPF")]
public class RecompilerPageTests
{
    private const string Mips = "poc_mips32_le_call.elf";

    private static async Task LoadAsync(Harness h, string sample)
    {
        await h.Session.AnalyzeAsync(h.Sample(sample));
        await Harness.WaitUntilAsync(() => !h.CodeService.IsRunning);
    }

    [Fact]
    public void Without_a_file_nothing_can_be_built_or_played_and_the_reasons_say_why()
    {
        Sta.Run(() =>
        {
            using var h = new Harness();
            var page = h.RecompilerPage;

            Assert.False(page.HasFile);
            Assert.False(page.RecompileCommand.CanExecute(null));
            Assert.Contains("Analyze an ELF file first", page.RecompileDisabledReason, StringComparison.Ordinal);
            Assert.False(page.PlayCommand.CanExecute(null));
            Assert.Contains("Nothing has been built yet", page.PlayDisabledReason, StringComparison.Ordinal);
            Assert.Equal(CompatibilityLevel.Unknown, page.Level);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void A_self_made_mips_program_is_experimental_and_can_be_built()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, Mips);
            var page = h.RecompilerPage;

            Assert.True(page.HasFile);
            Assert.Equal(CompatibilityLevel.Experimental, page.Level);
            Assert.True(page.RecompileCommand.CanExecute(null), page.RecompileDisabledReason);
            Assert.Equal(string.Empty, page.RecompileDisabledReason);
            Assert.False(page.PlayCommand.CanExecute(null));
            Assert.Contains(page.Notes, n => n.Kind == "Info" && n.Text.Contains("No console kernel", StringComparison.Ordinal));
            Assert.EndsWith("poc_mips32_le_call", page.OutputDirectory, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Recompile_runs_the_pipeline_with_the_chosen_options_and_enables_play_only_afterwards()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, Mips);
            var page = h.RecompilerPage;
            page.Validate = false;
            page.SelectedPlatform = page.PlatformChoices.Single(p => p.Id == "ps2");
            h.Recompiler.BuildGate = new TaskCompletionSource();

            var running = page.RecompileCommand.ExecuteAsync(null);

            Assert.True(page.IsBuilding);
            Assert.False(page.RecompileCommand.CanExecute(null));
            Assert.Equal("A build is running.", page.RecompileDisabledReason);
            Assert.False(page.PlayCommand.CanExecute(null));
            Assert.True(page.CancelBuildCommand.CanExecute(null));
            await Harness.WaitUntilAsync(() => page.Stages.Any(s => s.Status == "running"));
            Assert.Equal(7, page.Stages.Count);

            h.Recompiler.BuildGate.SetResult();
            await running;

            Assert.False(page.IsBuilding);
            Assert.Equal(1, h.Recompiler.RecompileCalls);
            var request = h.Recompiler.LastRequest!;
            Assert.False(request.Validate);
            Assert.Equal("ps2", request.PlatformOverride);
            Assert.False(request.AllowIncomplete);
            Assert.Equal(page.OutputDirectory, request.OutputDirectory);
            Assert.EndsWith(Mips, request.SourcePath, StringComparison.Ordinal);

            Assert.True(page.HasResult);
            Assert.True(page.ResultSucceeded);
            Assert.Equal("Program built", page.ResultTitle);
            Assert.Equal(BuildCategory.TranslatedWithRuntime.Title(), page.ResultCategory);
            Assert.Contains(page.ResultRows, r => r.Name == "Program SHA-256");
            Assert.True(page.PlayCommand.CanExecute(null), page.PlayDisabledReason);
            Assert.True(page.OpenBuildFolderCommand.CanExecute(null));
            Assert.True(page.OpenReportCommand.CanExecute(null));
            Assert.All(page.Stages.Where(s => s.Status != "skipped"), s => Assert.Equal("done", s.Status));
        });
    }

    [Fact]
    public void Play_starts_the_built_program_and_shows_its_output_and_the_summary()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, Mips);
            var page = h.RecompilerPage;
            await page.RecompileCommand.ExecuteAsync(null);

            await page.PlayCommand.ExecuteAsync(null);

            Assert.Equal(1, h.Recompiler.RunCalls);
            Assert.EndsWith("fake.exe", h.Recompiler.LastRunPath, StringComparison.Ordinal);
            await Harness.WaitUntilAsync(() => page.RunLines.Count == 2);
            Assert.Contains("hello from the built program", page.RunLines);
            Assert.Contains("[error] a warning", page.RunLines);
            Assert.StartsWith("Exited", page.RunSummary, StringComparison.Ordinal);
            Assert.Matches(@"1\D{0,2}234 guest instructions", page.RunSummary);
            Assert.False(page.IsRunning);
        });
    }

    [Fact]
    public void Stop_ends_a_running_program_and_Play_is_blocked_while_it_runs()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, Mips);
            var page = h.RecompilerPage;
            await page.RecompileCommand.ExecuteAsync(null);
            h.Recompiler.HoldRun = true;

            var running = page.PlayCommand.ExecuteAsync(null);

            Assert.True(page.IsRunning);
            Assert.False(page.PlayCommand.CanExecute(null));
            Assert.Equal("The program is already running.", page.PlayDisabledReason);
            Assert.True(page.StopCommand.CanExecute(null));

            page.StopCommand.Execute(null);
            await running;

            Assert.False(page.IsRunning);
            Assert.Equal("Stopped by you.", page.RunSummary);
            Assert.False(page.StopCommand.CanExecute(null));
            Assert.True(page.PlayCommand.CanExecute(null));
        });
    }

    [Fact]
    public void A_failed_build_leaves_Play_disabled_with_the_failure_as_the_reason()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, Mips);
            var page = h.RecompilerPage;
            h.Recompiler.ResultFactory = _ => new RecompileResult
            {
                Success = false,
                Category = BuildCategory.Unsupported,
                FailureReason = "The build failed with 2 error(s) (dotnet exit code 1).",
                Stages = [new StageOutcome(PipelineStage.Build, false, TimeSpan.Zero, "build failed")],
            };

            await page.RecompileCommand.ExecuteAsync(null);

            Assert.True(page.HasResult);
            Assert.False(page.ResultSucceeded);
            Assert.Equal("No program was produced", page.ResultTitle);
            Assert.Equal(BuildCategory.Unsupported.Title(), page.ResultCategory);
            Assert.False(page.PlayCommand.CanExecute(null));
            Assert.Contains("2 error(s)", page.PlayDisabledReason, StringComparison.Ordinal);
            Assert.False(page.OpenBuildFolderCommand.CanExecute(null));
        });
    }

    [Fact]
    public void A_program_that_failed_validation_is_not_started()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, Mips);
            var page = h.RecompilerPage;
            h.Recompiler.ResultFactory = request =>
            {
                var ok = h.Recompiler.Succeeded(request);
                var reference = new RecompRadar.Recompilation.ExecutionResult(RecompRadar.Recompilation.ExecutionStatus.Exited, 0, 10, "x", null, new uint[128]);
                var native = new RunReport("Faulted", 70, 10, "boom", "x", new uint[128]);
                return new RecompileResult
                {
                    Success = false,
                    Category = ok.Category,
                    FailureReason = "The built program differs from the reference interpreter.",
                    Build = ok.Build,
                    Compatibility = ok.Compatibility,
                    Validation = DifferentialValidator.Compare(reference, native),
                    Stages = ok.Stages,
                    ProjectDirectory = ok.ProjectDirectory,
                    ExecutablePath = ok.ExecutablePath,
                };
            };

            await page.RecompileCommand.ExecuteAsync(null);

            Assert.Equal("Program built but not trusted", page.ResultTitle);
            Assert.Contains("Differs from the reference interpreter", page.ValidationText, StringComparison.Ordinal);
            Assert.False(page.PlayCommand.CanExecute(null));
            Assert.Contains("differently", page.PlayDisabledReason, StringComparison.Ordinal);
            Assert.Equal(0, h.Recompiler.RunCalls);
        });
    }

    [Fact]
    public void A_file_for_an_unimplemented_console_cannot_be_built_and_says_why()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, "synthetic_ppc64_be_ps3_like.elf");
            var page = h.RecompilerPage;

            Assert.Equal(CompatibilityLevel.Unsupported, page.Level);
            Assert.False(page.RecompileCommand.CanExecute(null));
            Assert.Contains("PowerPC", page.RecompileDisabledReason, StringComparison.Ordinal);
            Assert.False(page.PlayCommand.CanExecute(null));
            Assert.False(page.CanOfferIncomplete);
            Assert.Equal(0, h.Recompiler.RecompileCalls);
        });
    }

    [Fact]
    public void A_manual_platform_choice_cannot_make_an_unsupported_file_buildable()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, "synthetic_x86_64_ps4_like.elf");
            var page = h.RecompilerPage;

            foreach (var choice in page.PlatformChoices)
            {
                page.SelectedPlatform = choice;
                Assert.NotEqual(CompatibilityLevel.Experimental, page.Level);
                Assert.False(page.RecompileCommand.CanExecute(null), $"choice {choice.Label} must not enable Recompile");
            }

            page.SelectedPlatform = page.PlatformChoices.Single(p => p.Id == "ps2");
            Assert.Equal(CompatibilityLevel.Unsupported, page.Level);
            Assert.Contains("cannot change that", page.RecompileDisabledReason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Without_a_dotnet_sdk_building_is_disabled_with_the_install_hint()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            h.Recompiler.Sdk = null;
            var page = new RecompilerViewModel(h.Session, h.CodeService, h.Recompiler, h.Dialogs, h.Shell, h.Notifications,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RecompilerViewModel>.Instance);
            await LoadAsync(h, Mips);

            Assert.False(page.SdkAvailable);
            Assert.False(page.RecompileCommand.CanExecute(null));
            Assert.Contains("No .NET SDK", page.RecompileDisabledReason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Choosing_an_output_folder_updates_the_build_target()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, Mips);
            h.Dialogs.Folder = Path.Combine(Path.GetTempPath(), "recompradar-chosen");

            h.RecompilerPage.BrowseOutputCommand.Execute(null);

            Assert.Equal(h.Dialogs.Folder, h.RecompilerPage.OutputDirectory);
        });
    }

    [Fact]
    public void Loading_another_file_clears_the_previous_build()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, Mips);
            var page = h.RecompilerPage;
            await page.RecompileCommand.ExecuteAsync(null);
            Assert.True(page.PlayCommand.CanExecute(null));

            await LoadAsync(h, "synthetic_x86_64_ps4_like.elf");

            Assert.False(page.HasResult);
            Assert.False(page.PlayCommand.CanExecute(null));
            Assert.Contains("Nothing has been built yet", page.PlayDisabledReason, StringComparison.Ordinal);
            Assert.Empty(page.Stages);
        });
    }
}

[Collection("WPF")]
public class DisassemblerAndConsolePageTests
{
    private static async Task LoadAsync(Harness h, string sample)
    {
        await h.Session.AnalyzeAsync(h.Sample(sample));
        await Harness.WaitUntilAsync(() => !h.CodeService.IsRunning);
    }

    [Fact]
    public void The_disassembler_shows_real_functions_blocks_and_instructions()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, "poc_mips32_le_call.elf");
            var page = h.Disassembler;

            Assert.True(page.HasResult);
            Assert.NotEmpty(page.Functions);
            Assert.Contains(page.Summary, r => r.Name == "Instructions" && r.Evidence == "Verified");
            Assert.Contains(page.Summary, r => r.Name == "Functions" && r.Evidence == "Inferred");
            Assert.NotNull(page.SelectedFunction);
            Assert.NotEmpty(page.Blocks);
            Assert.NotNull(page.SelectedBlock);
            Assert.NotEmpty(page.Listing);
            Assert.All(page.Listing.Where(l => l.Address.Length > 0), l => Assert.Matches("^0x[0-9A-F]{8}$", l.Address));
            Assert.Contains("block 0x", page.IrText, StringComparison.Ordinal);
            Assert.NotEmpty(page.Uncertainties);
            Assert.Contains(page.Uncertainties, u => u.Contains("inferred", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void Kernel_calls_are_shown_by_name_and_the_ps2_hint_selects_the_ps2_profile()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, "ps2_kernel_selftest.elf");

            Assert.Contains(h.Disassembler.Syscalls, s => s.Meaning == "PlayStation 2 kernel call CreateSema (implemented as a subset)" && s.Number == "0x40");
            Assert.Contains(h.Disassembler.Syscalls, s => s.Meaning == "RecompRadar test call: write integer");
            Assert.Equal("ps2", h.RecompilerPage.Compatibility!.PlatformId);
            Assert.Equal(RecompRadar.Recompilation.RuntimeProfile.Ps2Ee, h.RecompilerPage.Compatibility.Profile);
            Assert.Equal(RecompRadar.Recompilation.Compatibility.CompatibilityLevel.Experimental, h.RecompilerPage.Level);
            Assert.Contains(h.RecompilerPage.Notes, n => n.Text.Contains("kernel call site(s) use implemented calls", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Selecting_another_function_shows_its_own_blocks()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, "poc_mips32_le_call.elf");
            var page = h.Disassembler;
            Assert.True(page.Functions.Count >= 2, "the call sample has a caller and a callee");

            var first = page.Functions[0];
            var second = page.Functions[1];
            page.SelectedFunction = second;

            Assert.Equal(second.Blocks, page.Blocks.Count);
            Assert.Equal(second.Entry, page.Blocks[0].Start);
            Assert.NotEqual(first.Entry, page.Blocks[0].Start);
            Assert.Contains(page.Calls, c => c.Callee == second.Name);
        });
    }

    [Fact]
    public void The_function_filter_narrows_the_list()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, "poc_mips32_le_call.elf");
            var page = h.Disassembler;
            var total = page.FunctionsView.Cast<object>().Count();

            page.FunctionFilter = page.Functions[0].Name;

            Assert.True(page.FunctionsView.Cast<object>().Count() < total);
            page.FunctionFilter = "zzz-no-such-function";
            Assert.Empty(page.FunctionsView.Cast<object>());
        });
    }

    [Fact]
    public void An_architecture_without_a_decoder_shows_the_reason_instead_of_guesses()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, "synthetic_ppc64_be_ps3_like.elf");

            Assert.False(h.Disassembler.HasResult);
            Assert.Empty(h.Disassembler.Functions);
            Assert.Contains("No decoder and translator", h.CodeService.Status, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_deleted_original_file_is_reported_not_silently_ignored()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            var path = h.Sample("poc_mips32_le_call.elf");
            await h.Session.AnalyzeAsync(path);
            await Harness.WaitUntilAsync(() => !h.CodeService.IsRunning);
            Assert.True(h.Disassembler.HasResult);

            File.Delete(path);
            await h.CodeService.RunAsync();

            Assert.False(h.Disassembler.HasResult);
            Assert.Contains("no longer at its recorded location", h.CodeService.Status, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_console_page_lists_every_backend_with_honest_status_and_the_detected_hints()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            await LoadAsync(h, "synthetic_mips32_le_r5900_flag.elf");
            var page = h.Consoles;

            Assert.Equal(4, page.Backends.Count);
            Assert.True(page.Backends.Single(b => b.Id == "ps2").HasPipeline);
            Assert.False(page.Backends.Single(b => b.Id == "ps3").HasPipeline);
            Assert.False(page.Backends.Single(b => b.Id == "ps4").HasPipeline);
            Assert.All(page.Backends.Where(b => !b.HasPipeline), b => Assert.DoesNotContain(b.Modules, m => m.State == "Implemented" && !m.Name.Contains("analysis", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(page.Backends.Single(b => b.Id == "ps2").Modules, m => m.Name.StartsWith("Graphics Synthesizer", StringComparison.Ordinal) && m.State == "Planned");
            Assert.True(page.HasResult);
            Assert.NotEmpty(page.Candidates);
            Assert.Contains("not proof", page.DetectionDisclaimer, StringComparison.Ordinal);
        });
    }
}
