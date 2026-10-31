// This file is compiled into RecompRadar.Recompilation AND copied verbatim into every program the recompiler builds for the
// PlayStation 2 profile, so the reference interpreter and the built program share one definition of the EE memory map and the
// kernel services. Keep it free of dependencies on the rest of RecompRadar.
//
// What this models, from public sources (ps2sdk: ee/kernel/include/syscallnr.h, kernel.h, sio.h):
//  * the EE address space: 32 MiB RAM (also visible through the KSEG0/KSEG1 mirrors), the 16 KiB scratchpad and the serial port;
//  * the kernel call convention: call number in $v1, arguments in $a0.., result in $v0;
//  * a subset of the kernel: threads, semaphores, sleep/wakeup, priority changes, exit.
// What it does not model: interrupts, timers, time slicing, DMA, graphics, IOP, files, most I/O registers, TLB, caches.
// The numbers and struct layouts come from the ps2sdk headers; the *return values* and scheduling details are not documented
// there and follow common convention (ids on success, -1 on failure; lower priority number runs first). They have not been
// compared with real hardware.


namespace RecompRadar.Semantics;

/// <summary>The EE address space. All faults are raised through the factory so the host can use its own exception type.</summary>
public sealed class Ps2Memory
{
    public const uint RamSize = 0x02000000;
    public const uint ScratchpadBase = 0x70000000;
    public const uint ScratchpadSize = 0x4000;
    public const uint IoBase = 0x10000000;
    public const uint IoEnd = 0x10010000;

    /// <summary>SIO_ISR in ps2sdk's sio.h. Reading 0 means "ready to transmit".</summary>
    public const uint SioIsr = 0x1000F130;

    /// <summary>SIO_TXFIFO in ps2sdk's sio.h: a byte written here is sent to the serial port (the debug console).</summary>
    public const uint SioTxFifo = 0x1000F180;

    /// <summary>Initial stack pointer of the main thread (top of RAM, 16-byte aligned).</summary>
    public const uint InitialStackPointer = 0x01FFFFF0;

    private enum Area
    {
        None,
        Ram,
        Scratchpad,
        Io,
    }

    private readonly byte[] _ram = new byte[RamSize];
    private readonly byte[] _scratchpad = new byte[ScratchpadSize];
    private readonly List<(uint Start, uint End)> _code = [];
    private readonly bool _little;
    private readonly Func<string, Exception> _fault;
    private readonly Action<byte> _serial;

    public Ps2Memory(bool littleEndian, Func<string, Exception> faultFactory, Action<byte> serialOut)
    {
        _little = littleEndian;
        _fault = faultFactory;
        _serial = serialOut;
    }

    /// <summary>KSEG0 and KSEG1 (0x80000000-0xBFFFFFFF) mirror the physical addresses below 512 MiB.</summary>
    public static uint ToPhysical(uint address) => address >= 0x80000000u && address < 0xC0000000u ? address & 0x1FFFFFFFu : address;

    /// <summary>Copies a program segment into RAM. Executable segments become write-protected (self-modifying code is not supported).</summary>
    public void LoadSegment(uint address, ReadOnlySpan<byte> data, bool executable)
    {
        var physical = ToPhysical(address);
        if ((ulong)physical + (ulong)data.Length > RamSize)
        {
            throw new InvalidDataException($"The segment at 0x{address:X8} ({data.Length} bytes) does not fit into the 32 MiB of EE RAM.");
        }

        data.CopyTo(_ram.AsSpan((int)physical));
        if (executable && data.Length > 0)
        {
            _code.Add((physical, physical + (uint)data.Length));
        }
    }

    private Area Locate(uint address, int size, out uint offset)
    {
        var a = ToPhysical(address);
        offset = a;
        if (a < RamSize)
        {
            return (uint)size <= RamSize - a ? Area.Ram : Area.None;
        }

        if (a >= ScratchpadBase && a < ScratchpadBase + ScratchpadSize)
        {
            offset = a - ScratchpadBase;
            return (uint)size <= ScratchpadSize - offset ? Area.Scratchpad : Area.None;
        }

        return a >= IoBase && a < IoEnd ? Area.Io : Area.None;
    }

