using System.Text;

namespace RecompRadar.TestSupport;

/// <summary>A named, self-made sample file together with a note on what it demonstrates.</summary>
public sealed record SampleFile(string FileName, string Description, byte[] Bytes);

/// <summary>
/// Factory for synthetic ELF files. Only headers, tables and tiny self-written programs are produced; the files mimic
/// header characteristics of different systems so the analyzer can be demonstrated, but they contain no code or data from any real product.
/// </summary>
public static class SampleFiles
{
    public const ulong CodeBase = 0x00400000;

    /// <summary>Sum of 1..10 printed with the PoC runtime's write-int syscall, then exit(0). Output: "55\n".</summary>
    public static MipsProgram SumProgram() => new MipsProgram()
        .Addiu(Reg.T0, Reg.Zero, 0)
        .Addiu(Reg.T1, Reg.Zero, 1)
        .Addiu(Reg.T2, Reg.Zero, 11)
        .Label("loop")
        .Addu(Reg.T0, Reg.T0, Reg.T1)
        .Addiu(Reg.T1, Reg.T1, 1)
        .Bne(Reg.T1, Reg.T2, "loop")
        .Nop()
        .Move(Reg.A0, Reg.T0)
        .Addiu(Reg.V0, Reg.Zero, 3)
        .Syscall()
        .Addiu(Reg.A0, Reg.Zero, 0)
        .Addiu(Reg.V0, Reg.Zero, 1)
        .Syscall();

    /// <summary>Calls a function that doubles its argument (jal / jr with delay slots), prints 42 and exits with code 7.</summary>
    public static MipsProgram CallProgram() => new MipsProgram()
        .Addiu(Reg.A0, Reg.Zero, 21)
        .Jal("double")
        .Nop()
        .Move(Reg.A0, Reg.V0)
        .Addiu(Reg.V0, Reg.Zero, 3)
        .Syscall()
        .Addiu(Reg.A0, Reg.Zero, 7)
        .Addiu(Reg.V0, Reg.Zero, 1)
        .Syscall()
        .Label("double")
        .Addu(Reg.V0, Reg.A0, Reg.A0)
        .Jr(Reg.Ra)
        .Nop();

    public static byte[] Mips32Elf(MipsProgram program, bool bigEndian, uint flags = 0x50001000, ushort type = 2, ulong address = CodeBase)
    {
        var builder = new ElfBuilder
        {
            Is64 = false,
            BigEndian = bigEndian,
            Machine = 8,
            Type = type,
            Flags = flags,
        };
        builder.WithCode(program.ToBytes(address, bigEndian), address);
        return builder.Build();
    }

