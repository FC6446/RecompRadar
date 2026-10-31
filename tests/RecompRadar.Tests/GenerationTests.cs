using System.Buffers.Binary;
using System.Text;
using RecompRadar.Recompilation.Generation;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

public class GenerationTests
{
    private static byte[] SmallElf() => SampleFiles.Mips32Elf(new MipsProgram().PrintInt(Reg.T0).ExitWith(0), bigEndian: false);

    [Fact]
    public void Generation_is_deterministic()
    {
        var a = NativeTestHelper.Generate(Ps2StyleSelfTest.BuildElf(), "selftest");
        var b = NativeTestHelper.Generate(Ps2StyleSelfTest.BuildElf(), "selftest");

        Assert.Equal(a.Files.Keys, b.Files.Keys);
        foreach (var (name, content) in a.Files)
        {
            Assert.True(content.AsSpan().SequenceEqual(b.Files[name]), $"{name} differs between two generations");
        }
    }

    [Fact]
    public void The_project_contains_the_expected_files_and_no_absolute_paths()
    {
        var project = NativeTestHelper.Generate(SmallElf(), "tiny");

        Assert.Equal(
            new[] { "BuildInfo.cs", "Directory.Build.props", "Directory.Build.targets", "GuestRuntime.cs", "IrSemantics.cs", "Program.cs", "Ps2Runtime.cs", "Recompiled.cs", "image.bin", "tiny.csproj" },
            project.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        foreach (var (name, content) in project.Files.Where(f => f.Key != "image.bin"))
        {
            var text = Encoding.UTF8.GetString(content);
            Assert.DoesNotContain(@"C:\", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_semantics_in_generated_programs_are_a_verbatim_copy_of_the_library_source()
    {
        var project = NativeTestHelper.Generate(SmallElf(), "tiny");

        var copied = Encoding.UTF8.GetString(project.Files["IrSemantics.cs"]);

        Assert.Contains("namespace RecompRadar.Semantics;", copied, StringComparison.Ordinal);
        Assert.Contains("public static class PocSystemCalls", copied, StringComparison.Ordinal);
        Assert.Contains("public static uint DivS(uint a, uint b)", copied, StringComparison.Ordinal);
    }

    [Fact]
    public void The_image_container_carries_every_segment_unchanged()
    {
        var elfBytes = Ps2StyleSelfTest.BuildElf();
        var image = NativeTestHelper.Load(elfBytes, out _);

        var container = ImageContainer.Write(image);

        Assert.True(container.AsSpan(0, 8).SequenceEqual("RRIMG2\0\0"u8));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(8)));            // little-endian guest
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(12)));            // RecompRadar test profile
        Assert.Equal((uint)image.EntryPoint, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(16)));
        Assert.Equal((uint)image.Segments.Count, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(20)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(ImageContainer.Write(image, RecompRadar.Recompilation.RuntimeProfile.Ps2Ee).AsSpan(12)));
        var offset = 24;
        foreach (var segment in image.Segments)
        {
            Assert.Equal((uint)segment.VirtualAddress, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(offset)));
            Assert.Equal((uint)segment.Data.Length, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(offset + 4)));
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(offset + 8));
            Assert.Equal(segment.Executable, (flags & 4) != 0);
            Assert.Equal(segment.Writable, (flags & 2) != 0);
            Assert.True(segment.Data.AsSpan().SequenceEqual(container.AsSpan(offset + 12, segment.Data.Length)));
            offset += 12 + segment.Data.Length;
        }

        Assert.Equal(container.Length, offset);
    }

    [Fact]
    public void An_incomplete_program_is_refused_unless_incomplete_builds_are_allowed()
    {
        var elf = SampleFiles.Mips32Elf(new MipsProgram().Beql(Reg.T0, Reg.T1, "x").Nop().Label("x").ExitWith(0), bigEndian: false);

        var ex = Assert.Throws<GenerationException>(() => NativeTestHelper.Generate(elf, "partial"));
        Assert.Contains("beql", ex.Message, StringComparison.Ordinal);

        var project = NativeTestHelper.Generate(elf, "partial", allowIncomplete: true);
        Assert.Equal(1, project.Stats.UnsupportedSites);
        Assert.Contains("throw new ProgramStopException(", Encoding.UTF8.GetString(project.Files["Recompiled.cs"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("my game.elf", "my_game_elf")]
    [InlineData("..\\..\\evil", "evil")]
    [InlineData("1abc", "P1abc")]
    [InlineData("", "Recompiled")]
    [InlineData("äöü", "Recompiled")]
    public void Project_names_are_made_safe_for_file_names(string input, string expected)
    {
        Assert.Equal(expected, CSharpGenerator.SanitizeName(input));
    }

    [Fact]
    public void String_literals_escape_everything_that_could_break_the_source()
    {
        var literal = CSharpGenerator.Literal("a\"b\\c\n\r\t{x}\u00E4\u0001");

        Assert.Equal("\"a\\\"b\\\\c\\n\\r\\t{x}\\u00E4\\u0001\"", literal);
    }

    [Fact]
    public void A_hostile_program_name_cannot_inject_code_into_the_generated_sources()
    {
        var project = NativeTestHelper.Generate(SmallElf(), "x\"; System.Diagnostics.Process.Start(\"calc\"); //");

        // The project and assembly name are reduced to safe characters ...
        Assert.StartsWith("x___System_Diagnostics_Process_Start", project.ProjectName, StringComparison.Ordinal);
        var csproj = Encoding.UTF8.GetString(project.Files[project.ProjectFile]);
        Assert.DoesNotContain("Process.Start", csproj, StringComparison.Ordinal);

        // ... and the original text only appears as an escaped string literal, never as code.
        var buildInfo = Encoding.UTF8.GetString(project.Files["BuildInfo.cs"]);
        Assert.Contains("x\\\"; System.Diagnostics.Process.Start(\\\"calc\\\")", buildInfo, StringComparison.Ordinal);
        foreach (var line in buildInfo.Split('\n').Where(l => l.Contains("Process.Start", StringComparison.Ordinal)))
        {
            var unescaped = line.Replace("\\\\", string.Empty, StringComparison.Ordinal).Replace("\\\"", string.Empty, StringComparison.Ordinal);
            Assert.Equal(2, unescaped.Count(c => c == '"')); // only the opening and closing quote of the literal remain
        }
    }

    [Fact]
    public void Writing_a_project_cannot_escape_the_target_directory()
    {
        var project = new GeneratedProject
        {
            ProjectName = "p",
            ProjectFile = "p.csproj",
            Files = new Dictionary<string, byte[]> { ["..\\escape.txt"] = [1] },
            Stats = new GenerationStats(0, 0, 0, 0, 0, 0, 0),
        };
        var target = Path.Combine(Path.GetTempPath(), "recompradar-escape-" + Guid.NewGuid().ToString("N")[..8], "inner");
        try
        {
            Assert.Throws<InvalidOperationException>(() => project.WriteTo(target));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(target)!, "escape.txt")));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(target)))
            {
                Directory.Delete(Path.GetDirectoryName(target)!, true);
            }
        }
    }
}
