using RecompRadar.Recompilation;
using RecompRadar.Recompilation.Execution;
using RecompRadar.Semantics;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

public class Ps2MemoryTests
{
    private static Ps2Memory Create(out List<byte> serial, bool little = true)
    {
        var sink = new List<byte>();
        serial = sink;
        return new Ps2Memory(little, message => new GuestFaultException(message), sink.Add);
    }

    private static GuestFaultException Fault(Action action) => Assert.Throws<GuestFaultException>(action);

    [Fact]
    public void Ram_is_visible_through_the_kseg0_and_kseg1_mirrors()
    {
        var m = Create(out _);

        m.Store(0x00123450, 4, 0xDEADBEEF);

        Assert.Equal(0xDEADBEEFu, m.Load(0x00123450, 4, false));
        Assert.Equal(0xDEADBEEFu, m.Load(0x80123450, 4, false));
        Assert.Equal(0xDEADBEEFu, m.Load(0xA0123450, 4, false));
        m.Store(0xA0123454, 4, 7);
        Assert.Equal(7u, m.Load(0x00123454, 4, false));
        Assert.Equal(0x00123450u, Ps2Memory.ToPhysical(0xA0123450));
    }

    [Fact]
    public void Ram_is_exactly_32_mebibytes()
    {
        var m = Create(out _);

        m.Store(0x01FFFFFC, 4, 1);
        Assert.Equal(1u, m.Load(0x01FFFFFC, 4, false));
        Assert.Contains("unmapped address 0x02000000", Fault(() => m.Load(0x02000000, 4, false)).Message, StringComparison.Ordinal);
        Assert.Contains("unmapped address 0x0FFFFFFC", Fault(() => m.Store(0x0FFFFFFC, 4, 1)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scratchpad_is_16_kibibytes_and_not_mirrored()
    {
        var m = Create(out _);

        m.Store(0x70003FFC, 4, 0x11223344);

        Assert.Equal(0x11223344u, m.Load(0x70003FFC, 4, false));
        Assert.Contains("unmapped", Fault(() => m.Load(0x70004000, 4, false)).Message, StringComparison.Ordinal);
        Assert.Contains("unmapped", Fault(() => m.Load(0xF0000000, 4, false)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_byte_stored_to_the_serial_fifo_is_sent_and_the_status_register_reads_ready()
    {
        var m = Create(out var serial);

        m.Store(Ps2Memory.SioTxFifo, 1, 'H');
        m.Store(0xB000F180, 1, 'i');                                  // the uncached mirror of the same register

        Assert.Equal("Hi", System.Text.Encoding.ASCII.GetString(serial.ToArray()));
        Assert.Equal(0u, m.Load(Ps2Memory.SioIsr, 4, false));
    }

    [Fact]
    public void Other_io_registers_fault_instead_of_returning_invented_values()
    {
        var m = Create(out var serial);

        Assert.Contains("I/O register 0x10000000", Fault(() => m.Load(0x10000000, 4, false)).Message, StringComparison.Ordinal);
        Assert.Contains("I/O register 0x1000F000", Fault(() => m.Store(0x1000F000, 4, 1)).Message, StringComparison.Ordinal);
        Assert.Empty(serial);
        Assert.Contains("unmapped", Fault(() => m.Load(0x12000000, 4, false)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Misaligned_accesses_fault_in_both_directions()
    {
        var m = Create(out _);

        Assert.Equal("Unaligned 4-byte read at 0x00100001.", Fault(() => m.Load(0x00100001, 4, false)).Message);
        Assert.Equal("Unaligned 2-byte write at 0x00100003.", Fault(() => m.Store(0x00100003, 2, 1)).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Byte_order_and_sign_extension_follow_the_guest_endianness(bool little)
    {
        var m = Create(out _, little);

        m.Store(0x00200000, 4, 0x80FF1234);

        // Bytes in memory: little endian 34 12 FF 80, big endian 80 FF 12 34.
        Assert.Equal(little ? 0x34u : 0x80u, m.Load(0x00200000, 1, false));
        Assert.Equal(little ? 0x1234u : 0x80FFu, m.Load(0x00200000, 2, false));
        Assert.Equal(0xFFFFFF80u, m.Load(little ? 0x00200003u : 0x00200000u, 1, true));
        Assert.Equal(0xFFFF80FFu, m.Load(little ? 0x00200002u : 0x00200000u, 2, true));
        Assert.Equal(little ? 0x80FFu : 0x1234u, m.Load(0x00200002, 2, false));
    }

    [Fact]
    public void Loaded_executable_segments_are_write_protected_but_data_segments_are_not()
    {
        var m = Create(out _);
        m.LoadSegment(0x00100000, new byte[16], executable: true);
        m.LoadSegment(0x00200000, new byte[16], executable: false);

        Assert.Contains("self-modifying code is not supported", Fault(() => m.Store(0x00100008, 4, 1)).Message, StringComparison.Ordinal);
        Assert.Contains("self-modifying code is not supported", Fault(() => m.Store(0x80100000, 1, 1)).Message, StringComparison.Ordinal);
        m.Store(0x00200008, 4, 1);
        m.Store(0x00100010, 4, 1);                                    // the first word behind the segment is ordinary RAM
        Assert.Equal(1u, m.Load(0x00200008, 4, false));
    }

    [Fact]
    public void A_segment_outside_ram_is_refused()
    {
        var m = Create(out _);

        Assert.Throws<InvalidDataException>(() => m.LoadSegment(0x01FFFFF0, new byte[32], false));
        Assert.Throws<InvalidDataException>(() => m.LoadSegment(0x40000000, new byte[4], false));
    }

    [Fact]
    public void Non_faulting_reads_never_touch_io_and_report_misses()
    {
        var m = Create(out _);
        m.Store(0x00300000, 4, 0xCAFEBABE);

        Assert.True(m.TryLoad32(0x80300000, out var value));
        Assert.Equal(0xCAFEBABEu, value);
        Assert.False(m.TryLoad32(0x00300002, out _));
        Assert.False(m.TryLoad32(Ps2Memory.SioIsr, out _));
        Assert.Equal(0xBE, m.TryReadByte(0x00300000));
        Assert.Equal(-1, m.TryReadByte(0x10000000));
    }
}

public class Ps2KernelTests
{
    private const uint Pointer = 0x1000;

    private sealed class Rig
    {
        public readonly Dictionary<uint, uint> Memory = [];
        public readonly ulong[] R = new ulong[128];
        public readonly Ps2Kernel Kernel;
        public uint Pc = 0x400000;

        public Rig()
        {
            Kernel = new Ps2Kernel((uint address, out uint value) => Memory.TryGetValue(address, out value));
        }

        public Ps2Result Call(int number, uint a0 = 0, uint a1 = 0)
        {
            R[3] = unchecked((uint)number);
            R[4] = a0;
            R[5] = a1;
            var result = Kernel.Handle(R, Pc + 4);
            if (result.Action == Ps2Action.Continue)
            {
                Pc = result.NextPc;
            }

            return result;
        }

        public int Return => unchecked((int)R[2]);

        public uint Thread(uint entry, int priority, uint stack = 0x80000, uint size = 0x1000, uint gp = 0x5555)
        {
            var at = (uint)(Pointer + (Memory.Count * 0x40));
            Memory[at + 4] = entry;
            Memory[at + 8] = stack;
            Memory[at + 0xC] = size;
            Memory[at + 0x10] = gp;
            Memory[at + 0x14] = (uint)priority;
            Call(Ps2Kernel.CreateThread, at);
            return (uint)R[2];
        }

        public uint Sema(uint max, uint init)
        {
            var at = (uint)(Pointer + 0x8000 + (Memory.Count * 0x40));
            Memory[at + 4] = max;
            Memory[at + 8] = init;
            Call(Ps2Kernel.CreateSema, at);
            return (uint)R[2];
        }
    }

    [Fact]
    public void Call_numbers_are_the_values_from_ps2sdk_syscallnr_h()
    {
        Assert.Equal(1, Ps2Kernel.ResetEE);
        Assert.Equal(4, Ps2Kernel.KExit);
        Assert.Equal(0x20, Ps2Kernel.CreateThread);
        Assert.Equal(0x21, Ps2Kernel.DeleteThread);
        Assert.Equal(0x22, Ps2Kernel.StartThread);
        Assert.Equal(0x23, Ps2Kernel.ExitThread);
        Assert.Equal(0x24, Ps2Kernel.ExitDeleteThread);
        Assert.Equal(0x25, Ps2Kernel.TerminateThread);
        Assert.Equal(0x29, Ps2Kernel.ChangeThreadPriority);
        Assert.Equal(0x2B, Ps2Kernel.RotateThreadReadyQueue);
        Assert.Equal(0x2F, Ps2Kernel.GetThreadId);
        Assert.Equal(0x32, Ps2Kernel.SleepThread);
        Assert.Equal(0x33, Ps2Kernel.WakeupThread);
        Assert.Equal(0x3C, Ps2Kernel.SetupThread);
        Assert.Equal(0x3D, Ps2Kernel.SetupHeap);
        Assert.Equal(0x3E, Ps2Kernel.EndOfHeap);
        Assert.Equal(0x40, Ps2Kernel.CreateSema);
        Assert.Equal(0x41, Ps2Kernel.DeleteSema);
        Assert.Equal(0x42, Ps2Kernel.SignalSema);
        Assert.Equal(0x44, Ps2Kernel.WaitSema);
        Assert.Equal(0x45, Ps2Kernel.PollSema);
        Assert.Equal(0x64, Ps2Kernel.FlushCache);
        Assert.Equal(0x71, Ps2Kernel.GsPutIMR);
        Assert.Equal(-0x43, Ps2Kernel.ISignalSema);
    }

    [Fact]
    public void Eighteen_calls_are_implemented_and_all_known_calls_have_names()
    {
        var implemented = Ps2Kernel.ImplementedCalls();

        Assert.Equal(18, implemented.Count);
        Assert.All(implemented, n => Assert.NotNull(Ps2Kernel.NameOf(n)));
        Assert.Equal("CreateThread", Ps2Kernel.NameOf(0x20));
        Assert.Equal("SetupThread", Ps2Kernel.NameOf(0x3C));
        Assert.False(Ps2Kernel.IsImplemented(0x3C));
        Assert.False(Ps2Kernel.IsImplemented(0x99));
        Assert.Null(Ps2Kernel.NameOf(0x99));
        Assert.True(Ps2Kernel.NeverReturns(Ps2Kernel.KExit));
        Assert.True(Ps2Kernel.NeverReturns(Ps2Kernel.ExitThread));
        Assert.False(Ps2Kernel.NeverReturns(Ps2Kernel.SleepThread));
    }

    [Fact]
    public void The_main_thread_has_id_1_and_kexit_ends_the_program_with_the_given_code()
    {
        var rig = new Rig();

        rig.Call(Ps2Kernel.GetThreadId);
        Assert.Equal(1, rig.Return);

        var exit = rig.Call(Ps2Kernel.KExit, 42);
        Assert.Equal(Ps2Action.Exit, exit.Action);
        Assert.Equal(42, exit.ExitCode);
    }

    [Fact]
    public void Create_thread_reads_the_parameter_structure_and_validates_it()
    {
        var rig = new Rig();

        Assert.Equal(2u, rig.Thread(entry: 0x100000, priority: 5));
        Assert.Equal(3u, rig.Thread(entry: 0x100000, priority: 127));
        Assert.Equal(0xFFFFFFFFu, rig.Thread(entry: 0x100000, priority: 128));    // out of range
        Assert.Equal(0xFFFFFFFFu, rig.Thread(entry: 0, priority: 5));             // no entry function

        var fault = rig.Call(Ps2Kernel.CreateThread, 0x7777_0000);                 // not readable memory
        Assert.Equal(Ps2Action.Fault, fault.Action);
        Assert.Contains("CreateThread", fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_started_thread_begins_with_the_documented_register_state()
    {
        var rig = new Rig();
        var id = rig.Thread(entry: 0x100800, priority: 0, stack: 0x90000, size: 0x2000, gp: 0xABCD);
        rig.R[16] = 0x1234;                                                        // a register of the main thread

        var result = rig.Call(Ps2Kernel.StartThread, id, 0x600D);                  // priority 0 beats main's priority 1

        Assert.Equal(Ps2Action.Continue, result.Action);
        Assert.Equal(0x100800u, result.NextPc);
        Assert.Equal(0x600Du, rig.R[4]);
        Assert.Equal(0xABCDu, rig.R[28]);
        Assert.Equal(0x92000u, rig.R[29]);
        Assert.Equal(unchecked((ulong)(long)(int)Ps2Kernel.ThreadExitAddress), rig.R[31]);   // kernel addresses are held sign-extended
        Assert.Equal(0u, rig.R[16]);
        rig.Call(Ps2Kernel.GetThreadId);
        Assert.Equal((int)id, rig.Return);
    }

    [Fact]
    public void A_lower_priority_thread_waits_until_the_main_thread_blocks()
    {
        var rig = new Rig();
        var id = rig.Thread(entry: 0x100800, priority: 9);
        rig.R[16] = 0x1234;

        var started = rig.Call(Ps2Kernel.StartThread, id);
        Assert.Equal(rig.Pc, started.NextPc);                                      // main continues
        Assert.Equal(id, rig.R[2]);
        Assert.Equal(0x1234u, rig.R[16]);

        var slept = rig.Call(Ps2Kernel.SleepThread);                               // main sleeps, the worker runs
        Assert.Equal(0x100800u, slept.NextPc);
        rig.Call(Ps2Kernel.WakeupThread, 1);                                       // worker wakes main: main (priority 1) preempts
        Assert.Equal(0x1234u, rig.R[16]);
        Assert.Equal(0, rig.Return);                                               // SleepThread returned 0
    }

    [Fact]
    public void A_wakeup_that_comes_early_is_remembered()
    {
        var rig = new Rig();

        rig.Call(Ps2Kernel.WakeupThread, 1);                                       // the main thread is running, not sleeping
        Assert.Equal(1, rig.Return);
        var sleep = rig.Call(Ps2Kernel.SleepThread);                               // consumes the queued wake-up and goes on
        Assert.Equal(Ps2Action.Continue, sleep.Action);
        Assert.Equal(rig.Pc, sleep.NextPc);
        var second = rig.Call(Ps2Kernel.SleepThread);                              // nothing queued: deadlock, the only thread sleeps
        Assert.Equal(Ps2Action.Fault, second.Action);
        Assert.Contains("deadlock", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Waking_a_dormant_or_unknown_thread_fails()
    {
        var rig = new Rig();
        var id = rig.Thread(entry: 0x100000, priority: 5);

        rig.Call(Ps2Kernel.WakeupThread, id);
        Assert.Equal(-1, rig.Return);
        rig.Call(Ps2Kernel.WakeupThread, 99);
        Assert.Equal(-1, rig.Return);
        rig.Call(Ps2Kernel.WakeupThread, 0);
        Assert.Equal(-1, rig.Return);
    }

    [Fact]
    public void Semaphores_count_poll_and_refuse_to_exceed_their_maximum()
    {
        var rig = new Rig();
        var s = rig.Sema(max: 2, init: 1);
        Assert.Equal(1u, s);

        rig.Call(Ps2Kernel.PollSema, s);
        Assert.Equal(1, rig.Return);                                               // took the only token
        rig.Call(Ps2Kernel.PollSema, s);
        Assert.Equal(-1, rig.Return);                                              // empty
        rig.Call(Ps2Kernel.SignalSema, s);
        rig.Call(Ps2Kernel.SignalSema, s);
        Assert.Equal(1, rig.Return);
        rig.Call(Ps2Kernel.SignalSema, s);
        Assert.Equal(-1, rig.Return);                                              // already at the maximum of 2
        Assert.Equal(ulong.MaxValue, rig.R[2]);                                    // failure is -1 sign-extended to the 64-bit register
        rig.Call(Ps2Kernel.WaitSema, s);
        Assert.Equal(1, rig.Return);                                               // does not block: a token is there
    }

    [Fact]
    public void Invalid_semaphore_parameters_and_ids_are_rejected()
    {
        var rig = new Rig();

        Assert.Equal(0xFFFFFFFFu, rig.Sema(max: 0, init: 0));
        Assert.Equal(0xFFFFFFFFu, rig.Sema(max: 1, init: 2));
        rig.Call(Ps2Kernel.SignalSema, 5);
        Assert.Equal(-1, rig.Return);
        rig.Call(Ps2Kernel.WaitSema, 0);
        Assert.Equal(-1, rig.Return);
        Assert.Equal(Ps2Action.Fault, rig.Call(Ps2Kernel.CreateSema, 0x7777_0000).Action);
    }

    [Fact]
    public void Waiters_are_woken_in_order_and_deleting_a_semaphore_releases_them_with_an_error()
    {
        var rig = new Rig();
        var s = rig.Sema(max: 1, init: 0);
        var a = rig.Thread(0x100100, priority: 2);
        var b = rig.Thread(0x100200, priority: 2);
        rig.Call(Ps2Kernel.StartThread, a);
        rig.Call(Ps2Kernel.StartThread, b);

        var mainResume = rig.Pc + 4;                                               // where main continues after its WaitSema call
        var wait = rig.Call(Ps2Kernel.WaitSema, s);                                // main blocks, thread a runs
        Assert.Equal(0x100100u, wait.NextPc);
        var waitA = rig.Call(Ps2Kernel.WaitSema, s);                               // thread a blocks, thread b runs
        Assert.Equal(0x100200u, waitA.NextPc);
        var signal = rig.Call(Ps2Kernel.SignalSema, s);                            // wakes main first (FIFO); main has the best priority
        Assert.Equal(mainResume, signal.NextPc);
        Assert.Equal((int)s, rig.Return);

        rig.Call(Ps2Kernel.DeleteSema, s);                                         // thread a is still waiting: it is released with -1
        Assert.Equal((int)s, rig.Return);
        rig.Call(Ps2Kernel.WaitSema, s);
        Assert.Equal(-1, rig.Return);                                              // the semaphore no longer exists
    }

    [Fact]
    public void A_preempted_thread_resumes_before_its_equal_priority_peers()
    {
        var rig = new Rig();
        var s = rig.Sema(max: 1, init: 0);
        var a = rig.Thread(0x100100, priority: 3);
        var b = rig.Thread(0x100200, priority: 3);
        rig.Call(Ps2Kernel.StartThread, a);
        rig.Call(Ps2Kernel.StartThread, b);
        rig.Call(Ps2Kernel.WaitSema, s);                                           // main blocks, a runs
        var aResume = rig.Pc + 4;
        rig.Call(Ps2Kernel.SignalSema, s);                                         // wakes main, which preempts a

        var again = rig.Call(Ps2Kernel.WaitSema, s);                               // main blocks again

        Assert.Equal(aResume, again.NextPc);                                       // a continues; b (waiting longer in the queue) is still ready
        Assert.NotEqual(0x100200u, again.NextPc);
    }

    [Fact]
    public void Rotate_lets_equal_priority_threads_take_turns()
    {
        var rig = new Rig();
        var a = rig.Thread(0x100100, priority: 3);
        var b = rig.Thread(0x100200, priority: 3);
        rig.Call(Ps2Kernel.ChangeThreadPriority, 0, 3);                            // main joins priority 3
        rig.Call(Ps2Kernel.StartThread, a);
        rig.Call(Ps2Kernel.StartThread, b);

        var mainResume = rig.Pc + 4;
        var first = rig.Call(Ps2Kernel.RotateThreadReadyQueue, 3);                 // main yields to a
        var second = rig.Call(Ps2Kernel.RotateThreadReadyQueue, 3);                // a yields to b
        var third = rig.Call(Ps2Kernel.RotateThreadReadyQueue, 3);                 // b yields to main
        var fourth = rig.Call(Ps2Kernel.RotateThreadReadyQueue, 3);                // main yields to a (resumes after its rotate)

        Assert.Equal(0x100100u, first.NextPc);
        Assert.Equal(0x200u + 0x100000u, second.NextPc);
        Assert.Equal(mainResume, third.NextPc);
        Assert.Equal(0x100104u, fourth.NextPc);                                    // thread a resumes right after its own rotate call
        Assert.Equal(3, rig.Return);
    }

    [Fact]
    public void Changing_a_priority_validates_the_range_and_the_target()
    {
        var rig = new Rig();
        var id = rig.Thread(0x100100, priority: 5);

        rig.Call(Ps2Kernel.ChangeThreadPriority, 0, 128);
        Assert.Equal(-1, rig.Return);
        rig.Call(Ps2Kernel.ChangeThreadPriority, id, 3);                           // dormant threads cannot be changed
        Assert.Equal(-1, rig.Return);
        rig.Call(Ps2Kernel.ChangeThreadPriority, 0, 10);
        Assert.Equal(0, rig.Return);
    }

    [Fact]
    public void Deleting_and_terminating_threads_follows_their_state()
    {
        var rig = new Rig();
        var id = rig.Thread(0x100100, priority: 5);
        rig.Call(Ps2Kernel.StartThread, id);

        rig.Call(Ps2Kernel.DeleteThread, id);
        Assert.Equal(-1, rig.Return);                                              // not dormant
        rig.Call(Ps2Kernel.TerminateThread, id);
        Assert.Equal((int)id, rig.Return);
        rig.Call(Ps2Kernel.DeleteThread, id);
        Assert.Equal((int)id, rig.Return);
        rig.Call(Ps2Kernel.DeleteThread, id);
        Assert.Equal(-1, rig.Return);                                              // gone
        rig.Call(Ps2Kernel.TerminateThread, 1);
        Assert.Equal(-1, rig.Return);                                              // a thread cannot terminate itself this way
    }

    [Fact]
    public void A_thread_function_that_returns_ends_the_thread_and_the_last_exit_ends_the_program()
    {
        var rig = new Rig();
        var id = rig.Thread(0x100100, priority: 0);
        var mainResume = rig.Pc + 4;
        rig.Call(Ps2Kernel.StartThread, id);                                       // the new thread preempts main

        var back = rig.Kernel.ThreadReturned(rig.R);                               // it returns into the kernel
        Assert.Equal(Ps2Action.Continue, back.Action);
        Assert.Equal(mainResume, back.NextPc);                                     // main resumes

        var end = rig.Call(Ps2Kernel.ExitThread);                                  // main was the last thread
        Assert.Equal(Ps2Action.Exit, end.Action);
        Assert.Equal(0, end.ExitCode);
    }

    [Fact]
    public void Exit_and_delete_frees_the_thread_id_for_reuse()
    {
        var rig = new Rig();
        var id = rig.Thread(0x100100, priority: 0);
        rig.Call(Ps2Kernel.StartThread, id);

        rig.Call(Ps2Kernel.ExitDeleteThread);                                      // the new thread deletes itself, main resumes

        Assert.Equal(id, rig.Thread(0x100200, priority: 4));                       // the id is reused
    }

    [Fact]
    public void Unimplemented_calls_name_themselves_in_the_fault()
    {
        var rig = new Rig();

        var setup = rig.Call(Ps2Kernel.SetupThread);
        Assert.Equal(Ps2Action.Fault, setup.Action);
        Assert.Contains("SetupThread", setup.Message, StringComparison.Ordinal);
        Assert.Contains("not implemented", setup.Message, StringComparison.Ordinal);

        var unknown = rig.Call(0x7F);
        Assert.Contains("Unknown EE kernel call number 127", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("iSignalSema", rig.Call(Ps2Kernel.ISignalSema).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Flush_cache_is_accepted_and_changes_nothing()
    {
        var rig = new Rig();
        rig.R[16] = 77;

        var result = rig.Call(Ps2Kernel.FlushCache, 2);

        Assert.Equal(Ps2Action.Continue, result.Action);
        Assert.Equal(0, rig.Return);
        Assert.Equal(77u, rig.R[16]);
    }
}

public class Ps2KernelSelfTestTests
{
    private static ExecutionResult Run() => RefRun.RunElf(Ps2KernelSelfTest.BuildElf(), 2_000_000, RuntimeProfile.Ps2Ee);

    [Fact]
    public void The_reference_interpreter_produces_the_output_derived_by_hand()
    {
        var result = Run();

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Ps2KernelSelfTest.ExpectedOutput(), result.Output);
    }

    [Fact]
    public void The_same_program_is_refused_by_the_flat_profile_because_the_serial_port_does_not_exist_there()
    {
        var result = RefRun.RunElf(Ps2KernelSelfTest.BuildElf(), 2_000_000);

        Assert.Equal(ExecutionStatus.Faulted, result.Status);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public void The_program_is_valid_homebrew_shaped_elf_with_function_symbols()
    {
        var elf = T.ParseOk(Ps2KernelSelfTest.BuildElf());

        Assert.Equal(8, elf.Header.Machine);
        Assert.Equal(Ps2KernelSelfTest.CodeAddress, elf.Header.Entry);
        Assert.Equal(new[] { "main", "sio_putc", "sio_puts", "sleeper", "worker" }, elf.Symbols.Where(s => s.IsFunction).Select(s => s.Name).OrderBy(n => n).ToArray());
    }
}