    public static IReadOnlyList<SampleFile> All()
    {
        var list = new List<SampleFile>();

        list.Add(new SampleFile("poc_mips32_be_sum.elf",
            "MIPS32 big-endian program (own code): sums 1..10, prints 55, exits. Runs on the proof-of-concept interpreter.",
            Mips32Elf(SumProgram(), bigEndian: true)));

        list.Add(new SampleFile("poc_mips32_le_call.elf",
            "MIPS32 little-endian program (own code): function call with delay slots, prints 42, exits with code 7.",
            Mips32Elf(CallProgram(), bigEndian: false)));

        var x64 = ElfBuilder.X64Linux();
        x64.OsAbi = 0;
        x64.Interpreter = "/lib64/ld-linux-x86-64.so.2";
        x64.NeededLibraries.AddRange(["libc.so.6", "libm.so.6", "libSDL2-2.0.so.0", "libGL.so.1"]);
        x64.DynamicSymbols.AddRange(
        [
            SymbolSpec.Import("printf"), SymbolSpec.Import("malloc"), SymbolSpec.Import("pthread_create"),
            SymbolSpec.Import("SDL_Init"), SymbolSpec.Import("SDL_CreateWindow"), SymbolSpec.Import("glClear"),
            SymbolSpec.Import("ioctl"), SymbolSpec.Import("__libc_start_main"),
            SymbolSpec.Function("main", CodeBase),
        ]);
        x64.Symbols.AddRange([SymbolSpec.Function("main", CodeBase, 16), SymbolSpec.Function("_start", CodeBase)]);
        x64.WithCode([0x31, 0xC0, 0xC3], CodeBase);
        x64.WithData(Encoding.ASCII.GetBytes("Hello from a synthetic sample\0/dev/fb0\0"), 0x500000);
        list.Add(new SampleFile("synthetic_x86_64_linux_dynamic.elf",
            "Synthetic x86-64 Linux-style dynamic executable with libc/SDL/OpenGL imports and a /dev/fb0 string. Native instruction set.",
            x64.Build()));

        var ps2 = new ElfBuilder { Machine = 8, Flags = 0x20924001 };
        ps2.WithCode(SumProgram().ToBytes(0x00100000, bigEndian: false), 0x00100000);
        ps2.WithData(Encoding.ASCII.GetBytes("cdrom0:\\SLUS_000.00;1\0host0:test\0"), 0x00200000);
        ps2.Symbols.AddRange([SymbolSpec.Function("main", 0x00100000, 64), SymbolSpec.Function("SifInitRpc"), SymbolSpec.Function("sceGsResetPath"), SymbolSpec.Function("scePadInit")]);
        list.Add(new SampleFile("synthetic_mips32_le_r5900_flag.elf",
            "Synthetic MIPS32 little-endian ELF whose e_flags carry the R5900 CPU variant, with PS2-SDK-style symbol names. Header characteristics only; the code is the generic sum program.",
            ps2.Build()));

        var psp = new ElfBuilder { Machine = 8, Type = 0xFFA0, Flags = 0x10A23001 };
        psp.WithCode(SumProgram().ToBytes(0x08804000, bigEndian: false), 0x08804000);
        psp.Symbols.AddRange([SymbolSpec.Function("sceGuInit"), SymbolSpec.Function("sceDisplayWaitVblankStart"), SymbolSpec.Function("sceKernelExitGame"), SymbolSpec.Function("module_start", 0x08804000)]);
        psp.WithData(Encoding.ASCII.GetBytes("ms0:/PSP/GAME/test\0"), 0x08900000);
        list.Add(new SampleFile("synthetic_mips32_le_psp_prx_like.elf",
            "Synthetic MIPS32 little-endian module with e_type 0xFFA0, PSP-toolchain flags, PSP-memory-range addresses and sce* symbol names. Header characteristics only.",
            psp.Build()));

        var gc = new ElfBuilder { Machine = 20, BigEndian = true };
        gc.WithCode([0x4E, 0x80, 0x00, 0x20], 0x80003100);
        gc.Symbols.AddRange([SymbolSpec.Function("main", 0x80003100), SymbolSpec.Function("GX_Init"), SymbolSpec.Function("VIDEO_Init"), SymbolSpec.Function("PAD_Init")]);
        list.Add(new SampleFile("synthetic_ppc32_be_gamecube_like.elf",
            "Synthetic PowerPC 32-bit big-endian ELF at 0x80003100 with libogc-style symbol names (a single blr instruction). Header characteristics only.",
            gc.Build()));

        var ps3 = new ElfBuilder { Is64 = true, BigEndian = true, Machine = 21, OsAbi = 0x66 };
        ps3.WithCode([0x4E, 0x80, 0x00, 0x20], 0x10200);
        ps3.Symbols.AddRange([SymbolSpec.Function("main", 0x10200), SymbolSpec.Function("cellGcmInit"), SymbolSpec.Function("sys_ppu_thread_create")]);
        list.Add(new SampleFile("synthetic_ppc64_be_ps3_like.elf",
            "Synthetic PowerPC 64-bit big-endian ELF with OS/ABI 0x66 and Cell-SDK-style symbol names (a single blr instruction). Header characteristics only.",
            ps3.Build()));

        var ps4 = new ElfBuilder { Is64 = true, Machine = 62, OsAbi = 9, Type = 0xFE10 };
        ps4.NeededLibraries.AddRange(["libkernel.sprx", "libSceGnmDriver.sprx", "libSceVideoOut.sprx"]);
        ps4.DynamicSymbols.AddRange([SymbolSpec.Import("sceVideoOutOpen"), SymbolSpec.Import("sceGnmSubmitCommandBuffers")]);
        ps4.WithCode([0xC3], 0x400000);
        list.Add(new SampleFile("synthetic_x86_64_ps4_like.elf",
            "Synthetic x86-64 ELF with FreeBSD OS/ABI, e_type 0xFE10 and libSce* dependencies. Header characteristics only; shows that the CPU code needs no translation.",
            ps4.Build()));

        var stripped = new ElfBuilder { Is64 = true, Machine = 183, OmitSectionHeaders = true };
        stripped.WithCode([0xC0, 0x03, 0x5F, 0xD6], 0x400000);
        list.Add(new SampleFile("synthetic_aarch64_stripped.elf",
            "Synthetic AArch64 ELF without section headers or symbols (a single ret instruction). Demonstrates the 'no symbols' handling.",
            stripped.Build()));

        var truncated = ElfBuilder.X64Linux().WithCode(new byte[256]).Build();
        list.Add(new SampleFile("corrupt_truncated_header.elf",
            "A valid ELF cut off after 40 bytes, inside its header. Demonstrates safe handling of damaged input.",
            truncated[..40]));

        var badOffsets = ElfBuilder.X64Linux().WithCode(new byte[64]).Build();
        BitConverter.GetBytes(0x7FFFFFFFFFFFFF00UL).CopyTo(badOffsets, 0x20); // e_phoff far outside the file
        BitConverter.GetBytes((ulong)badOffsets.Length - 8).CopyTo(badOffsets, 0x28); // e_shoff near the end
        list.Add(new SampleFile("corrupt_bad_offsets.elf",
            "A structurally valid ELF header whose program/section header offsets point outside the file. Demonstrates offset validation.",
            badOffsets));

        list.Add(new SampleFile("not_an_elf.bin",
            "Plain text that is not an executable. Demonstrates the unknown-format path.",
            Encoding.ASCII.GetBytes("This file is intentionally not an executable. RecompRadar should say so clearly.\n")));

        return list;
    }
}
