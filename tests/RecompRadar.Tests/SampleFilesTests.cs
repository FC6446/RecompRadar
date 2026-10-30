using RecompRadar.Core.Model;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

public class SampleFilesTests
{
    [Fact]
    public void Committed_sample_files_match_the_generator_output()
    {
        var folder = Path.Combine(T.RepoRoot(), "samples");

        foreach (var sample in SampleFiles.All())
        {
            var path = Path.Combine(folder, sample.FileName);
            Assert.True(File.Exists(path), $"{sample.FileName} is missing from samples/. Run tools/RecompRadar.SampleGenerator.");
            Assert.Equal(sample.Bytes, File.ReadAllBytes(path));
        }

        var onDisk = Directory.GetFiles(folder).Select(Path.GetFileName).Where(n => n != "README.md").Order().ToList();
        Assert.Equal(SampleFiles.All().Select(s => s.FileName).Order(), onDisk);
    }

    [Fact]
    public void Samples_are_small_and_deterministic()
    {
        var first = SampleFiles.All();
        var second = SampleFiles.All();

        Assert.Equal(first.Select(s => s.Bytes.Length), second.Select(s => s.Bytes.Length));
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Bytes, second[i].Bytes);
        }

        Assert.All(first, s => Assert.True(s.Bytes.Length < 4096, s.FileName));
        Assert.All(first, s => Assert.False(string.IsNullOrWhiteSpace(s.Description)));
    }

    [Fact]
    public async Task Every_sample_can_be_analyzed_without_error_or_internal_failure()
    {
        foreach (var sample in SampleFiles.All())
        {
            var bundle = await T.AnalyzeAsync(sample.Bytes, sample.FileName);

            Assert.DoesNotContain(bundle.Analysis.Diagnostics, d => d.Code.EndsWith("INTERNAL_ERROR", StringComparison.Ordinal));
            Assert.Equal(6, bundle.Report.Questions.Count);
            Assert.False(string.IsNullOrEmpty(bundle.Analysis.File.Sha256));
        }
    }

    [Fact]
    public async Task Intact_samples_are_analyzed_without_any_warning()
    {
        foreach (var sample in SampleFiles.All().Where(s => !s.FileName.StartsWith("corrupt_", StringComparison.Ordinal) && s.FileName.EndsWith(".elf", StringComparison.Ordinal)))
        {
            var bundle = await T.AnalyzeAsync(sample.Bytes, sample.FileName);

            Assert.True(bundle.Analysis.Parsed, sample.FileName);
            Assert.DoesNotContain(bundle.Analysis.Diagnostics, d => d.Severity >= Severity.Warning);
        }
    }

    [Theory]
    [InlineData("poc_mips32_be_sum.elf", true, CpuFamily.Mips, 32, Endianness.Big)]
    [InlineData("poc_mips32_le_call.elf", true, CpuFamily.Mips, 32, Endianness.Little)]
    [InlineData("synthetic_x86_64_linux_dynamic.elf", true, CpuFamily.X86_64, 64, Endianness.Little)]
    [InlineData("synthetic_mips32_le_r5900_flag.elf", true, CpuFamily.Mips, 32, Endianness.Little)]
    [InlineData("synthetic_mips32_le_psp_prx_like.elf", true, CpuFamily.Mips, 32, Endianness.Little)]
    [InlineData("synthetic_ppc32_be_gamecube_like.elf", true, CpuFamily.PowerPc, 32, Endianness.Big)]
    [InlineData("synthetic_ppc64_be_ps3_like.elf", true, CpuFamily.PowerPc, 64, Endianness.Big)]
    [InlineData("synthetic_x86_64_ps4_like.elf", true, CpuFamily.X86_64, 64, Endianness.Little)]
    [InlineData("synthetic_aarch64_stripped.elf", true, CpuFamily.Arm64, 64, Endianness.Little)]
    [InlineData("corrupt_bad_offsets.elf", true, CpuFamily.X86_64, 64, Endianness.Little)]
    [InlineData("corrupt_truncated_header.elf", false, CpuFamily.Unknown, 0, Endianness.Little)]
    [InlineData("not_an_elf.bin", false, CpuFamily.Unknown, 0, Endianness.Little)]
    public async Task Samples_produce_the_documented_architecture(string name, bool parsed, CpuFamily family, int bits, Endianness endianness)
    {
        var bundle = await T.AnalyzeAsync(SampleFiles.All().Single(s => s.FileName == name).Bytes, name);

        Assert.Equal(parsed, bundle.Analysis.Parsed);
        if (parsed)
        {
            Assert.Equal(family, bundle.Analysis.Architecture!.Family);
            Assert.Equal(bits, bundle.Analysis.Architecture.PointerBits);
            Assert.Equal(endianness, bundle.Analysis.Architecture.Endianness);
        }
        else
        {
            Assert.Null(bundle.Analysis.Architecture);
            Assert.Contains(bundle.Analysis.Diagnostics, d => d.Severity >= Severity.Warning);
        }
    }

    [Fact]
    public async Task Damaged_samples_report_the_problem_in_plain_language()
    {
        var offsets = await T.AnalyzeAsync(SampleFiles.All().Single(s => s.FileName == "corrupt_bad_offsets.elf").Bytes);
        var truncated = await T.AnalyzeAsync(SampleFiles.All().Single(s => s.FileName == "corrupt_truncated_header.elf").Bytes);

        Assert.Contains(offsets.Analysis.Diagnostics, d => d.Code == "ELF.PH_TABLE" && d.Message.Contains("outside the file") && d.Explanation is not null);
        Assert.Contains(truncated.Analysis.Diagnostics, d => d.Code == "ELF.TRUNCATED_HEADER" && d.Explanation!.Contains("cut off"));
    }

    [Fact]
    public async Task Random_mutations_of_samples_never_break_the_full_pipeline()
    {
        var random = new Random(424242);
        var samples = SampleFiles.All();
        using var dir = new TempDir();
        var pipeline = new RecompRadar.Portability.AnalysisPipeline();

        for (var i = 0; i < 150; i++)
        {
            var data = (byte[])samples[i % samples.Count].Bytes.Clone();
            for (var f = 0; f < random.Next(1, 12); f++)
            {
                data[random.Next(Math.Min(data.Length, 200))] = (byte)random.Next(256);
            }

            var path = dir.Write($"m{i}.bin", data);
            var bundle = await pipeline.AnalyzeAsync(path, new RecompRadar.Core.AnalysisOptions());

            Assert.DoesNotContain(bundle.Analysis.Diagnostics, d => d.Code.EndsWith("INTERNAL_ERROR", StringComparison.Ordinal));
            Assert.Equal(6, bundle.Report.Questions.Count);
        }
    }
}
