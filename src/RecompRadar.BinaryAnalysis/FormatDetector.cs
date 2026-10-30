using System.Text;
using RecompRadar.Core.Model;

namespace RecompRadar.BinaryAnalysis;

/// <summary>
/// Recognises container formats from their magic numbers. Recognition is purely informational:
/// only ELF is analyzed structurally, and nothing is ever decrypted or unpacked.
/// </summary>
public static class FormatDetector
{
    public const int HeadBytesNeeded = 64;

    public static FormatDetection Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
        {
            return new FormatDetection
            {
                Id = "elf",
                DisplayName = "ELF (Executable and Linkable Format)",
                IsSupported = true,
                Explanation = "ELF signature found. The file is analyzed structurally.",
            };
        }

        if (StartsWith(head, "PS-X EXE"))
        {
            return Unsupported("ps-x-exe", "PS-X EXE (PlayStation 1 executable)",
                "The PS-X EXE signature was found. This is the native PlayStation 1 executable format, not ELF. Support for it is not implemented yet.");
        }

        if (head.Length >= 4 && head[0] == 0 && head[1] == (byte)'P' && head[2] == (byte)'B' && head[3] == (byte)'P')
        {
            return Unsupported("pbp", "PBP container (PSP)",
                "The PBP signature was found. PBP is a container that can hold program data that is often encrypted. RecompRadar does not unpack or decrypt containers.");
        }

        if (StartsWith(head, "~PSP"))
        {
            return Unsupported("psp-encrypted", "Encrypted PSP module (~PSP)",
                "The '~PSP' signature marks an encrypted PSP executable module. RecompRadar does not decrypt protected files; provide an unencrypted ELF/PRX that you are entitled to analyze.");
        }

        if (head.Length >= 4 && head[0] == (byte)'S' && head[1] == (byte)'C' && head[2] == (byte)'E' && head[3] == 0)
        {
            return Unsupported("sce-self", "SCE signed executable (SELF)",
                "The 'SCE' signature marks a signed (and usually encrypted) Sony executable container. RecompRadar does not decrypt protected files.");
        }

        if (head.Length >= 4 && head[0] == 0x4F && head[1] == 0x15 && head[2] == 0x3D && head[3] == 0x1D)
        {
            return Unsupported("sce-self-orbis", "Signed Sony executable (SELF, PS4-style header)",
                "This signature marks a signed (and usually encrypted) Sony executable container. RecompRadar does not decrypt protected files.");
        }

        if (head.Length >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z')
        {
            return Unsupported("pe", "PE/COFF (Windows executable)",
                "A Windows executable (MZ signature) was found. RecompRadar currently analyzes ELF files only.");
        }

        if (head.Length >= 4)
        {
            var magic = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
            if (magic is unchecked((int)0xFEEDFACE) or unchecked((int)0xFEEDFACF) or unchecked((int)0xCEFAEDFE) or unchecked((int)0xCFFAEDFE))
            {
                return Unsupported("mach-o", "Mach-O (Apple executable)",
                    "A Mach-O signature was found. RecompRadar currently analyzes ELF files only.");
            }

            if (magic == unchecked((int)0xCAFEBABE))
            {
                return Unsupported("mach-o-fat-or-java", "Mach-O universal binary or Java class file",
                    "The 0xCAFEBABE signature is shared by Mach-O universal binaries and Java class files. RecompRadar currently analyzes ELF files only.");
            }
        }

        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B)
        {
            return Unsupported("gzip", "gzip-compressed data", "The file is compressed. RecompRadar does not unpack archives; extract the executable first.");
        }

        if (head.Length >= 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4)
        {
            return Unsupported("zip", "ZIP archive", "The file is an archive. RecompRadar does not unpack archives; extract the executable first.");
        }

        return new FormatDetection
        {
            Id = "unknown",
            DisplayName = "Unknown format",
            IsSupported = false,
            Explanation = "No known executable signature was found. The file may be raw data, a packed or encrypted executable, or a format RecompRadar does not know.",
        };
    }

    private static FormatDetection Unsupported(string id, string name, string explanation) =>
        new() { Id = id, DisplayName = name, IsSupported = false, Explanation = explanation };

    private static bool StartsWith(ReadOnlySpan<byte> data, string ascii)
    {
        var expected = Encoding.ASCII.GetBytes(ascii);
        return data.Length >= expected.Length && data[..expected.Length].SequenceEqual(expected);
    }
}
