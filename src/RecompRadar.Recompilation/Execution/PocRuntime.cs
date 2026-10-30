using System.Globalization;
using System.Text;

namespace RecompRadar.Recompilation.Execution;

/// <summary>
/// Minimal runtime layer with RecompRadar's own tiny proof-of-concept system call ABI. It is not the system interface of any
/// real console or operating system.
/// <para>Syscall number in $v0 (r2), argument in $a0 (r4): 1 = exit(code), 2 = write byte, 3 = write signed decimal number followed by a newline.</para>
/// </summary>
public sealed class PocRuntime : IRuntimeCompatibilityLayer, IOutputCapture
{
    public const uint SyscallExit = 1;
    public const uint SyscallWriteByte = 2;
    public const uint SyscallWriteInt = 3;

    private const int MaxOutputChars = 1 << 20;
    private readonly StringBuilder _output = new();

    public string Name => "RecompRadar PoC runtime (exit / write byte / write int)";

    public string CapturedOutput => _output.ToString();

    public SyscallOutcome HandleSyscall(GuestMachine machine)
    {
        var number = machine.Registers[2];
        var argument = machine.Registers[4];
        switch (number)
        {
            case SyscallExit:
                return new SyscallOutcome(Exit: true, ExitCode: (int)argument);
            case SyscallWriteByte:
                Append(((char)(argument & 0xFF)).ToString());
                machine.Registers[2] = 0;
                return new SyscallOutcome(false);
            case SyscallWriteInt:
                Append(((int)argument).ToString(CultureInfo.InvariantCulture) + "\n");
                machine.Registers[2] = 0;
                return new SyscallOutcome(false);
            default:
                return new SyscallOutcome(false, Fault: $"Unimplemented system call {number} (the PoC runtime only knows 1, 2 and 3).");
        }
    }

    private void Append(string text)
    {
        if (_output.Length + text.Length <= MaxOutputChars)
        {
            _output.Append(text);
        }
    }
}
