using System.Buffers.Binary;
using System.Text;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation.IR;
using RecompRadar.Semantics;

namespace RecompRadar.Recompilation.Execution;

public sealed class GuestFaultException : Exception
{
    public GuestFaultException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// State of the emulated guest used by the reference interpreter: the shared virtual register file, a program counter and
/// bounds-checked memory. Every invalid access raises <see cref="GuestFaultException"/>. With <see cref="RuntimeProfile.Ps2Ee"/>
/// the memory is the PlayStation 2 EE address space (see <see cref="Ps2Memory"/>) instead of the flat segment list.
/// </summary>
public sealed class GuestMachine : IMemoryReader
{
    public const int RegisterCount = Regs.Count;
    public const uint StackBase = 0x7FFE0000;
    public const uint StackSize = 0x10000;

    private sealed class Region
    {
        public required ulong Base { get; init; }
        public required byte[] Data { get; init; }
        public required bool Writable { get; init; }
        public required bool Executable { get; init; }
    }

    private readonly List<Region> _regions = [];

    public GuestMachine(Endianness endianness, RuntimeProfile profile = RuntimeProfile.RecompRadarTest)
    {
        Endianness = endianness;
        Profile = profile;
        if (profile == RuntimeProfile.Ps2Ee)
        {
            Ps2 = new Ps2Memory(endianness == Endianness.Little, message => new GuestFaultException(message), AppendSerial);
        }
    }

    public ulong[] Registers { get; } = new ulong[RegisterCount];

    public ulong Pc { get; set; }

    public Endianness Endianness { get; }

    public RuntimeProfile Profile { get; }

    /// <summary>The EE address space, or null for the flat profile.</summary>
    public Ps2Memory? Ps2 { get; }

    /// <summary>Everything the program printed (test calls and serial output), in order.</summary>
    public StringBuilder Output { get; } = new();

    /// <summary>Set when a store hit an executable region, so cached translations must be discarded.</summary>
    public bool CodeWritten { get; set; }

    public void AppendOutput(string text)
    {
        if (Output.Length + text.Length <= PocSystemCalls.MaxOutputChars)
        {
            Output.Append(text);
        }
    }

    private void AppendSerial(byte value) => AppendOutput(((char)value).ToString());

    public static GuestMachine FromImage(LoadedImage image, RuntimeProfile profile = RuntimeProfile.RecompRadarTest)
    {
        var machine = new GuestMachine(image.Endianness, profile) { Pc = image.EntryPoint };
        if (profile == RuntimeProfile.Ps2Ee)
        {
            foreach (var segment in image.Segments)
            {
                if (segment.VirtualAddress > uint.MaxValue)
                {
                    throw new InvalidOperationException($"The segment at 0x{segment.VirtualAddress:X} is outside the 32-bit EE address space.");
                }

                machine.Ps2!.LoadSegment((uint)segment.VirtualAddress, segment.Data, segment.Executable);
            }

            machine.Registers[29] = Ps2Memory.InitialStackPointer;
            return machine;
        }

        foreach (var segment in image.Segments)
        {
            machine._regions.Add(new Region
            {
                Base = segment.VirtualAddress,
                Data = segment.Data,
                Writable = segment.Writable,
                Executable = segment.Executable,
            });
        }

        if (machine._regions.Any(r => Overlaps(r, StackBase, StackSize)))
        {
            throw new InvalidOperationException("The image overlaps the reserved stack region.");
        }

        machine._regions.Add(new Region { Base = StackBase, Data = new byte[StackSize], Writable = true, Executable = false });
        machine.Registers[29] = StackBase + StackSize - 16;
        return machine;
    }

    private static bool Overlaps(Region r, ulong start, ulong size) =>
        r.Base < start + size && start < r.Base + (ulong)r.Data.Length;

    public bool TryRead(ulong address, Span<byte> buffer)
    {
        if (Ps2 is not null)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                var b = address + (ulong)i > uint.MaxValue ? -1 : Ps2.TryReadByte((uint)(address + (ulong)i));
                if (b < 0)
                {
                    return false;
                }

                buffer[i] = (byte)b;
            }

            return true;
        }

        if (!TryLocate(address, buffer.Length, out var region, out var offset))
        {
            return false;
        }