    public uint Load(uint address, int size, bool signExtend)
    {
        if (address % (uint)size != 0)
        {
            throw _fault($"Unaligned {size}-byte read at 0x{address:X8}.");
        }

        switch (Locate(address, size, out var offset))
        {
            case Area.Ram:
                return Read(_ram, (int)offset, size, signExtend);
            case Area.Scratchpad:
                return Read(_scratchpad, (int)offset, size, signExtend);
            case Area.Io:
                if (ToPhysical(address) == SioIsr && size == 4)
                {
                    return 0;
                }

                throw _fault($"Read of the I/O register 0x{ToPhysical(address):X8}: only the serial port is modelled.");
            default:
                throw _fault($"Read of {size} byte(s) at unmapped address 0x{address:X8}.");
        }
    }

    public void Store(uint address, int size, uint value)
    {
        if (address % (uint)size != 0)
        {
            throw _fault($"Unaligned {size}-byte write at 0x{address:X8}.");
        }

        switch (Locate(address, size, out var offset))
        {
            case Area.Ram:
                foreach (var (start, end) in _code)
                {
                    if (offset < end && offset + (uint)size > start)
                    {
                        throw _fault($"Write to executable memory at 0x{address:X8}: self-modifying code is not supported.");
                    }
                }

                Write(_ram, (int)offset, size, value);
                break;
            case Area.Scratchpad:
                Write(_scratchpad, (int)offset, size, value);
                break;
            case Area.Io:
                if (ToPhysical(address) == SioTxFifo)
                {
                    _serial((byte)value);
                    break;
                }

                throw _fault($"Write to the I/O register 0x{ToPhysical(address):X8}: only the serial port is modelled.");
            default:
                throw _fault($"Write of {size} byte(s) at unmapped address 0x{address:X8}.");
        }
    }

    public ulong Load64(uint address)
    {
        if (address % 8 != 0)
        {
            throw _fault($"Unaligned 8-byte read at 0x{address:X8}.");
        }

        switch (Locate(address, 8, out var offset))
        {
            case Area.Ram:
                return Read64(_ram, (int)offset);
            case Area.Scratchpad:
                return Read64(_scratchpad, (int)offset);
            case Area.Io:
                throw _fault($"Read of the I/O register 0x{ToPhysical(address):X8}: only the serial port is modelled.");
            default:
                throw _fault($"Read of 8 byte(s) at unmapped address 0x{address:X8}.");
        }
    }

    public void Store64(uint address, ulong value)
    {
        if (address % 8 != 0)
        {
            throw _fault($"Unaligned 8-byte write at 0x{address:X8}.");
        }

        switch (Locate(address, 8, out var offset))
        {
            case Area.Ram:
                foreach (var (start, end) in _code)
                {
                    if (offset < end && offset + 8 > start)
                    {
                        throw _fault($"Write to executable memory at 0x{address:X8}: self-modifying code is not supported.");
                    }
                }

                Write64(_ram, (int)offset, value);
                break;
            case Area.Scratchpad:
                Write64(_scratchpad, (int)offset, value);
                break;
            case Area.Io:
                throw _fault($"Write to the I/O register 0x{ToPhysical(address):X8}: only the serial port is modelled.");
            default:
                throw _fault($"Write of 8 byte(s) at unmapped address 0x{address:X8}.");
        }
    }

    private ulong Read64(byte[] data, int offset)
    {
        ulong value = 0;
        for (var i = 0; i < 8; i++)
        {
            value = (value << 8) | data[offset + (_little ? 7 - i : i)];
        }

        return value;
    }

    private void Write64(byte[] data, int offset, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            data[offset + (_little ? i : 7 - i)] = (byte)(value >> (8 * i));
        }
    }

    /// <summary>Reads one byte of RAM or scratchpad for instruction fetch and strings; -1 when the address is not backed by memory.</summary>
    public int TryReadByte(uint address)
    {
        switch (Locate(address, 1, out var offset))
        {
            case Area.Ram:
                return _ram[offset];
            case Area.Scratchpad:
                return _scratchpad[offset];
            default:
                return -1;
        }
    }

    /// <summary>Reads an aligned word of RAM or scratchpad without faulting.</summary>
    public bool TryLoad32(uint address, out uint value)
    {
        value = 0;
        if (address % 4 != 0)
        {
            return false;
        }

        switch (Locate(address, 4, out var offset))
        {
            case Area.Ram:
                value = Read(_ram, (int)offset, 4, false);
                return true;
            case Area.Scratchpad:
                value = Read(_scratchpad, (int)offset, 4, false);
                return true;
            default:
                return false;
        }
    }

    private uint Read(byte[] data, int offset, int size, bool signExtend)
    {
        uint value = 0;
        for (var i = 0; i < size; i++)
        {
            var b = data[offset + (_little ? size - 1 - i : i)];
            value = (value << 8) | b;
        }

        if (signExtend && size < 4)
        {
            var shift = 32 - (8 * size);
            return (uint)((int)(value << shift) >> shift);
        }

        return value;
    }

    private void Write(byte[] data, int offset, int size, uint value)
    {
        for (var i = 0; i < size; i++)
        {
            data[offset + (_little ? i : size - 1 - i)] = (byte)(value >> (8 * i));
        }
    }
}

