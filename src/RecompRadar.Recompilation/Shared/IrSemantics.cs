// This file is compiled into RecompRadar.Recompilation AND copied verbatim into every program the recompiler builds,
// so the reference interpreter and the generated Windows executable share exactly one definition of these operations.
// Keep it free of dependencies on the rest of RecompRadar.
using System.Globalization;
using System.Text;

namespace RecompRadar.Semantics;

/// <summary>Arithmetic of the IR operations that are more than a one-line C# expression.</summary>
public static class Sem
{
    // ---- integer multiply / divide (HI/LO) ----

    public static uint MulHiS(uint a, uint b) => (uint)(((long)(int)a * (int)b) >> 32);

    public static uint MulHiU(uint a, uint b) => (uint)(((ulong)a * b) >> 32);

    // Division by zero and INT_MIN / -1 leave HI/LO "undefined" in the MIPS manual. These are the values common hardware
    // produces, chosen once here so that every backend agrees.
    public static uint DivS(uint a, uint b)
    {
        int x = (int)a, y = (int)b;
        if (y == 0)
        {
            return x < 0 ? 1u : 0xFFFFFFFFu;
        }

        return x == int.MinValue && y == -1 ? 0x80000000u : (uint)(x / y);
    }

    public static uint RemS(uint a, uint b)
    {
        int x = (int)a, y = (int)b;
        if (y == 0)
        {
            return a;
        }

        return x == int.MinValue && y == -1 ? 0u : (uint)(x % y);
    }

    public static uint DivU(uint a, uint b) => b == 0 ? 0xFFFFFFFFu : a / b;

    public static uint RemU(uint a, uint b) => b == 0 ? a : a % b;

    // ---- single-precision floating point; registers hold the raw IEEE-754 bits ----
    // The PS2's R5900 FPU is not fully IEEE-754 (no denormals, no NaN/Infinity). Standard IEEE-754 single precision is
    // used here; this is a documented deviation.

    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    private static uint B(float value) => BitConverter.SingleToUInt32Bits(value);

    public static uint FAdd(uint a, uint b) => B(F(a) + F(b));

    public static uint FSub(uint a, uint b) => B(F(a) - F(b));

    public static uint FMul(uint a, uint b) => B(F(a) * F(b));

    public static uint FDiv(uint a, uint b) => B(F(a) / F(b));

    public static uint FSqrt(uint a) => B(MathF.Sqrt(F(a)));

    public static uint FAbs(uint a) => a & 0x7FFFFFFFu;

    public static uint FNeg(uint a) => a ^ 0x80000000u;

    public static uint CvtSW(uint a) => B((float)(int)a);

    public static uint CvtWS(uint a) => ToInt32(MathF.Round(F(a), MidpointRounding.ToEven));

    public static uint TruncWS(uint a) => ToInt32(MathF.Truncate(F(a)));

    // MIPS returns the largest representable value when the result does not fit or the input is NaN.
    private static uint ToInt32(float value)
    {
        if (float.IsNaN(value) || value >= 2147483648f)
        {
            return 0x7FFFFFFFu;
        }

        return value < -2147483648f ? 0x80000000u : (uint)(int)value;
    }

    public static uint FEq(uint a, uint b) => F(a) == F(b) ? 1u : 0u;

    public static uint FLt(uint a, uint b) => F(a) < F(b) ? 1u : 0u;

    public static uint FLe(uint a, uint b) => F(a) <= F(b) ? 1u : 0u;

    /// <summary>Text a floating-point value gets when a program prints it through the proof-of-concept system call.</summary>
    public static string FormatFloat(uint bits) => F(bits).ToString("R", CultureInfo.InvariantCulture);
}

public enum SyscallAction
{
    Continue,
    Exit,
    Fault,
}

public readonly record struct SyscallResult(SyscallAction Action, uint ReturnValue = 0, int ExitCode = 0, string? Message = null);

/// <summary>
/// RecompRadar's own tiny system call ABI for self-made test programs (it is not any console's system interface).
/// Number in $v0, first argument in $a0 (float argument in $f12):
/// 1 = exit(code), 2 = write byte, 3 = write signed integer + newline, 4 = write float + newline, 5 = write NUL-terminated string at $a0.
/// </summary>
public static class PocSystemCalls
{
    public const uint Exit = 1;
    public const uint WriteByte = 2;
    public const uint WriteInt = 3;
    public const uint WriteFloat = 4;
    public const uint WriteString = 5;
    public const int MaxStringLength = 4096;
    public const int MaxOutputChars = 1 << 20;

    /// <param name="readByte">Reads one guest byte; returns -1 when the address is not mapped.</param>
    public static SyscallResult Handle(uint number, uint a0, uint f12Bits, Func<uint, int> readByte, StringBuilder output)
    {
        void Append(string text)
        {
            if (output.Length + text.Length <= MaxOutputChars)
            {
                output.Append(text);
            }
        }

        switch (number)
        {
            case Exit:
                return new SyscallResult(SyscallAction.Exit, ExitCode: (int)a0);
            case WriteByte:
                Append(((char)(a0 & 0xFF)).ToString());
                return new SyscallResult(SyscallAction.Continue);
            case WriteInt:
                Append(((int)a0).ToString(CultureInfo.InvariantCulture) + "\n");
                return new SyscallResult(SyscallAction.Continue);
            case WriteFloat:
                Append(Sem.FormatFloat(f12Bits) + "\n");
                return new SyscallResult(SyscallAction.Continue);
            case WriteString:
            {
                var sb = new StringBuilder();
                for (var i = 0; i < MaxStringLength; i++)
                {
                    var value = readByte(unchecked(a0 + (uint)i));
                    if (value < 0)
                    {
                        return new SyscallResult(SyscallAction.Fault, Message: $"System call 5 read unmapped memory at 0x{unchecked(a0 + (uint)i):X8}.");
                    }

                    if (value == 0)
                    {
                        break;
                    }

                    sb.Append((char)value);
                }

                Append(sb.ToString());
                return new SyscallResult(SyscallAction.Continue);
            }

            default:
                return new SyscallResult(SyscallAction.Fault, Message: $"Unimplemented system call {number} (the PoC runtime only knows 1 to 5).");
        }
    }
}
