using System.Text;
using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.BinaryAnalysis.Dependencies;

/// <summary>
/// Extracts printable ASCII strings from non-executable data of an ELF file for knowledge-base matching.
/// Total work is bounded by <see cref="AnalysisLimits.MaxStringScanBytes"/>.
/// </summary>
internal static class StringScanner
{
    private const int MinLength = 4;
    private const int ChunkSize = 1 << 20;

    /// <summary>Returns the number of strings that were passed to <paramref name="onString"/>.</summary>
    public static int Scan(ElfFile elf, Stream stream, AnalysisLimits limits, CancellationToken cancellationToken, Action<string> onString)
    {
        var ranges = new List<(ulong Offset, ulong Size)>();
        foreach (var s in elf.Sections.Where(s => s.IsAllocated && !s.IsExecutable && s.Type == 1 && s.Size > 0))
        {
            ranges.Add((s.Offset, s.Size));
        }

        if (ranges.Count == 0)
        {
            foreach (var s in elf.Segments.Where(s => s.Type == 1 && !s.IsExecutable && s.FileSize > 0))
            {
                ranges.Add((s.Offset, s.FileSize));
            }
        }

        var budget = (ulong)Math.Max(0, limits.MaxStringScanBytes);
        var length = (ulong)stream.Length;
        var buffer = new byte[ChunkSize];
        var current = new StringBuilder();
        var count = 0;

        void Flush()
        {
            if (current.Length >= MinLength)
            {
                onString(current.ToString());
                count++;
            }

            current.Clear();
        }

        foreach (var (offset, size) in ranges)
        {
            if (budget == 0 || offset >= length)
            {
                continue;
            }

            var remaining = Math.Min(Math.Min(size, length - offset), budget);
            budget -= remaining;
            stream.Seek((long)offset, SeekOrigin.Begin);
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var want = (int)Math.Min(remaining, (ulong)buffer.Length);
                var read = stream.Read(buffer, 0, want);
                if (read <= 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    var b = buffer[i];
                    if (b is >= 0x20 and <= 0x7E)
                    {
                        if (current.Length < limits.MaxStringLength)
                        {
                            current.Append((char)b);
                        }
                    }
                    else
                    {
                        Flush();
                    }
                }

                remaining -= (ulong)read;
            }

            Flush();
        }

        return count;
    }
}