public enum Ps2Action
{
    Continue,
    Exit,
    Fault,
}

/// <param name="NextPc">Where the (possibly different) running thread continues.</param>
public readonly record struct Ps2Result(Ps2Action Action, uint NextPc = 0, int ExitCode = 0, string? Message = null);

public delegate bool TryRead32(uint address, out uint value);

/// <summary>
/// A cooperative-at-kernel-calls, priority-based thread scheduler with semaphores. A lower priority number runs first; equal
/// priorities run in order of becoming ready; a thread is preempted only by a kernel call that makes a higher-priority thread
/// ready. There is no time slicing and there are no interrupts.
/// </summary>
public sealed class Ps2Kernel
{
    /// <summary>A thread function that returns jumps here; the kernel treats it as ExitThread.</summary>
    public const uint ThreadExitAddress = 0x80000FF0;

    public const int MaxThreads = 256;
    public const int MaxSemaphores = 256;
    public const int PriorityCount = 128;
    public const int MainThreadId = 1;
    public const int MainThreadPriority = 1;

    // Call numbers from ps2sdk ee/kernel/include/syscallnr.h.
    public const int ResetEE = 1;
    public const int KExit = 4;
    public const int CreateThread = 0x20;
    public const int DeleteThread = 0x21;
    public const int StartThread = 0x22;
    public const int ExitThread = 0x23;
    public const int ExitDeleteThread = 0x24;
    public const int TerminateThread = 0x25;
    public const int ChangeThreadPriority = 0x29;
    public const int RotateThreadReadyQueue = 0x2B;
    public const int GetThreadId = 0x2F;
    public const int SleepThread = 0x32;
    public const int WakeupThread = 0x33;
    public const int SetupThread = 0x3C;
    public const int SetupHeap = 0x3D;
    public const int EndOfHeap = 0x3E;
    public const int CreateSema = 0x40;
    public const int DeleteSema = 0x41;
    public const int SignalSema = 0x42;
    public const int WaitSema = 0x44;
    public const int PollSema = 0x45;
    public const int FlushCache = 0x64;
    public const int GsPutIMR = 0x71;
    public const int ISignalSema = -0x43;
    public const int IWakeupThread = -0x34;

    private static readonly (int Number, string Name, bool Implemented)[] Known =
    [
        (ResetEE, "ResetEE", false),
        (KExit, "KExit", true),
        (CreateThread, "CreateThread", true),
        (DeleteThread, "DeleteThread", true),
        (StartThread, "StartThread", true),
        (ExitThread, "ExitThread", true),
        (ExitDeleteThread, "ExitDeleteThread", true),
        (TerminateThread, "TerminateThread", true),
        (ChangeThreadPriority, "ChangeThreadPriority", true),
        (RotateThreadReadyQueue, "RotateThreadReadyQueue", true),
        (GetThreadId, "GetThreadId", true),
        (SleepThread, "SleepThread", true),
        (WakeupThread, "WakeupThread", true),
        (SetupThread, "SetupThread", false),
        (SetupHeap, "SetupHeap", false),
        (EndOfHeap, "EndOfHeap", false),
        (CreateSema, "CreateSema", true),
        (DeleteSema, "DeleteSema", true),
        (SignalSema, "SignalSema", true),
        (WaitSema, "WaitSema", true),
        (PollSema, "PollSema", true),
        (FlushCache, "FlushCache", true),
        (GsPutIMR, "GsPutIMR", false),
        (ISignalSema, "iSignalSema", false),
        (IWakeupThread, "iWakeupThread", false),
    ];

    /// <summary>The name of a call number the kernel layer knows about (implemented or not), or null.</summary>
    public static string? NameOf(int number)
    {
        foreach (var (n, name, _) in Known)
        {
            if (n == number)
            {
                return name;
            }
        }

        return null;
    }