        region.Data.AsSpan(offset, buffer.Length).CopyTo(buffer);
        return true;
    }

    private bool TryLocate(ulong address, int length, out Region region, out int offset)
    {
        foreach (var r in _regions)
        {
            if (address >= r.Base)
            {
                var delta = address - r.Base;
                if (delta <= (ulong)r.Data.Length && (ulong)length <= (ulong)r.Data.Length - delta)
                {
                    region = r;
                    offset = (int)delta;
                    return true;
                }
            }
        }

        region = null!;
        offset = 0;
        return false;
    }

    public uint Load(ulong address, int size, bool signExtend)
    {
        address &= 0xFFFFFFFF;
        if (Ps2 is not null)
        {
            return Ps2.Load((uint)address, size, signExtend);
        }

        if (address % (ulong)size != 0)
        {
            throw new GuestFaultException($"Unaligned {size}-byte read at 0x{address:X8}.");
        }

        if (!TryLocate(address, size, out var region, out var offset))
        {
            throw new GuestFaultException($"Read of {size} byte(s) at unmapped address 0x{address:X8}.");
        }

        var span = region.Data.AsSpan(offset, size);
        var little = Endianness == Endianness.Little;
        switch (size)
        {
            case 1:
                return signExtend ? (uint)(sbyte)span[0] : span[0];
            case 2:
            {
                var value = little ? BinaryPrimitives.ReadUInt16LittleEndian(span) : BinaryPrimitives.ReadUInt16BigEndian(span);
                return signExtend ? (uint)(short)value : value;
            }

            default:
                return little ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt32BigEndian(span);
        }
    }

    public ulong Load64(ulong address)
    {
        address &= 0xFFFFFFFF;
        if (Ps2 is not null)
        {
            return Ps2.Load64((uint)address);
        }

        if (address % 8 != 0)
        {
            throw new GuestFaultException($"Unaligned 8-byte read at 0x{address:X8}.");
        }

        if (!TryLocate(address, 8, out var region, out var offset))
        {
            throw new GuestFaultException($"Read of 8 byte(s) at unmapped address 0x{address:X8}.");
        }

        var span = region.Data.AsSpan(offset, 8);
        return Endianness == Endianness.Little ? BinaryPrimitives.ReadUInt64LittleEndian(span) : BinaryPrimitives.ReadUInt64BigEndian(span);
    }

    public void Store64(ulong address, ulong value)
    {
        address &= 0xFFFFFFFF;
        if (Ps2 is not null)
        {
            Ps2.Store64((uint)address, value);
            return;
        }

        if (address % 8 != 0)
        {
            throw new GuestFaultException($"Unaligned 8-byte write at 0x{address:X8}.");
        }

        if (!TryLocate(address, 8, out var region, out var offset))
        {
            throw new GuestFaultException($"Write of 8 byte(s) at unmapped address 0x{address:X8}.");
        }

        if (!region.Writable)
        {
            throw new GuestFaultException($"Write to read-only memory at 0x{address:X8}.");
        }

        var span = region.Data.AsSpan(offset, 8);
        if (Endianness == Endianness.Little)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64BigEndian(span, value);
        }

        if (region.Executable)
        {
            CodeWritten = true;
        }
    }

    public void Store(ulong address, int size, uint value)
    {
        address &= 0xFFFFFFFF;
        if (Ps2 is not null)
        {
            Ps2.Store((uint)address, size, value);
            return;
        }

        if (address % (ulong)size != 0)
        {
            throw new GuestFaultException($"Unaligned {size}-byte write at 0x{address:X8}.");
        }

        if (!TryLocate(address, size, out var region, out var offset))
        {
            throw new GuestFaultException($"Write of {size} byte(s) at unmapped address 0x{address:X8}.");
        }

        if (!region.Writable)
        {
            throw new GuestFaultException($"Write to read-only memory at 0x{address:X8}.");
        }

        var span = region.Data.AsSpan(offset, size);
        var little = Endianness == Endianness.Little;
        switch (size)
        {
            case 1:
                span[0] = (byte)value;
                break;
            case 2:
                if (little)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)value);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)value);
                }

                break;
            default:
                if (little)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(span, value);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(span, value);
                }

                break;
        }

        if (region.Executable)
        {
            CodeWritten = true;
        }
    }
}
