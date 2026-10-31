using RecompRadar.Core.Model;

namespace RecompRadar.Recompilation.Analysis;

public static class AnalysisInputFactory
{
    /// <summary>
    /// Builds the analysis input for a loaded image: executable segments become the code ranges and the ELF's defined
    /// function symbols (when the file has any) seed function discovery.
    /// </summary>
    public static AnalysisInput FromImage(LoadedImage image, ElfFile elf, ICodeTranslator translator, IReadOnlySet<uint>? noReturnSystemCalls = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(elf);
        ArgumentNullException.ThrowIfNull(translator);

        var ranges = image.Segments
            .Where(s => s.Executable && s.Data.Length > 0)
            .Select(s => (s.VirtualAddress, s.VirtualAddress + (ulong)s.Data.Length))
            .ToList();

        var symbols = new Dictionary<ulong, string>();
        foreach (var symbol in elf.Symbols.Where(s => s.IsFunction && !s.IsUndefined && s.Name.Length > 0))
        {
            symbols.TryAdd(symbol.Value, symbol.Name);
        }

        return new AnalysisInput(image, image.Endianness, translator, image.EntryPoint, ranges, symbols, noReturnSystemCalls);
    }
}