    public static bool IsImplemented(int number)
    {
        foreach (var (n, _, implemented) in Known)
        {
            if (n == number)
            {
                return implemented;
            }
        }

        return false;
    }

    /// <summary>Calls after which the calling thread never continues (the code behind them is not its continuation).</summary>
    public static bool NeverReturns(int number) => number is KExit or ExitThread or ExitDeleteThread;

    public static IReadOnlyList<int> ImplementedCalls()
    {
        var list = new List<int>();
        foreach (var (n, _, implemented) in Known)
        {
            if (implemented)
            {
                list.Add(n);
            }
        }

        return list;
    }

    private enum State
    {
        Dormant,
        Ready,
        Running,
        Sleeping,
        WaitingSema,
    }

    private sealed class Thread
    {
        public int Id;
        public State State;
        public int Priority;
        public uint Entry;
        public uint StackTop;
        public uint Gp;
        public uint Argument;
        public bool Fresh;
        public int WakeupCount;
        public ulong[]? Saved;
        public uint ResumePc;
        public Sema? Waiting;
    }

    private sealed class Sema
    {
        public int Id;
        public int Count;
        public int Max;
        public readonly List<Thread> Waiters = [];
    }

    private readonly Thread?[] _threads = new Thread?[MaxThreads + 1];
    private readonly Sema?[] _semas = new Sema?[MaxSemaphores + 1];
    private readonly List<Thread> _ready = [];
    private readonly TryRead32 _read;
    private Thread _current;

    public Ps2Kernel(TryRead32 read)
    {
        _read = read;
        _current = new Thread { Id = MainThreadId, State = State.Running, Priority = MainThreadPriority };
        _threads[MainThreadId] = _current;
    }

    /// <summary>The id of the thread that is running now.</summary>
    public int CurrentThreadId => _current.Id;

    /// <summary>The thread function returned (or the program jumped to <see cref="ThreadExitAddress"/>): same as ExitThread.</summary>
    public Ps2Result ThreadReturned(ulong[] r) => EndCurrentThread(r, delete: false);

