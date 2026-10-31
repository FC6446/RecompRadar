using RecompRadar.Semantics;

namespace RecompRadar.Recompilation.Execution;

/// <summary>
/// The PlayStation 2 profile for the reference interpreter: kernel calls with the number in $v1 (see <see cref="Ps2Kernel"/>), serial
/// output through the EE address space, and the RecompRadar test calls when $v1 is 0. It is a small subset of the real EE kernel and
/// provides no graphics, sound, input, files or interrupts. The generated Windows programs use the very same shared implementation.
/// </summary>
public sealed class Ps2Runtime : IRuntimeCompatibilityLayer, IPcInterceptor
{
    private Ps2Kernel? _kernel;

    public string Name => "RecompRadar PS2 EE runtime (kernel subset, serial output)";

    public SyscallOutcome HandleSyscall(GuestMachine machine)
    {
        if (machine.Registers[3] == 0)
        {
            return PocRuntime.HandleTestCall(machine);
        }

        return Apply(machine, KernelFor(machine).Handle(machine.Registers, (uint)machine.Pc));
    }

    public bool TryIntercept(GuestMachine machine, ulong pc, out SyscallOutcome outcome)
    {
        if (pc != Ps2Kernel.ThreadExitAddress)
        {
            outcome = new SyscallOutcome(false);
            return false;
        }

        outcome = Apply(machine, KernelFor(machine).ThreadReturned(machine.Registers));
        return true;
    }

    private Ps2Kernel KernelFor(GuestMachine machine)
    {
        if (machine.Ps2 is not { } memory)
        {
            throw new InvalidOperationException("The PS2 runtime needs a machine created with the PS2 profile.");
        }

        return _kernel ??= new Ps2Kernel(memory.TryLoad32);
    }

    private static SyscallOutcome Apply(GuestMachine machine, Ps2Result result)
    {
        switch (result.Action)
        {
            case Ps2Action.Exit:
                return new SyscallOutcome(Exit: true, ExitCode: result.ExitCode);
            case Ps2Action.Fault:
                return new SyscallOutcome(false, Fault: result.Message);
            default:
                machine.Pc = result.NextPc;
                return new SyscallOutcome(false);
        }
    }
}
