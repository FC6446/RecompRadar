using System.Buffers.Binary;
using RecompRadar.Core.Model;

namespace RecompRadar.Recompilation.Execution;

public sealed class GuestFaultException : Exception
{
    public GuestFaultException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// State of the emulated guest used by the reference interpreter: 64 virtual registers (0-31 mirror the guest registers),
/// a program counter and bounds-checked memory. Every invalid access raises <see cref="GuestFaultException"/>.
/// </summary>
public sealed class GuestMachine : IMemoryReader
{
    public const int RegisterCount = 64;
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

    public GuestMachine(Endianness endianness)
    {
        Endianness = endianness;
    }

    public uint[] Registers { get; } = new uint[RegisterCount];

    public ulong Pc { get; set; }

    public Endianness Endianness { get; }

    /// <summary>Set when a store hit an executable region, so cached translations must be discarded.</summary>
    public bool CodeWritten { get; set; }

    public static GuestMachine FromImage(LoadedImage image)
    {
        var machine = new GuestMachine(image.Endianness) { Pc = image.EntryPoint };
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

    public void Store(ulong address, int size, uint value)
    {
        address &= 0xFFFFFFFF;
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