    /// <summary>Performs the kernel call selected by the number in $v1 (r[3]); arguments are in r[4].. and the result goes to r[2].</summary>
    public Ps2Result Handle(ulong[] r, uint continuation)
    {
        var number = unchecked((int)(uint)r[3]);
        var a0 = (uint)r[4];
        var a1 = (uint)r[5];
        switch (number)
        {
            case KExit:
                return new Ps2Result(Ps2Action.Exit, ExitCode: unchecked((int)a0));
            case FlushCache:
                return Return(r, 0, continuation);
            case GetThreadId:
                return Return(r, (uint)_current.Id, continuation);
            case ExitThread:
                return EndCurrentThread(r, delete: false);
            case ExitDeleteThread:
                return EndCurrentThread(r, delete: true);

            case CreateThread:
            {
                if (!_read(a0 + 4, out var entry) || !_read(a0 + 8, out var stack) || !_read(a0 + 0xC, out var stackSize) ||
                    !_read(a0 + 0x10, out var gp) || !_read(a0 + 0x14, out var priority))
                {
                    return Fault($"CreateThread: the thread parameter structure at 0x{a0:X8} is not readable memory.");
                }

                if (entry == 0 || priority >= PriorityCount)
                {
                    return Return(r, Failure, continuation);
                }

                var id = FreeThreadId();
                if (id < 0)
                {
                    return Return(r, Failure, continuation);
                }

                _threads[id] = new Thread
                {
                    Id = id,
                    State = State.Dormant,
                    Priority = (int)priority,
                    Entry = entry,
                    StackTop = unchecked(stack + stackSize),
                    Gp = gp,
                };
                return Return(r, (uint)id, continuation);
            }

            case DeleteThread:
            {
                var t = FindThread(a0);
                if (t is null || t.State != State.Dormant || t == _current)
                {
                    return Return(r, Failure, continuation);
                }

                _threads[t.Id] = null;
                return Return(r, a0, continuation);
            }

            case StartThread:
            {
                var t = FindThread(a0);
                if (t is null || t.State != State.Dormant)
                {
                    return Return(r, Failure, continuation);
                }

                t.Argument = a1;
                t.Fresh = true;
                t.WakeupCount = 0;
                MakeReady(t);
                return Return(r, a0, continuation);
            }

            case TerminateThread:
            {
                var t = FindThread(a0);
                if (t is null || t == _current || t.State == State.Dormant)
                {
                    return Return(r, Failure, continuation);
                }

                Detach(t);
                t.State = State.Dormant;
                t.WakeupCount = 0;
                t.Saved = null;
                return Return(r, a0, continuation);
            }

            case ChangeThreadPriority:
            {
                var t = a0 == 0 ? _current : FindThread(a0);
                if (t is null || t.State == State.Dormant || a1 >= PriorityCount)
                {
                    return Return(r, Failure, continuation);
                }

                t.Priority = (int)a1;
                if (t.State == State.Ready)
                {
                    _ready.Remove(t);
                    _ready.Add(t);
                }

                return Return(r, 0, continuation);
            }

            case RotateThreadReadyQueue:
            {
                if (a0 >= PriorityCount)
                {
                    return Return(r, Failure, continuation);
                }

                var peer = FirstReady((int)a0);
                if (peer is not null)
                {
                    if (_current.Priority == (int)a0)
                    {
                        r[2] = Sx(a0);
                        Save(_current, r, continuation);
                        _current.State = State.Ready;
                        _ready.Add(_current);
                        return Dispatch(PickReady()!, r);
                    }

                    _ready.Remove(peer);
                    _ready.Add(peer);
                }

                return Return(r, a0, continuation);
            }

            case SleepThread:
                if (_current.WakeupCount > 0)
                {
                    _current.WakeupCount--;
                    return Return(r, 0, continuation);
                }

                return Block(r, continuation, State.Sleeping, null, 0);

            case WakeupThread:
            {
                var t = FindThread(a0);
                if (t is null || t.State == State.Dormant)
                {
                    return Return(r, Failure, continuation);
                }

                if (t.State == State.Sleeping)
                {
                    Wake(t, 0);
                }
                else
                {
                    t.WakeupCount++;
                }

                return Return(r, a0, continuation);
            }

            case CreateSema:
            {
                if (!_read(a0 + 4, out var max) || !_read(a0 + 8, out var init))
                {
                    return Fault($"CreateSema: the semaphore parameter structure at 0x{a0:X8} is not readable memory.");
                }

                if (max == 0 || max > int.MaxValue || init > max)
                {
                    return Return(r, Failure, continuation);
                }

                for (var id = 1; id <= MaxSemaphores; id++)
                {
                    if (_semas[id] is null)
                    {
                        _semas[id] = new Sema { Id = id, Count = (int)init, Max = (int)max };
                        return Return(r, (uint)id, continuation);
                    }
                }

                return Return(r, Failure, continuation);
            }

            case DeleteSema:
            {
                var s = FindSema(a0);
                if (s is null)
                {
                    return Return(r, Failure, continuation);
                }

                foreach (var waiter in s.Waiters.ToArray())
                {
                    Wake(waiter, Failure);
                }

                _semas[s.Id] = null;
                return Return(r, a0, continuation);
            }

            case SignalSema:
            {
                var s = FindSema(a0);
                if (s is null)
                {
                    return Return(r, Failure, continuation);
                }

                if (s.Waiters.Count > 0)
                {
                    Wake(s.Waiters[0], a0);
                }
                else if (s.Count < s.Max)
                {
                    s.Count++;
                }
                else
                {
                    return Return(r, Failure, continuation);
                }

                return Return(r, a0, continuation);
            }

            case WaitSema:
            {
                var s = FindSema(a0);
                if (s is null)
                {
                    return Return(r, Failure, continuation);
                }

                if (s.Count > 0)
                {
                    s.Count--;
                    return Return(r, a0, continuation);
                }

                return Block(r, continuation, State.WaitingSema, s, a0);
            }

            case PollSema:
            {
                var s = FindSema(a0);
                if (s is null || s.Count == 0)
                {
                    return Return(r, Failure, continuation);
                }

                s.Count--;
                return Return(r, a0, continuation);
            }

            default:
            {
                var name = NameOf(number);
                return Fault(name is null
                    ? $"Unknown EE kernel call number {number} (0x{unchecked((uint)number):X}); it is not implemented."
                    : $"The EE kernel call {name} (number {number}) is not implemented.");
            }
        }
    }

    private const uint Failure = 0xFFFFFFFF;

    /// <summary>Sign-extends a 32-bit value to the 64-bit register width (kernel results and addresses are held that way).</summary>
    private static ulong Sx(uint value) => unchecked((ulong)(long)(int)value);

