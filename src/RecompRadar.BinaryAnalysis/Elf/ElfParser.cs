using System.Text;
using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.BinaryAnalysis.Elf;

public sealed record ElfParseResult(ElfFile? Elf, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Success => Elf is not null;
}

/// <summary>
/// Static ELF32/ELF64 parser for little- and big-endian files. It never executes anything, never trusts offsets or sizes
/// from the file, and degrades to a partial result with diagnostics when the file is damaged.
/// </summary>
public sealed class ElfParser
{
    private readonly AnalysisLimits _limits;

    public ElfParser(AnalysisLimits? limits = null)
    {
        _limits = limits ?? AnalysisLimits.Default;
    }

    public ElfParseResult Parse(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var diag = new DiagnosticCollector();

        if (!stream.CanSeek || !stream.CanRead)
        {
            diag.Error("ELF.STREAM", "The input cannot be read randomly.", "RecompRadar needs a seekable file to analyze.");
            return new ElfParseResult(null, diag.Build());
        }

        try
        {
            var elf = new Session(stream, _limits, diag, cancellationToken).Run();
            return new ElfParseResult(elf, diag.Build());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
        {
            diag.Error("ELF.IO_ERROR", $"Reading the file failed: {ex.Message}", "The file may have been moved, locked, or truncated while it was being analyzed.");
            return new ElfParseResult(null, diag.Build());
        }
        catch (OutOfMemoryException)
        {
            diag.Error("ELF.OUT_OF_MEMORY", "Not enough memory to analyze this file.", "Lower the file size limit in Settings or close other applications.");
            return new ElfParseResult(null, diag.Build());
        }
        catch (Exception ex)
        {
            // A parser bug must never take the application down; it is reported as an error so tests and users can see it.
            diag.Error("ELF.INTERNAL_ERROR", $"Unexpected parser failure ({ex.GetType().Name}): {ex.Message}", "This is a bug in RecompRadar. Please report it together with the file's SHA-256.");
            return new ElfParseResult(null, diag.Build());
        }
    }

    private readonly record struct RawSection(
        uint Name, uint Type, ulong Flags, ulong Address, ulong Offset, ulong Size,
        uint Link, uint Info, ulong Align, ulong EntSize);

    private sealed class Session
    {
        private const ulong MaxTableBlockBytes = 64UL * 1024 * 1024;
        private const int MaxNameBytes = 4096;

        private readonly BinaryView _view;
        private readonly AnalysisLimits _limits;
        private readonly DiagnosticCollector _diag;
        private readonly CancellationToken _ct;
        private readonly Dictionary<uint, byte[]?> _stringTables = [];

        private bool _is64;
        private bool _little;
        private EndianReader _e;
        private List<RawSection> _raw = [];
        private List<ElfSection> _sections = [];
        private List<ElfSegment> _segments = [];

        public Session(Stream stream, AnalysisLimits limits, DiagnosticCollector diag, CancellationToken ct)
        {
            _view = new BinaryView(stream);
            _limits = limits;
            _diag = diag;
            _ct = ct;
        }

        public ElfFile? Run()
        {
            var length = _view.Length;
            if (length < 16)
            {
                _diag.Error("ELF.TOO_SMALL", $"The file is only {length} bytes long; an ELF file needs at least a 16-byte identification block.",
                    "The file is empty or was cut off. Check that the copy or download completed.");
                return null;
            }

            var ident = _view.ReadBytes(0, 16, 16)!;
            if (ident[0] != 0x7F || ident[1] != (byte)'E' || ident[2] != (byte)'L' || ident[3] != (byte)'F')
            {
                _diag.Error("ELF.BAD_MAGIC", "The file does not start with the ELF signature (7F 45 4C 46).",
                    "This is not an ELF file. Other executable formats (for example PS-X EXE, SELF, DOL or PE) are not supported yet.");
                return null;
            }

            if (ident[4] is not (1 or 2))
            {
                _diag.Error("ELF.BAD_CLASS", $"Invalid ELF class byte 0x{ident[4]:X2} (expected 1 = 32-bit or 2 = 64-bit).",
                    "The identification block is damaged, so the layout of the rest of the file cannot be determined.");
                return null;
            }

            if (ident[5] is not (1 or 2))
            {
                _diag.Error("ELF.BAD_DATA", $"Invalid ELF data-encoding byte 0x{ident[5]:X2} (expected 1 = little endian or 2 = big endian).",
                    "Without the byte order, no multi-byte field can be read reliably.");
                return null;
            }

            _is64 = ident[4] == 2;
            _little = ident[5] == 1;
            _e = new EndianReader(_little, _is64);

            var headerSize = _is64 ? 64 : 52;
            if (length < headerSize)
            {
                _diag.Error("ELF.TRUNCATED_HEADER", $"The ELF header needs {headerSize} bytes but the file has only {length}.",
                    "The file is cut off inside its header.");
                return null;
            }

            if (ident[6] != 1)
            {
                _diag.Warn("ELF.IDENT_VERSION", $"Unexpected ELF identification version {ident[6]} (expected 1).");
            }

            var h = _view.ReadBytes(0, (ulong)headerSize, 64)!;
            var type = _e.U16(h, 16);
            var machine = _e.U16(h, 18);
            var version = _e.U32(h, 20);
            var o = 24;
            var entry = _e.Addr(h, o);
            o += _e.AddrSize;
            var phoff = _e.Addr(h, o);
            o += _e.AddrSize;
            var shoff = _e.Addr(h, o);
            o += _e.AddrSize;
            var flags = _e.U32(h, o);
            var ehsize = _e.U16(h, o + 4);
            var phentsize = _e.U16(h, o + 6);
            var phnum = _e.U16(h, o + 8);
            var shentsize = _e.U16(h, o + 10);
            var shnum = _e.U16(h, o + 12);
            var shstrndx = _e.U16(h, o + 14);

            if (version != 1)
            {
                _diag.Warn("ELF.VERSION", $"Unexpected ELF version {version} (expected 1).");
            }

            if (ehsize != headerSize)
            {
                _diag.Warn("ELF.EHSIZE", $"The header declares a size of {ehsize} bytes; {headerSize} is expected for this ELF class.");
            }

            // Extended counts (ELF gABI): the real values live in section header 0 when the 16-bit fields overflow.
            uint phCount = phnum;
            uint shCount = shnum;
            uint shStrIndex = shstrndx;
            if (shoff != 0 && (shnum == 0 || phnum == 0xFFFF || shstrndx == 0xFFFF))
            {
                var section0 = ReadRawSectionAt(shoff, shentsize, 0);
                if (section0 is { } s0)
                {
                    if (shnum == 0)
                    {
                        shCount = (uint)Math.Min(s0.Size, uint.MaxValue);
                    }

                    if (phnum == 0xFFFF)
                    {
                        phCount = s0.Info;
                    }

                    if (shstrndx == 0xFFFF)
                    {
                        shStrIndex = s0.Link;
                    }
                }
            }

            var header = new ElfHeader
            {
                Class = _is64 ? ElfClass.Elf64 : ElfClass.Elf32,
                Endianness = _little ? Endianness.Little : Endianness.Big,
                IdentVersion = ident[6],
                OsAbi = ident[7],
                OsAbiName = ElfNames.OsAbiName(ident[7]),
                AbiVersion = ident[8],
                Type = type,
                TypeName = ElfNames.TypeName(type),
                Machine = machine,
                MachineName = ElfNames.MachineName(machine),
                Version = version,
                Entry = entry,
                ProgramHeaderOffset = phoff,
                SectionHeaderOffset = shoff,
                Flags = flags,
                HeaderSize = ehsize,
                ProgramHeaderEntrySize = phentsize,
                ProgramHeaderCount = phCount,
                SectionHeaderEntrySize = shentsize,
                SectionHeaderCount = shCount,
                SectionNameStringIndex = shStrIndex,
            };

            _ct.ThrowIfCancellationRequested();
            _segments = ReadSegments(phoff, phCount, phentsize);
            ValidateSegments();

            _ct.ThrowIfCancellationRequested();
            _raw = ReadRawSections(shoff, shCount, shentsize);
            _sections = BuildSections(shStrIndex);
            ValidateSections();

            _ct.ThrowIfCancellationRequested();
            var symbols = ReadSymbols(out var hasSymtab, out var hasDynsym, out var symtabCount, out var dynsymCount, out var truncated);

            _ct.ThrowIfCancellationRequested();
            var dynamic = ReadDynamic();

            var regions = BuildRegions();
            var executable = regions.Where(r => r.Executable).ToList();
            var (location, inExecutable) = LocateEntryPoint(header, regions);

            if (_segments.Count == 0 && _sections.Count == 0)
            {
                _diag.Warn("ELF.NO_TABLES", "The file has neither program headers nor section headers.",
                    "Only the ELF header could be read. The file may be stripped in an unusual way, or the header tables are damaged.");
            }

            return new ElfFile
            {
                Header = header,
                Segments = _segments,
                Sections = _sections,
                Symbols = symbols,
                Dynamic = dynamic,
                MemoryRegions = regions,
                ExecutableRegions = executable,
                HasProgramHeaders = _segments.Count > 0,
                HasSectionHeaders = _sections.Count > 0,
                HasSymbolTable = hasSymtab,
                HasDynamicSymbolTable = hasDynsym,
                SymbolTableEntryCount = symtabCount,
                DynamicSymbolEntryCount = dynsymCount,
                SymbolsTruncated = truncated,
                EntryPointLocation = location,
                EntryPointInExecutableRegion = inExecutable,
            };
        }

        // ---- tables --------------------------------------------------------------------------------------------

        /// <summary>
        /// Limits a declared table size to what really fits in the file, the configured limit and the block-size cap.
        /// All arithmetic is overflow-safe; returns the number of entries that may be read.
        /// </summary>
        private ulong ClampCount(string what, string code, ulong offset, ulong declared, ulong stride, int minEntry, int maxCount)
        {
            if (declared == 0)
            {
                return 0;
            }

            if (stride < (ulong)minEntry)
            {
                _diag.Warn(code, $"{what}: entry size {stride} is smaller than the required {minEntry} bytes; the table was skipped.",
                    "The table description in the header is inconsistent.");
                return 0;
            }

            var count = declared;
            if (count > (ulong)maxCount)
            {
                _diag.Warn(code + ".LIMIT", $"{what} declares {declared} entries; only the first {maxCount} are read.",
                    "A configured safety limit was reached. The result is partial.");
                count = (ulong)maxCount;
            }

            var length = (ulong)_view.Length;
            var available = offset < length ? length - offset : 0;
            if (available < (ulong)minEntry)
            {
                _diag.Warn(code, $"{what}: the table at offset 0x{offset:X} lies outside the file (file size {length} bytes).",
                    "The offset in the header points past the end of the file; the file is probably truncated or damaged.");
                return 0;
            }

            var fits = ((available - (ulong)minEntry) / stride) + 1;
            if (count > fits)
            {
                _diag.Warn(code, $"{what} declares {declared} entries but only {fits} fit inside the file.",
                    "The file is probably truncated. Only complete entries are read.");
                count = fits;
            }

            var fitsBlock = ((MaxTableBlockBytes - (ulong)minEntry) / stride) + 1;
            if (count > fitsBlock)
            {
                _diag.Warn(code + ".LIMIT", $"{what}: entries are too large to read {count} of them safely; reading {fitsBlock}.");
                count = fitsBlock;
            }

            return count;
        }

        private byte[]? ReadTableBlock(ulong offset, ulong count, ulong stride, int minEntry)
        {
            if (count == 0)
            {
                return null;
            }

            var size = ((count - 1) * stride) + (ulong)minEntry;
            return _view.ReadBytes(offset, size, MaxTableBlockBytes);
        }

        private List<ElfSegment> ReadSegments(ulong phoff, uint phnum, ushort phentsize)
        {
            var result = new List<ElfSegment>();
            if (phnum == 0)
            {
                return result;
            }

            var min = _is64 ? 56 : 32;
            if (phoff == 0)
            {
                _diag.Warn("ELF.PH_OFFSET", "The header declares program headers but their offset is 0.");
                return result;
            }

            var count = ClampCount("Program header table", "ELF.PH_TABLE", phoff, phnum, phentsize, min, _limits.MaxProgramHeaders);
            var block = ReadTableBlock(phoff, count, phentsize, min);
            if (block is null)
            {
                return result;
            }

            for (var i = 0; i < (int)count; i++)
            {
                var b = block.AsSpan(i * phentsize, min);
                uint type, flags;
                ulong offset, vaddr, paddr, filesz, memsz, align;
                if (_is64)
                {
                    type = _e.U32(b, 0);
                    flags = _e.U32(b, 4);
                    offset = _e.U64(b, 8);
                    vaddr = _e.U64(b, 16);
                    paddr = _e.U64(b, 24);
                    filesz = _e.U64(b, 32);
                    memsz = _e.U64(b, 40);
                    align = _e.U64(b, 48);
                }
                else
                {
                    type = _e.U32(b, 0);
                    offset = _e.U32(b, 4);
                    vaddr = _e.U32(b, 8);
                    paddr = _e.U32(b, 12);
                    filesz = _e.U32(b, 16);
                    memsz = _e.U32(b, 20);
                    flags = _e.U32(b, 24);
                    align = _e.U32(b, 28);
                }

                result.Add(new ElfSegment
                {
                    Index = i,
                    Type = type,
                    TypeName = ElfNames.SegmentTypeName(type),
                    Flags = flags,
                    Offset = offset,
                    VirtualAddress = vaddr,
                    PhysicalAddress = paddr,
                    FileSize = filesz,
                    MemorySize = memsz,
                    Alignment = align,
                });
            }

            return result;
        }

        private void ValidateSegments()
        {
            foreach (var s in _segments)
            {
                if (s.FileSize > 0 && !_view.Fits(s.Offset, s.FileSize))
                {
                    _diag.Warn("ELF.SEGMENT_RANGE",
                        $"Segment #{s.Index} ({s.TypeName}) claims {s.FileSize} bytes at file offset 0x{s.Offset:X}, which extends past the end of the file ({_view.Length} bytes).",
                        "The file is truncated, or the segment header is corrupt. Data from this segment is not read beyond the file.");
                }

                if (s.Type == 1)
                {
                    if (s.FileSize > s.MemorySize)
                    {
                        _diag.Warn("ELF.SEGMENT_SIZE", $"Loadable segment #{s.Index} has a file size ({s.FileSize}) larger than its memory size ({s.MemorySize}).",
                            "The ELF specification forbids this; the segment header is inconsistent.");
                    }

                    if (s.Alignment > 1 && (s.Alignment & (s.Alignment - 1)) == 0 && (s.Offset % s.Alignment) != (s.VirtualAddress % s.Alignment))
                    {
                        _diag.Warn("ELF.SEGMENT_ALIGN", $"Loadable segment #{s.Index}: file offset 0x{s.Offset:X} and address 0x{s.VirtualAddress:X} are not congruent modulo the alignment 0x{s.Alignment:X}.",
                            "A conforming loader could not map this segment; the header may be damaged or produced by a non-standard tool.");
                    }
                }
            }

            var loads = _segments.Where(s => s.Type == 1 && s.MemorySize > 0).OrderBy(s => s.VirtualAddress).ToList();
            for (var i = 1; i < loads.Count; i++)
            {
                var previous = loads[i - 1];
                var previousEnd = SaturatingAdd(previous.VirtualAddress, previous.MemorySize);
                if (previousEnd > loads[i].VirtualAddress)
                {
                    _diag.Warn("ELF.LOAD_OVERLAP", $"Loadable segments #{previous.Index} and #{loads[i].Index} overlap in memory (0x{loads[i].VirtualAddress:X} < 0x{previousEnd:X}).",
                        "Overlapping load segments are unusual and may indicate a damaged or hand-made file.");
                }
            }
        }

        private RawSection? ReadRawSectionAt(ulong shoff, ushort shentsize, ulong index)
        {
            var min = _is64 ? 64 : 40;
            if (shentsize < min)
            {
                return null;
            }

            var offset = unchecked(shoff + (index * shentsize));
            if (offset < shoff)
            {
                return null;
            }

            var bytes = _view.ReadBytes(offset, (ulong)min, (ulong)min);
            return bytes is null ? null : ParseRawSection(bytes);
        }

        private RawSection ParseRawSection(ReadOnlySpan<byte> b)
        {
            if (_is64)
            {
                return new RawSection(_e.U32(b, 0), _e.U32(b, 4), _e.U64(b, 8), _e.U64(b, 16), _e.U64(b, 24), _e.U64(b, 32),
                    _e.U32(b, 40), _e.U32(b, 44), _e.U64(b, 48), _e.U64(b, 56));
            }

            return new RawSection(_e.U32(b, 0), _e.U32(b, 4), _e.U32(b, 8), _e.U32(b, 12), _e.U32(b, 16), _e.U32(b, 20),
                _e.U32(b, 24), _e.U32(b, 28), _e.U32(b, 32), _e.U32(b, 36));
        }

        private List<RawSection> ReadRawSections(ulong shoff, uint shnum, ushort shentsize)
        {
            var result = new List<RawSection>();
            if (shnum == 0 || shoff == 0)
            {
                return result;
            }

            var min = _is64 ? 64 : 40;
            var count = ClampCount("Section header table", "ELF.SH_TABLE", shoff, shnum, shentsize, min, _limits.MaxSectionHeaders);
            var block = ReadTableBlock(shoff, count, shentsize, min);
            if (block is null)
            {
                return result;
            }

            for (var i = 0; i < (int)count; i++)
            {
                result.Add(ParseRawSection(block.AsSpan(i * shentsize, min)));
            }

            return result;
        }

        private List<ElfSection> BuildSections(uint shStrIndex)
        {
            var result = new List<ElfSection>(_raw.Count);
            if (_raw.Count == 0)
            {
                return result;
            }

            byte[]? names = null;
            // Index 0 (SHN_UNDEF) means the file deliberately has no section names.
            if (shStrIndex != 0)
            {
                if (shStrIndex < _raw.Count)
                {
                    names = GetStringTable(shStrIndex);
                }
                else
                {
                    _diag.Warn("ELF.SHSTRNDX", $"The section-name string table index {shStrIndex} is outside the {_raw.Count} sections.",
                        "Section names cannot be resolved and are shown by index.");
                }
            }

            for (var i = 0; i < _raw.Count; i++)
            {
                var r = _raw[i];
                var name = names is null ? "" : ReadString(names, r.Name);
                result.Add(new ElfSection
                {
                    Index = i,
                    Name = name,
                    Type = r.Type,
                    TypeName = ElfNames.SectionTypeName(r.Type),
                    Flags = r.Flags,
                    Address = r.Address,
                    Offset = r.Offset,
                    Size = r.Size,
                    Link = r.Link,
                    Info = r.Info,
                    AddressAlign = r.Align,
                    EntrySize = r.EntSize,
                });
            }

            return result;
        }

        private void ValidateSections()
        {
            foreach (var s in _sections)
            {
                if (s.HasFileData && s.Size > 0 && !_view.Fits(s.Offset, s.Size))
                {
                    _diag.Warn("ELF.SECTION_RANGE",
                        $"Section #{s.Index} '{s.Name}' claims {s.Size} bytes at file offset 0x{s.Offset:X}, which extends past the end of the file ({_view.Length} bytes).",
                        "The file is truncated or the section header is corrupt. The content of this section is not read.");
                }
            }
        }

        private byte[]? GetStringTable(uint index)
        {
            if (_stringTables.TryGetValue(index, out var cached))
            {
                return cached;
            }

            byte[]? bytes = null;
            if (index < _raw.Count)
            {
                var s = _raw[(int)index];
                if (s.Type is 0 or 8)
                {
                    _diag.Warn("ELF.STRTAB_TYPE", $"Section #{index} is used as a string table but has type {ElfNames.SectionTypeName(s.Type)}; names cannot be resolved.");
                }
                else
                {
                    if (s.Type != 3)
                    {
                        _diag.Warn("ELF.STRTAB_TYPE", $"Section #{index} is used as a string table but has type {ElfNames.SectionTypeName(s.Type)} instead of SHT_STRTAB.");
                    }

                    bytes = _view.ReadBytes(s.Offset, s.Size, (ulong)_limits.MaxStringTableBytes);
                    if (bytes is null)
                    {
                        _diag.Warn("ELF.STRTAB_RANGE", $"String table section #{index} (offset 0x{s.Offset:X}, size {s.Size}) is outside the file or larger than the safety limit; names cannot be resolved.",
                            "Names for symbols or sections that use this table are shown empty.");
                    }
                }
            }
            else
            {
                _diag.Warn("ELF.STRTAB_INDEX", $"A string table reference points to section {index}, but the file has only {_raw.Count} sections.");
            }

            _stringTables[index] = bytes;
            return bytes;
        }

        private static string ReadString(byte[]? table, ulong offset)
        {
            if (table is null || offset >= (ulong)table.Length)
            {
                return "";
            }

            var span = table.AsSpan((int)offset);
            var end = span.IndexOf((byte)0);
            if (end < 0)
            {
                end = span.Length;
            }

            if (end > MaxNameBytes)
            {
                end = MaxNameBytes;
            }

            var text = Encoding.UTF8.GetString(span[..end]);
            return text.Any(char.IsControl) ? new string(text.Select(c => char.IsControl(c) ? '?' : c).ToArray()) : text;
        }

        // ---- symbols -------------------------------------------------------------------------------------------

        private List<ElfSymbol> ReadSymbols(out bool hasSymtab, out bool hasDynsym, out int symtabCount, out int dynsymCount, out bool truncated)
        {
            hasSymtab = false;
            hasDynsym = false;
            symtabCount = 0;
            dynsymCount = 0;
            truncated = false;
            var result = new List<ElfSymbol>();
            var min = _is64 ? 24 : 16;

            foreach (var section in _sections)
            {
                if (section.Type is not (2 or 11) || !section.HasFileData)
                {
                    continue;
                }

                _ct.ThrowIfCancellationRequested();
                var table = section.Type == 2 ? "symtab" : "dynsym";
                var stride = section.EntrySize == 0 ? (ulong)min : section.EntrySize;
                var declared = section.Size / stride;
                var strings = GetStringTable(section.Link);
                var count = ClampCount($"Symbol table '{section.Name}'", "ELF.SYMTAB", section.Offset, declared, stride, min, _limits.MaxSymbolsPerTable);
                var block = ReadTableBlock(section.Offset, count, stride, min);
                if (block is null)
                {
                    continue;
                }

                if (section.Type == 2)
                {
                    hasSymtab = true;
                    symtabCount += (int)Math.Min(declared, int.MaxValue / 2);
                }
                else
                {
                    hasDynsym = true;
                    dynsymCount += (int)Math.Min(declared, int.MaxValue / 2);
                }

                if (count < declared)
                {
                    truncated = true;
                }

                // Entry 0 is the mandatory null symbol.
                for (var i = 1; i < (int)count; i++)
                {
                    var b = block.AsSpan((int)((ulong)i * stride), min);
                    uint nameOffset = _e.U32(b, 0);
                    ulong value, size;
                    byte info, other;
                    ushort shndx;
                    if (_is64)
                    {
                        info = b[4];
                        other = b[5];
                        shndx = _e.U16(b, 6);
                        value = _e.U64(b, 8);
                        size = _e.U64(b, 16);
                    }
                    else
                    {
                        value = _e.U32(b, 4);
                        size = _e.U32(b, 8);
                        info = b[12];
                        other = b[13];
                        shndx = _e.U16(b, 14);
                    }

                    var type = (byte)(info & 0x0F);
                    var binding = (byte)(info >> 4);
                    var visibility = (byte)(other & 0x03);
                    result.Add(new ElfSymbol
                    {
                        Name = ReadString(strings, nameOffset),
                        Value = value,
                        Size = size,
                        Type = type,
                        TypeName = ElfNames.SymbolTypeName(type),
                        Binding = binding,
                        BindingName = ElfNames.SymbolBindingName(binding),
                        Visibility = visibility,
                        VisibilityName = ElfNames.SymbolVisibilityName(visibility),
                        SectionIndex = shndx,
                        Table = table,
                    });
                }
            }

            return result;
        }

        // ---- dynamic linking information -----------------------------------------------------------------------

        private ElfDynamicInfo ReadDynamic()
        {
            var dynSegment = _segments.FirstOrDefault(s => s.Type == 2);
            var dynSection = _sections.FirstOrDefault(s => s.Type == 6 && s.HasFileData);
            var interpreter = ReadInterpreter();

            if (dynSegment is null && dynSection is null)
            {
                return new ElfDynamicInfo { Interpreter = interpreter };
            }

            var entrySize = _is64 ? 16 : 8;
            var maxBytes = (ulong)_limits.MaxDynamicEntries * (ulong)entrySize;
            byte[]? bytes = null;
            if (dynSection is not null)
            {
                bytes = _view.ReadBytes(dynSection.Offset, Math.Min(dynSection.Size, maxBytes), maxBytes);
            }

            if (bytes is null && dynSegment is not null)
            {
                bytes = _view.ReadBytes(dynSegment.Offset, Math.Min(dynSegment.FileSize, maxBytes), maxBytes);
            }

            if (bytes is null)
            {
                _diag.Warn("ELF.DYNAMIC_RANGE", "The dynamic section lies outside the file; shared library dependencies cannot be listed.",
                    "The file is probably truncated.");
                return new ElfDynamicInfo
                {
                    HasDynamicSegment = dynSegment is not null,
                    HasDynamicSection = dynSection is not null,
                    Interpreter = interpreter,
                };
            }

            var raw = new List<(long Tag, ulong Value)>();
            var n = Math.Min(bytes.Length / entrySize, _limits.MaxDynamicEntries);
            for (var i = 0; i < n; i++)
            {
                var b = bytes.AsSpan(i * entrySize, entrySize);
                long tag = _is64 ? (long)_e.U64(b, 0) : (int)_e.U32(b, 0);
                var value = _e.Addr(b, _e.AddrSize);
                raw.Add((tag, value));
                if (tag == 0)
                {
                    break;
                }
            }

            byte[]? strings = null;
            if (dynSection is not null && dynSection.Link != 0)
            {
                strings = GetStringTable(dynSection.Link);
            }

            if (strings is null)
            {
                strings = ReadDynamicStringsViaAddress(raw);
            }

            var needed = new List<string>();
            string? soname = null, rpath = null, runpath = null;
            var entries = new List<ElfDynamicEntry>(raw.Count);
            foreach (var (tag, value) in raw)
            {
                string? text = null;
                if (tag is 1 or 14 or 15 or 29)
                {
                    text = ReadString(strings, value);
                    switch (tag)
                    {
                        case 1 when text.Length > 0:
                            needed.Add(text);
                            break;
                        case 14:
                            soname = text;
                            break;
                        case 15:
                            rpath = text;
                            break;
                        case 29:
                            runpath = text;
                            break;
                    }

                    if (text.Length == 0)
                    {
                        _diag.Warn("ELF.DYNSTR", $"The dynamic entry {ElfNames.DynamicTagName(tag)} refers to a string that could not be read.",
                            "The dynamic string table is missing or damaged, so the library name is unavailable.");
                    }
                }

                entries.Add(new ElfDynamicEntry { Tag = tag, TagName = ElfNames.DynamicTagName(tag), Value = value, StringValue = text });
            }

            return new ElfDynamicInfo
            {
                HasDynamicSegment = dynSegment is not null,
                HasDynamicSection = dynSection is not null,
                Interpreter = interpreter,
                NeededLibraries = needed,
                SharedObjectName = soname,
                RPath = rpath,
                RunPath = runpath,
                Entries = entries,
            };
        }

        private byte[]? ReadDynamicStringsViaAddress(List<(long Tag, ulong Value)> entries)
        {
            ulong? strtab = null, strsz = null;
            foreach (var (tag, value) in entries)
            {
                if (tag == 5)
                {
                    strtab = value;
                }
                else if (tag == 10)
                {
                    strsz = value;
                }
            }

            if (strtab is null)
            {
                return null;
            }

            if (!TryTranslate(strtab.Value, out var fileOffset, out var available))
            {
                return null;
            }

            var size = strsz is { } s ? Math.Min(s, available) : available;
            return _view.ReadBytes(fileOffset, size, (ulong)_limits.MaxStringTableBytes);
        }

        /// <summary>Maps a virtual address to a file offset using the PT_LOAD segments; also returns the bytes available from there.</summary>
        private bool TryTranslate(ulong address, out ulong fileOffset, out ulong available)
        {
            foreach (var s in _segments)
            {
                if (s.Type != 1 || address < s.VirtualAddress)
                {
                    continue;
                }

                var delta = address - s.VirtualAddress;
                if (delta >= s.FileSize)
                {
                    continue;
                }

                var offset = unchecked(s.Offset + delta);
                if (offset < s.Offset)
                {
                    continue;
                }

                fileOffset = offset;
                available = s.FileSize - delta;
                return true;
            }

            fileOffset = 0;
            available = 0;
            return false;
        }

        private string? ReadInterpreter()
        {
            var segment = _segments.FirstOrDefault(s => s.Type == 3);
            if (segment is null || segment.FileSize == 0)
            {
                return null;
            }

            var bytes = _view.ReadBytes(segment.Offset, Math.Min(segment.FileSize, 4096), 4096);
            if (bytes is null)
            {
                return null;
            }

            var text = ReadString(bytes, 0);
            return text.Length == 0 ? null : text;
        }

        // ---- regions and entry point ---------------------------------------------------------------------------

        private List<MemoryRegion> BuildRegions()
        {
            var regions = new List<MemoryRegion>();
            foreach (var s in _segments.Where(s => s.Type == 1))
            {
                regions.Add(new MemoryRegion
                {
                    Name = $"LOAD #{s.Index}",
                    Source = "segment",
                    VirtualAddress = s.VirtualAddress,
                    Size = s.MemorySize,
                    FileOffset = s.Offset,
                    FileSize = s.FileSize,
                    Readable = s.IsReadable,
                    Writable = s.IsWritable,
                    Executable = s.IsExecutable,
                });
            }

            foreach (var s in _sections.Where(s => s.IsAllocated && s.Size > 0))
            {
                regions.Add(new MemoryRegion
                {
                    Name = s.Name.Length > 0 ? s.Name : $"section #{s.Index}",
                    Source = "section",
                    VirtualAddress = s.Address,
                    Size = s.Size,
                    FileOffset = s.HasFileData ? s.Offset : null,
                    FileSize = s.HasFileData ? s.Size : 0,
                    Readable = true,
                    Writable = s.IsWritable,
                    Executable = s.IsExecutable,
                });
            }

            return regions;
        }

        private (string? Location, bool? InExecutable) LocateEntryPoint(ElfHeader header, List<MemoryRegion> regions)
        {
            // Relocatable objects and core dumps have no meaningful entry point.
            if (header.Type is 1 or 4)
            {
                return (null, null);
            }

            if (header.Entry == 0)
            {
                if (header.Type == 2)
                {
                    _diag.Info("ELF.ENTRY_ZERO", "The entry point is 0.", "An executable normally has a non-zero entry point; the file may be a library or a raw image.");
                }

                return (null, null);
            }

            var containing = regions.Where(r => header.Entry >= r.VirtualAddress && header.Entry - r.VirtualAddress < r.Size).ToList();
            if (containing.Count == 0)
            {
                _diag.Warn("ELF.ENTRY_UNMAPPED", $"The entry point 0x{header.Entry:X} is not inside any loadable segment or section.",
                    "The file may be damaged, or the program is relocated or unpacked by a loader before it starts.");
                return (null, null);
            }

            var location = string.Join(", ", containing.Select(r => r.Source == "segment" ? $"{r.Name} ({r.Permissions})" : $"section {r.Name}"));
            var inExecutable = containing.Any(r => r.Executable);
            if (!inExecutable)
            {
                _diag.Warn("ELF.ENTRY_NOT_EXECUTABLE", $"The entry point 0x{header.Entry:X} is located in a region that is not marked executable ({location}).",
                    "Execution would start in non-executable memory. The file may be damaged or use an unusual loader.");
            }

            return (location, inExecutable);
        }

        private static ulong SaturatingAdd(ulong a, ulong b) => a > ulong.MaxValue - b ? ulong.MaxValue : a + b;
    }
}
