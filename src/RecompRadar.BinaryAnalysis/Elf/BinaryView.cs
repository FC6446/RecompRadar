using System.Buffers.Binary;

namespace RecompRadar.BinaryAnalysis.Elf;

/// <summary>
/// Bounds-checked random access over a seekable stream. Every read is validated against the stream length with
/// overflow-safe arithmetic, so attacker-controlled offsets and sizes can never cause out-of-range reads or huge allocations.
/// </summary>
internal sealed class BinaryView
{
    private readonly Stream _stream;

    public BinaryView(Stream stream)
    {
        _stream = stream;
        Length = stream.Length;
    }

    public long Length { get; }

    /// <summary>True when [offset, offset + size) lies completely inside [0, length). Overflow-safe.</summary>
    public static bool Fits(ulong offset, ulong size, long length) =>
        length >= 0 && offset <= (ulong)length && size <= (ulong)length - offset;

    public bool Fits(ulong offset, ulong size) => Fits(offset, size, Length);

    /// <summary>
    /// Reads exactly <paramref name="size"/> bytes at <paramref name="offset"/>.
    /// Returns null when the range is outside the file or larger than <paramref name="maxBytes"/>.
    /// </summary>
    public byte[]? ReadBytes(ulong offset, ulong size, ulong maxBytes)
    {
        if (size > maxBytes || size > int.MaxValue || !Fits(offset, size))
        {
            return null;
        }

        var buffer = new byte[(int)size];
        if (size == 0)
        {
            return buffer;
        }

        _stream.Seek((long)offset, SeekOrigin.Begin);
        _stream.ReadExactly(buffer);
        return buffer;
    }
}

/// <summary>Endianness- and word-size-aware field access over byte spans.</summary>
internal readonly struct EndianReader
{
    private readonly bool _little;
    private readonly bool _is64;

    public EndianReader(bool little, bool is64)
    {
        _little = little;
        _is64 = is64;
    }

    public ushort U16(ReadOnlySpan<byte> s, int o) =>
        _little ? BinaryPrimitives.ReadUInt16LittleEndian(s[o..]) : BinaryPrimitives.ReadUInt16BigEndian(s[o..]);

    public uint U32(ReadOnlySpan<byte> s, int o) =>
        _little ? BinaryPrimitives.ReadUInt32LittleEndian(s[o..]) : BinaryPrimitives.ReadUInt32BigEndian(s[o..]);

    public ulong U64(ReadOnlySpan<byte> s, int o) =>
        _little ? BinaryPrimitives.ReadUInt64LittleEndian(s[o..]) : BinaryPrimitives.ReadUInt64BigEndian(s[o..]);

    /// <summary>Reads an address-sized field (4 bytes for ELF32, 8 bytes for ELF64).</summary>
    public ulong Addr(ReadOnlySpan<byte> s, int o) => _is64 ? U64(s, o) : U32(s, o);

    public int AddrSize => _is64 ? 8 : 4;
}
