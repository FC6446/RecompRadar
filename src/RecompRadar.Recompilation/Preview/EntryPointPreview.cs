using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation.Loading;

namespace RecompRadar.Recompilation.Preview;

public sealed record PreviewResult(
    string PluginName,
    string DecoderName,
    IReadOnlyList<DecodedInstruction> Instructions,
    int UnknownCount,
    string? Note);

/// <summary>Decodes the first instructions at the entry point with the decoder of a matching plugin. Read-only and bounded.</summary>
public static class EntryPointPreview
{
    public static PreviewResult? Create(ElfFile elf, Stream stream, RecompilerPluginRegistry? registry = null, int maxInstructions = 48)
    {
        registry ??= RecompilerPluginRegistry.Default;
        var plugin = registry.FindSupporting(elf).FirstOrDefault(p => p.CreateDecoder() is not null);
        if (plugin?.CreateDecoder() is not { } decoder)
        {
            return null;
        }

        if (elf.Header.Entry == 0 || elf.EntryPointInExecutableRegion != true)
        {
            return new PreviewResult(plugin.DisplayName, decoder.Name, [], 0,
                "No preview: the entry point is missing or not inside an executable region.");
        }

        var memory = new StreamImageReader(stream, elf);
        var list = new List<DecodedInstruction>();
        var address = elf.Header.Entry;
        for (var i = 0; i < Math.Clamp(maxInstructions, 1, 512); i++)
        {
            var instruction = decoder.Decode(memory, elf.Header.Endianness, address);
            if (instruction is null)
            {
                break;
            }

            list.Add(instruction);
            address += 4;
        }

        var unknown = list.Count(i => !i.IsKnown);
        var note = unknown > 0
            ? $"{unknown} of {list.Count} words are outside the supported instruction subset and are shown as raw words."
            : null;
        return new PreviewResult(plugin.DisplayName, decoder.Name, list, unknown, note);
    }
}
