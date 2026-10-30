using RecompRadar.Recompilation.IR;

namespace RecompRadar.Recompilation.Execution;

/// <summary>
/// Reference backend: executes the IR directly. It exists to prove that a translation is semantically correct;
/// it is not a native code generator.
/// </summary>
public sealed class IrInterpreter : ICodeBackend
{
    public string Name => "IR reference interpreter";

    public bool IsNative => false;

    public ExecutionResult Run(GuestMachine machine, ICodeTranslator translator, IRuntimeCompatibilityLayer runtime, ExecutionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(runtime);
        limits ??= new ExecutionLimits();

        var cache = new Dictionary<ulong, IrBlock>();
        long executed = 0;
        var regs = machine.Registers;

        ExecutionResult Finish(ExecutionStatus status, int exitCode, string? message) =>
            new(status, exitCode, executed, (runtime as IOutputCapture)?.CapturedOutput ?? string.Empty, message, regs.ToArray());

        try
        {
            while (true)
            {
                if (executed >= limits.MaxGuestInstructions)
                {
                    return Finish(ExecutionStatus.StepLimitReached, 0, $"Stopped after {executed} guest instructions (limit reached).");
                }

                var pc = machine.Pc & 0xFFFFFFFF;
                if (!cache.TryGetValue(pc, out var block))
                {
                    block = translator.TranslateBlock(machine, machine.Endianness, pc);
                    cache[pc] = block;
                }

                foreach (var instruction in block.Instructions)
                {
                    Execute(machine, regs, instruction);
                }

                executed += block.GuestInstructionCount;
                if (machine.CodeWritten)
                {
                    cache.Clear();
                    machine.CodeWritten = false;
                }

                switch (block.Terminator)
                {
                    case IrJump jump:
                        machine.Pc = jump.Target;
                        break;
                    case IrBranch branch:
                        machine.Pc = regs[branch.ConditionRegister] != 0 ? branch.Taken : branch.NotTaken;
                        break;
                    case IrJumpRegister jr:
                        machine.Pc = regs[jr.Register];
                        break;
                    case IrSyscall syscall:
                    {
                        machine.Pc = syscall.Continuation;
                        var outcome = runtime.HandleSyscall(machine);
                        if (outcome.Fault is not null)
                        {
                            return Finish(ExecutionStatus.Faulted, 0, outcome.Fault);
                        }

                        if (outcome.Exit)
                        {
                            return Finish(ExecutionStatus.Exited, outcome.ExitCode, null);
                        }

                        break;
                    }

                    case IrStop stop:
                        return Finish(ExecutionStatus.Stopped, 0, stop.Reason);
                    default:
                        return Finish(ExecutionStatus.Faulted, 0, "Unknown IR terminator.");
                }
            }
        }
        catch (GuestFaultException ex)
        {
            return Finish(ExecutionStatus.Faulted, 0, ex.Message);
        }
    }

    private static uint Value(uint[] regs, IrValue v) => v.IsImmediate ? v.Value : regs[v.Value];

    private static void Execute(GuestMachine m, uint[] regs, IrInstruction i)
    {
        var a = Value(regs, i.A);
        var b = Value(regs, i.B);
        switch (i.Op)
        {
            case IrOp.Const:
            case IrOp.Move:
                regs[i.Dest] = a;
                break;
            case IrOp.Add:
                regs[i.Dest] = unchecked(a + b);
                break;
            case IrOp.Sub:
                regs[i.Dest] = unchecked(a - b);
                break;
            case IrOp.Mul:
                regs[i.Dest] = unchecked(a * b);
                break;
            case IrOp.And:
                regs[i.Dest] = a & b;
                break;
            case IrOp.Or:
                regs[i.Dest] = a | b;
                break;
            case IrOp.Xor:
                regs[i.Dest] = a ^ b;
                break;
            case IrOp.Nor:
                regs[i.Dest] = ~(a | b);
                break;
            case IrOp.Shl:
                regs[i.Dest] = a << (int)(b & 31);
                break;
            case IrOp.Shr:
                regs[i.Dest] = a >> (int)(b & 31);
                break;
            case IrOp.Sar:
                regs[i.Dest] = (uint)((int)a >> (int)(b & 31));
                break;
            case IrOp.SetEq:
                regs[i.Dest] = a == b ? 1u : 0u;
                break;
            case IrOp.SetNe:
                regs[i.Dest] = a != b ? 1u : 0u;
                break;
            case IrOp.SetLtSigned:
                regs[i.Dest] = (int)a < (int)b ? 1u : 0u;
                break;
            case IrOp.SetLtUnsigned:
                regs[i.Dest] = a < b ? 1u : 0u;
                break;
            case IrOp.LoadS8:
                regs[i.Dest] = m.Load(unchecked(a + (uint)i.Offset), 1, signExtend: true);
                break;
            case IrOp.LoadU8:
                regs[i.Dest] = m.Load(unchecked(a + (uint)i.Offset), 1, signExtend: false);
                break;
            case IrOp.LoadS16:
                regs[i.Dest] = m.Load(unchecked(a + (uint)i.Offset), 2, signExtend: true);
                break;
            case IrOp.LoadU16:
                regs[i.Dest] = m.Load(unchecked(a + (uint)i.Offset), 2, signExtend: false);
                break;
            case IrOp.Load32:
                regs[i.Dest] = m.Load(unchecked(a + (uint)i.Offset), 4, signExtend: false);
                break;
            case IrOp.Store8:
                m.Store(unchecked(a + (uint)i.Offset), 1, b);
                break;
            case IrOp.Store16:
                m.Store(unchecked(a + (uint)i.Offset), 2, b);
                break;
            case IrOp.Store32:
                m.Store(unchecked(a + (uint)i.Offset), 4, b);
                break;
            default:
                throw new GuestFaultException($"Unknown IR operation {i.Op}.");
        }

        // $zero is hard-wired; translators redirect writes elsewhere, but keep the invariant explicit.
        regs[0] = 0;
    }
}

/// <summary>Implemented by runtime layers that collect program output, so the interpreter can include it in the result.</summary>
public interface IOutputCapture
{
    string CapturedOutput { get; }
}
