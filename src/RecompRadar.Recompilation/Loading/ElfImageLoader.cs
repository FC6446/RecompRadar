using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.Recompilation.Loading;

/// <summary>Maps the PT_LOAD segments of an ELF file into memory. Sizes and offsets are validated before anything is allocated.</summary>
public sealed class ElfImageLoader : IBinaryLoader
{
    public string Name => "ELF image loader";

    public bool CanLoad(ElfFile elf) => elf.Segments.Any(s => s.Type == ElfConstants.PT_LOAD && s.MemorySize > 0);

    public LoadedImage Load(Stream stream, ElfFile elf, LoaderLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(elf);
        limits ??= new LoaderLimits();

        var loads = elf.Segments.Where(s => s.Type == ElfConstants.PT_LOAD && s.MemorySize > 0).OrderBy(s => s.VirtualAddress).ToList();
        if (loads.Count == 0)
        {
            throw new BinaryLoadException("The file has no loadable segments to map.");
        }

        var segments = new List<LoadedSegment>();
        long total = 0;
        ulong previousEnd = 0;
        foreach (var s in loads)
        {
            if (s.FileSize > s.MemorySize)
            {
                throw new BinaryLoadException($"Segment #{s.Index} has more file data ({s.FileSize} bytes) than memory ({s.MemorySize} bytes).");
            }

            if (s.MemorySize > (ulong)limits.MaxSegmentBytes)
            {
                throw new BinaryLoadException($"Segment #{s.Index} needs {s.MemorySize} bytes of memory, more than the limit of {limits.MaxSegmentBytes} bytes.");
            }

            total += (long)s.MemorySize;
            if (total > limits.MaxTotalBytes)
            {
                throw new BinaryLoadException($"The loadable segments need more than {limits.MaxTotalBytes} bytes in total.");
            }

            if (s.VirtualAddress < previousEnd)
            {
                throw new BinaryLoadException($"Segment #{s.Index} overlaps the previous segment in memory.");
            }

            previousEnd = s.VirtualAddress > ulong.MaxValue - s.MemorySize ? ulong.MaxValue : s.VirtualAddress + s.MemorySize;

            var data = new byte[(int)s.MemorySize];
            if (s.FileSize > 0)
            {
                if (s.Offset > (ulong)stream.Length || s.FileSize > (ulong)stream.Length - s.Offset)
                {
                    throw new BinaryLoadException($"Segment #{s.Index} extends beyond the end of the file.");
                }

                stream.Seek((long)s.Offset, SeekOrigin.Begin);
                stream.ReadExactly(data, 0, (int)s.FileSize);
            }

            segments.Add(new LoadedSegment(s.VirtualAddress, data, s.IsReadable, s.IsWritable, s.IsExecutable));
        }

        return new LoadedImage
        {
            EntryPoint = elf.Header.Entry,
            Family = elf.Header.Machine switch
            {
                ElfConstants.EM_MIPS => CpuFamily.Mips,
                ElfConstants.EM_PPC or ElfConstants.EM_PPC64 => CpuFamily.PowerPc,
                ElfConstants.EM_X86_64 => CpuFamily.X86_64,
                ElfConstants.EM_386 => CpuFamily.X86,
                ElfConstants.EM_ARM => CpuFamily.Arm,
                ElfConstants.EM_AARCH64 => CpuFamily.Arm64,
                _ => CpuFamily.Unknown,
            },
            Endianness = elf.Header.Endianness,
            Segments = segments,
        };
    }
}

/// <summary>Reads guest memory straight from the file through the PT_LOAD mapping, without loading whole segments.</summary>
public sealed class StreamImageReader : IMemoryReader
{
    private readonly Stream _stream;
    private readonly IReadOnlyList<ElfSegment> _loads;

    public StreamImageReader(Stream stream, ElfFile elf)
    {
        _stream = stream;
        _loads = elf.Segments.Where(s => s.Type == ElfConstants.PT_LOAD && s.MemorySize > 0).ToList();
    }

    public bool TryRead(ulong address, Span<byte> buffer)
    {
        foreach (var s in _loads)
        {
            if (address < s.VirtualAddress)
            {
                continue;
            }

            var delta = address - s.VirtualAddress;
            var length = (ulong)buffer.Length;
            if (delta > s.MemorySize || length > s.MemorySize - delta)
            {
                continue;
            }

            buffer.Clear();
            var fileBytes = delta < s.FileSize ? Math.Min(length, s.FileSize - delta) : 0;
            if (fileBytes > 0)
            {
                var fileOffset = s.Offset + delta;
                if (fileOffset < s.Offset || fileOffset > (ulong)_stream.Length || fileBytes > (ulong)_stream.Length - fileOffset)
                {
                    return false;
                }

                _stream.Seek((long)fileOffset, SeekOrigin.Begin);
                _stream.ReadExactly(buffer[..(int)fileBytes]);
            }

            return true;
        }

        return false;
    }
}
