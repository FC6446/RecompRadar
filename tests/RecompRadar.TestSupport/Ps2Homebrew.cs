using System.Text;

namespace RecompRadar.TestSupport;

/// <summary>
/// A self-made program for the PlayStation 2 runtime profile (no third-party code). It uses the EE kernel call convention
/// (number in $v1) for semaphores, threads, priorities, rotate, sleep/wakeup and exit, prints text through the serial port
/// (a byte stored at the uncached mirror of SIO_TXFIFO), checks the KSEG0/KSEG1 mirrors and the scratchpad, and prints
/// numbers with the RecompRadar test call ($v1 = 0).
/// <see cref="ExpectedOutput"/> was derived by hand from the scheduling rules of <c>Ps2Kernel</c> (lower priority number runs
/// first, equal priorities in order of becoming ready, preemption only at kernel calls); the derivation is in the comments of
/// <see cref="BuildProgram"/>.
/// </summary>
public static class Ps2KernelSelfTest
{
    public const ulong CodeAddress = 0x00100000;
    public const uint Data = 0x00300000;
    private const int DataSize = 0x2000;

    private const uint SemaParam = Data + 0x400;
    private const uint SemaIdVariable = Data + 0x600;
    private const uint Thread1Param = Data + 0x440;
    private const uint Thread2Param = Data + 0x470;
    private const uint Thread3Param = Data + 0x4A0;
    private const uint Stack1 = Data + 0x1000;
    private const uint Stack2 = Data + 0x1400;
    private const uint Stack3 = Data + 0x1800;
    private const uint MirrorWord = Data + 0x800;

    // Kernel call numbers from ps2sdk's syscallnr.h.
    private const int KExit = 4, CreateThread = 0x20, DeleteThread = 0x21, StartThread = 0x22, ExitThread = 0x23, ChangeThreadPriority = 0x29;
    private const int RotateThreadReadyQueue = 0x2B, SleepThread = 0x32, WakeupThread = 0x33, CreateSema = 0x40, SignalSema = 0x42, WaitSema = 0x44, PollSema = 0x45;

    private static readonly string[] Texts =
    [
        "ee kernel selftest\n", "sema=", "t1=", "t2=", "main: waiting\n", "\nmain: signaled\n", "main: second signal\n", "deleted=", "poll=",
        "kseg0=", "kseg1=", "scratchpad=", "main: workers finished\n", "t3=", "main: waking sleeper\n", "main: waking sleeper again\n", "main: done\n",
        "sleeper: sleeping\n", "sleeper: woke\n", "sleeper: woke again\n",
    ];

    private static uint TextAddress(string text)
    {
        var offset = 0u;
        foreach (var t in Texts)
        {
            if (t == text)
            {
                return Data + offset;
            }

            offset += (uint)t.Length + 1;
        }

        throw new ArgumentException("unknown text " + text);
    }

    public static byte[] BuildElf()
    {
        // The first pass only discovers label addresses; every instruction has a fixed size, so the second pass lays out identically.
        var first = BuildProgram(new Dictionary<string, uint>());
        var addresses = new Dictionary<string, uint>();
        foreach (var name in new[] { "main", "worker", "sleeper", "sio_putc", "sio_puts" })
        {
            addresses[name] = (uint)first.AddressOf(name, CodeAddress);
        }

        var program = BuildProgram(addresses);
        var data = new byte[DataSize];
        var cursor = 0;
        foreach (var text in Texts)
        {
            Encoding.ASCII.GetBytes(text).CopyTo(data, cursor);
            cursor += text.Length + 1;
        }

        var builder = new ElfBuilder { Machine = 8, Flags = 0x20924001 };
        builder.WithCode(program.ToBytes(CodeAddress, bigEndian: false), CodeAddress);
        builder.WithData(data, Data, ".data", flags: 6);
        foreach (var (name, address) in addresses)
        {
            builder.Symbols.Add(SymbolSpec.Function(name, address));
        }

        return builder.Build();
    }

    private static MipsProgram Print(MipsProgram p, string text) => p.Li32(Reg.A0, TextAddress(text)).Jal("sio_puts").Nop();

    private static MipsProgram Kernel(MipsProgram p, int number) => p.Ee(number);

