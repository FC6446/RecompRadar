using System.Text;
using RecompRadar.Recompilation.IR;
using RecompRadar.Semantics;

namespace RecompRadar.Recompilation.Execution;

/// <summary>
/// Minimal runtime layer with RecompRadar's own tiny proof-of-concept system call ABI (see <see cref="PocSystemCalls"/>).
/// It is not the system interface of any real console or operating system. The generated Windows programs use the very same
/// shared implementation, so both produce identical output.
/// </summary>
public sealed class PocRuntime : IRuntimeCompatibilityLayer, IOutputCapture
{
    private readonly StringBuilder _output = new();

    public string Name => "RecompRadar PoC runtime (exit, write byte/int/float/string)";

    public string CapturedOutput => _output.ToString();

    public SyscallOutcome HandleSyscall(GuestMachine machine)
    {
        var result = PocSystemCalls.Handle(
            machine.Registers[2],
            machine.Registers[4],
            machine.Registers[Regs.Fpr(12)],
            address =>
            {
                Span<byte> one = stackalloc byte[1];
                return machine.TryRead(address, one) ? one[0] : -1;
            },
            _output);

        switch (result.Action)
        {
            case SyscallAction.Exit:
                return new SyscallOutcome(Exit: true, ExitCode: result.ExitCode);
            case SyscallAction.Fault:
                return new SyscallOutcome(false, Fault: result.Message);
            default:
                machine.Registers[2] = result.ReturnValue;
                return new SyscallOutcome(false);
        }
    }
}