    private static Ps2Result Fault(string message) => new(Ps2Action.Fault, Message: message);

    private Thread? FindThread(uint id) => id >= 1 && id <= MaxThreads ? _threads[id] : null;

    private Sema? FindSema(uint id) => id >= 1 && id <= MaxSemaphores ? _semas[id] : null;

    private int FreeThreadId()
    {
        for (var id = 2; id <= MaxThreads; id++)
        {
            if (_threads[id] is null)
            {
                return id;
            }
        }

        return -1;
    }

    private void MakeReady(Thread t)
    {
        t.State = State.Ready;
        _ready.Add(t);
    }

    private Thread? FirstReady(int priority)
    {
        foreach (var t in _ready)
        {
            if (t.Priority == priority)
            {
                return t;
            }
        }

        return null;
    }

    private Thread? PickReady()
    {
        Thread? best = null;
        foreach (var t in _ready)
        {
            if (best is null || t.Priority < best.Priority)
            {
                best = t;
            }
        }

        return best;
    }

    private static void Save(Thread t, ulong[] r, uint resumePc)
    {
        t.Saved = (ulong[])r.Clone();
        t.ResumePc = resumePc;
    }

    /// <summary>Removes a thread from the ready queue and from any semaphore it waits on.</summary>
    private void Detach(Thread t)
    {
        _ready.Remove(t);
        t.Waiting?.Waiters.Remove(t);
        t.Waiting = null;
    }

    /// <summary>Makes a blocked thread ready; the kernel call it blocked in returns <paramref name="value"/>.</summary>
    private void Wake(Thread t, uint value)
    {
        Detach(t);
        t.Saved![2] = Sx(value);
        MakeReady(t);
    }

    /// <summary>Finishes a call that leaves the caller runnable; a higher-priority ready thread preempts it.</summary>
    private Ps2Result Return(ulong[] r, uint value, uint continuation)
    {
        r[2] = Sx(value);
        var best = PickReady();
        if (best is not null && best.Priority < _current.Priority)
        {
            Save(_current, r, continuation);
            _current.State = State.Ready;
            var index = _ready.Count;
            for (var i = 0; i < _ready.Count; i++)
            {
                if (_ready[i].Priority >= _current.Priority)
                {
                    index = i;
                    break;
                }
            }

            _ready.Insert(index, _current);
            return Dispatch(best, r);
        }

        return new Ps2Result(Ps2Action.Continue, continuation);
    }

    private Ps2Result Block(ulong[] r, uint continuation, State state, Sema? sema, uint resumeValue)
    {
        r[2] = Sx(resumeValue);
        Save(_current, r, continuation);
        _current.State = state;
        _current.Waiting = sema;
        sema?.Waiters.Add(_current);
        return SwitchAway(r);
    }

    private Ps2Result EndCurrentThread(ulong[] r, bool delete)
    {
        var finished = _current;
        finished.State = State.Dormant;
        finished.Saved = null;
        finished.WakeupCount = 0;
        if (delete)
        {
            _threads[finished.Id] = null;
        }

        return SwitchAway(r);
    }

    /// <summary>The running thread cannot continue: run the best ready thread, or end the program when none can run.</summary>
    private Ps2Result SwitchAway(ulong[] r)
    {
        var next = PickReady();
        if (next is not null)
        {
            return Dispatch(next, r);
        }

        foreach (var t in _threads)
        {
            if (t is not null && t.State is State.Sleeping or State.WaitingSema)
            {
                return Fault("Every remaining thread is waiting for something that can no longer happen (deadlock). A real console would hang here.");
            }
        }

        return new Ps2Result(Ps2Action.Exit, ExitCode: 0, Message: "All threads have exited. A real console would idle here; the program ends instead.");
    }

    private Ps2Result Dispatch(Thread next, ulong[] r)
    {
        _ready.Remove(next);
        next.State = State.Running;
        _current = next;
        if (next.Fresh)
        {
            next.Fresh = false;
            Array.Clear(r);
            r[4] = Sx(next.Argument);
            r[28] = Sx(next.Gp);
            r[29] = Sx(next.StackTop);
            r[31] = Sx(ThreadExitAddress);
            return new Ps2Result(Ps2Action.Continue, next.Entry);
        }

        Array.Copy(next.Saved!, r, r.Length);
        return new Ps2Result(Ps2Action.Continue, next.ResumePc);
    }
}