    private static MipsProgram ThreadParam(MipsProgram p, uint param, uint entry, uint stack, uint size, int priority) =>
        p.Li32(Reg.T0, param)
            .Li32(Reg.T1, entry).Sw(Reg.T1, 4, Reg.T0)
            .Li32(Reg.T1, stack).Sw(Reg.T1, 8, Reg.T0)
            .Li32(Reg.T1, size).Sw(Reg.T1, 0xC, Reg.T0)
            .Sw(Reg.Zero, 0x10, Reg.T0)
            .Li32(Reg.T1, (uint)priority).Sw(Reg.T1, 0x14, Reg.T0).Sw(Reg.T1, 0x18, Reg.T0);

    public static MipsProgram BuildProgram(IReadOnlyDictionary<string, uint> labels)
    {
        uint Entry(string name) => labels.TryGetValue(name, out var a) ? a : 0;
        var p = new MipsProgram();

        // ---- main thread (priority 1) ----
        p.Label("main");
        Print(p, "ee kernel selftest\n");
        p.Li32(Reg.T0, SemaParam).Addiu(Reg.T1, Reg.Zero, 1).Sw(Reg.T1, 4, Reg.T0);        // max_count = 1, init_count = 0
        p.Move(Reg.A0, Reg.T0);
        Kernel(p, CreateSema).Move(Reg.S0, Reg.V0).Li32(Reg.T1, SemaIdVariable).Sw(Reg.S0, 0, Reg.T1);
        Print(p, "sema=").PrintIntEe(Reg.S0);

        ThreadParam(p, Thread1Param, Entry("worker"), Stack1, 0x400, 3).Li32(Reg.A0, Thread1Param);
        Kernel(p, CreateThread).Move(Reg.S1, Reg.V0);
        Print(p, "t1=").PrintIntEe(Reg.S1);
        ThreadParam(p, Thread2Param, Entry("worker"), Stack2, 0x400, 3).Li32(Reg.A0, Thread2Param);
        Kernel(p, CreateThread).Move(Reg.S2, Reg.V0);
        Print(p, "t2=").PrintIntEe(Reg.S2);

        // Two workers of priority 3 become ready; main (priority 1) keeps running because it has the higher priority.
        p.Move(Reg.A0, Reg.S1).Addiu(Reg.A1, Reg.Zero, 'A');
        Kernel(p, StartThread);
        p.Move(Reg.A0, Reg.S2).Addiu(Reg.A1, Reg.Zero, 'B');
        Kernel(p, StartThread);
        Print(p, "main: waiting\n");

        // main blocks on the empty semaphore. The workers alternate through RotateThreadReadyQueue (A B A B); the first worker's
        // SignalSema wakes main, which preempts it at once. Output so far: "ABAB".
        p.Move(Reg.A0, Reg.S0);
        Kernel(p, WaitSema);
        Print(p, "\nmain: signaled\n");

        // main blocks again: worker 1 (preempted, at the front of its priority group) finishes, worker 2 signals and main preempts it.
        p.Move(Reg.A0, Reg.S0);
        Kernel(p, WaitSema);
        Print(p, "main: second signal\n");

        p.Move(Reg.A0, Reg.S1);
        Kernel(p, DeleteThread).Move(Reg.S3, Reg.V0);
        Print(p, "deleted=").PrintIntEe(Reg.S3);                                          // worker 1 is dormant: its id comes back
        p.Move(Reg.A0, Reg.S0);
        Kernel(p, PollSema).Move(Reg.S3, Reg.V0);
        Print(p, "poll=").PrintIntEe(Reg.S3);                                             // the semaphore is empty: -1

        // EE memory map: RAM seen through KSEG0 and KSEG1, and the scratchpad.
        p.Li32(Reg.T0, MirrorWord).Li32(Reg.T1, 12345678).Sw(Reg.T1, 0, Reg.T0);
        p.Li32(Reg.T2, 0x80000000 | MirrorWord).Lw(Reg.S4, 0, Reg.T2);
        Print(p, "kseg0=").PrintIntEe(Reg.S4);
        p.Li32(Reg.T2, 0xA0000000 | MirrorWord).Lw(Reg.S4, 0, Reg.T2);
        Print(p, "kseg1=").PrintIntEe(Reg.S4);
        p.Li32(Reg.T2, 0x70000010).Li32(Reg.T1, 777).Sw(Reg.T1, 0, Reg.T2).Lw(Reg.S4, 0, Reg.T2);
        Print(p, "scratchpad=").PrintIntEe(Reg.S4);

        // main lowers its own priority to 5: worker 2 (priority 3) preempts, exits, and main continues.
        p.Addiu(Reg.A0, Reg.Zero, 0).Addiu(Reg.A1, Reg.Zero, 5);
        Kernel(p, ChangeThreadPriority);
        Print(p, "main: workers finished\n");

        // A thread of priority 2 sleeps; main wakes it twice. Each wake-up preempts main (priority 5).
        ThreadParam(p, Thread3Param, Entry("sleeper"), Stack3, 0x400, 2).Li32(Reg.A0, Thread3Param);
        Kernel(p, CreateThread).Move(Reg.S4, Reg.V0);                                     // reuses the id of the deleted worker 1
        Print(p, "t3=").PrintIntEe(Reg.S4);
        p.Move(Reg.A0, Reg.S4).Addiu(Reg.A1, Reg.Zero, 0);
        Kernel(p, StartThread);                                                           // preempts main at once: sleeper runs and sleeps
        Print(p, "main: waking sleeper\n");
        p.Move(Reg.A0, Reg.S4);
        Kernel(p, WakeupThread);
        Print(p, "main: waking sleeper again\n");
        p.Move(Reg.A0, Reg.S4);
        Kernel(p, WakeupThread);
        Print(p, "main: done\n");
        p.Move(Reg.A0, Reg.Zero);
        Kernel(p, KExit);

        // ---- worker: a0 = the character to print ----
        p.Label("worker").Move(Reg.S0, Reg.A0).Addiu(Reg.S1, Reg.Zero, 2);
        p.Label("worker_loop").Move(Reg.A0, Reg.S0).Jal("sio_putc").Nop();
        p.Addiu(Reg.A0, Reg.Zero, 3);
        Kernel(p, RotateThreadReadyQueue);
        p.Addiu(Reg.S1, Reg.S1, -1).Bne(Reg.S1, Reg.Zero, "worker_loop").Nop();
        p.Li32(Reg.T0, SemaIdVariable).Lw(Reg.A0, 0, Reg.T0);
        Kernel(p, SignalSema);
        Kernel(p, ExitThread);

        // ---- sleeper ----
        p.Label("sleeper");
        Print(p, "sleeper: sleeping\n");
        Kernel(p, SleepThread);
        Print(p, "sleeper: woke\n");
        Kernel(p, SleepThread);
        Print(p, "sleeper: woke again\n");
        Kernel(p, ExitThread);

        // ---- serial port output (SIO_TXFIFO at its uncached mirror 0xB000F180) ----
        p.Label("sio_putc").Li32(Reg.T0, 0xB000F180).Sb(Reg.A0, 0, Reg.T0).Jr(Reg.Ra).Nop();
        p.Label("sio_puts").Li32(Reg.T0, 0xB000F180);
        p.Label("sio_puts_loop").Lbu(Reg.T1, 0, Reg.A0).Beq(Reg.T1, Reg.Zero, "sio_puts_done").Nop()
            .Sb(Reg.T1, 0, Reg.T0).Addiu(Reg.A0, Reg.A0, 1).J("sio_puts_loop").Nop();
        p.Label("sio_puts_done").Jr(Reg.Ra).Nop();
        return p;
    }

    /// <summary>
    /// The output derived by hand from the scheduling rules. Ids: main = 1, the semaphore = 1, worker 1 = 2, worker 2 = 3, and the
    /// sleeper reuses id 2 after worker 1 was deleted.
    /// </summary>
    public static string ExpectedOutput() =>
        "ee kernel selftest\n" +
        "sema=1\nt1=2\nt2=3\n" +
        "main: waiting\n" +
        "ABAB" +
        "\nmain: signaled\n" +
        "main: second signal\n" +
        "deleted=2\npoll=-1\n" +
        "kseg0=12345678\nkseg1=12345678\nscratchpad=777\n" +
        "main: workers finished\n" +
        "t3=2\n" +
        "sleeper: sleeping\n" +
        "main: waking sleeper\n" +
        "sleeper: woke\n" +
        "main: waking sleeper again\n" +
        "sleeper: woke again\n" +
        "main: done\n";
}
