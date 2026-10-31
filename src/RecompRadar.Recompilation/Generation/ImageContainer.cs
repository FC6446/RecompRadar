using System.Buffers.Binary;
using RecompRadar.Core.Model;

namespace RecompRadar.Recompilation.Generation;

/// <summary>
/// The "RRIMG2" container that carries the guest memory image inside a generated program:
/// magic "RRIMG2\0\0", u32 endianness (0 little, 1 big), u32 runtime profile (0 RecompRadar test, 1 PlayStation 2), u32 entry point, u32 segment count, then per segment
/// u32 base address, u32 length, u32 flags (1 read, 2 write, 4 execute) and the bytes. All numbers are little-endian.
/// </summary>
public static class ImageContainer
{
    private static readonly byte[] Magic = "RRIMG2\0\0"u8.ToArray();

    public static byte[] Write(LoadedImage image, RuntimeProfile profile = RuntimeProfile.RecompRadarTest)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.EntryPoint > uint.MaxValue)
        {
            throw new GenerationException($"The entry point 0x{image.EntryPoint:X} does not fit into a 32-bit address space.");
        }

        using var stream = new MemoryStream();
        stream.Write(Magic);
        WriteUInt32(stream, image.Endianness == Endianness.Little ? 0u : 1u);
        WriteUInt32(stream, profile == RuntimeProfile.Ps2Ee ? 1u : 0u);
        WriteUInt32(stream, (uint)image.EntryPoint);
        WriteUInt32(stream, (uint)image.Segments.Count);
        foreach (var segment in image.Segments)
        {
            if (segment.VirtualAddress > uint.MaxValue || (ulong)segment.Data.Length > uint.MaxValue - segment.VirtualAddress + 1)
            {
                throw new GenerationException($"The segment at 0x{segment.VirtualAddress:X} does not fit into a 32-bit address space.");
            }

            WriteUInt32(stream, (uint)segment.VirtualAddress);
            WriteUInt32(stream, (uint)segment.Data.Length);
            WriteUInt32(stream, (segment.Readable ? 1u : 0u) | (segment.Writable ? 2u : 0u) | (segment.Executable ? 4u : 0u));
            stream.Write(segment.Data);
        }

        return stream.ToArray();
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}

public sealed class GenerationException : Exception
{
    public GenerationException(string message)
        : base(message)
    {
    }
}
