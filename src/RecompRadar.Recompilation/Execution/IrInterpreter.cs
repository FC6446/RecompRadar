using RecompRadar.Recompilation.IR;
using RecompRadar.Semantics;

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
            new(status, exitCode, executed, machine.Output.ToString(), message, regs.ToArray());

        try
        {
            while (true)
            {
                if (executed >= limits.MaxGuestInstructions)
                {
                    return Finish(ExecutionStatus.StepLimitReached, 0, $"Stopped after {executed} guest instructions (limit reached).");
                }

                var pc = machine.Pc & 0xFFFFFFFF;
                if (runtime is IPcInterceptor interceptor && interceptor.TryIntercept(machine, pc, out var intercepted))
                {
                    if (intercepted.Fault is not null)
                    {
                        return Finish(ExecutionStatus.Faulted, 0, intercepted.Fault);
                    }

                    if (intercepted.Exit)
                    {
                        return Finish(ExecutionStatus.Exited, intercepted.ExitCode, null);
                    }

                    continue;
                }

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
                        machine.Pc = (uint)regs[jr.Register];
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

    private static ulong Value(ulong[] regs, IrValue v) => v.IsImmediate ? v.Value : regs[v.Value];

    private static void Execute(GuestMachine m, ulong[] regs, IrInstruction i)
    {
        var a = Value(regs, i.A);
        var b = Value(regs, i.B);

        // Memory addresses are 32 bits wide: the low half of base + offset.
        var address = unchecked((uint)(a + (ulong)(long)i.Offset));
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
                regs[i.Dest] = a << (int)(b & 63);
                break;
            case IrOp.Shr:
                regs[i.Dest] = a >> (int)(b & 63);
                break;
            case IrOp.Sar:
                regs[i.Dest] = unchecked((ulong)((long)a >> (int)(b & 63)));
                break;
            case IrOp.AddW:
                regs[i.Dest] = Sem.Sx(unchecked((uint)(a + b)));
                break;
            case IrOp.SubW:
                regs[i.Dest] = Sem.Sx(unchecked((uint)(a - b)));
                break;
            case IrOp.MulW:
                regs[i.Dest] = Sem.Sx(unchecked((uint)(a * b)));
                break;
            case IrOp.ShlW:
                regs[i.Dest] = Sem.Sx((uint)a << (int)(b & 31));
                break;
            case IrOp.ShrW:
                regs[i.Dest] = Sem.Sx((uint)a >> (int)(b & 31));
                break;
            case IrOp.SarW:
                regs[i.Dest] = Sem.Sx(unchecked((uint)((int)(uint)a >> (int)(b & 31))));
                break;
            case IrOp.Sext32:
                regs[i.Dest] = Sem.Sx((uint)a);
                break;
            case IrOp.Zext32:
                regs[i.Dest] = (uint)a;
                break;
            case IrOp.SetEq:
                regs[i.Dest] = a == b ? 1UL : 0UL;
                break;
            case IrOp.SetNe:
                regs[i.Dest] = a != b ? 1UL : 0UL;
                break;
            case IrOp.SetLtSigned:
                regs[i.Dest] = (long)a < (long)b ? 1UL : 0UL;
                break;
            case IrOp.SetLtUnsigned:
                regs[i.Dest] = a < b ? 1UL : 0UL;
                break;
            case IrOp.LoadS8:
                regs[i.Dest] = Sem.Sx(m.Load(address, 1, signExtend: true));
                break;
            case IrOp.LoadU8:
                regs[i.Dest] = m.Load(address, 1, signExtend: false);
                break;
            case IrOp.LoadS16:
                regs[i.Dest] = Sem.Sx(m.Load(address, 2, signExtend: true));
                break;
            case IrOp.LoadU16:
                regs[i.Dest] = m.Load(address, 2, signExtend: false);
                break;
            case IrOp.LoadS32:
                regs[i.Dest] = Sem.Sx(m.Load(address, 4, signExtend: false));
                break;
            case IrOp.LoadU32:
                regs[i.Dest] = m.Load(address, 4, signExtend: false);
                break;
            case IrOp.Load64:
                regs[i.Dest] = m.Load64(address);
                break;
            case IrOp.Store8:
                m.Store(address, 1, (uint)b);
                break;
            case IrOp.Store16:
                m.Store(address, 2, (uint)b);
                break;
            case IrOp.Store32:
                m.Store(address, 4, (uint)b);
                break;
            case IrOp.Store64:
                m.Store64(address, b);
                break;
            case IrOp.MulHiS:
                regs[i.Dest] = Sem.MulHiS(a, b);
                break;
            case IrOp.MulHiU:
                regs[i.Dest] = Sem.MulHiU(a, b);
                break;
            case IrOp.DivS:
                regs[i.Dest] = Sem.DivS(a, b);
                break;
            case IrOp.RemS:
                regs[i.Dest] = Sem.RemS(a, b);
                break;
            case IrOp.DivU:
                regs[i.Dest] = Sem.DivU(a, b);
                break;
            case IrOp.RemU:
                regs[i.Dest] = Sem.RemU(a, b);
                break;
            case IrOp.MoveIfZero:
                if (b == 0)
                {
                    regs[i.Dest] = a;
                }

                break;
            case IrOp.MoveIfNonZero:
                if (b != 0)
                {
                    regs[i.Dest] = a;
                }

                break;
            case IrOp.FAdd:
                regs[i.Dest] = Sem.FAdd(a, b);
                break;
            case IrOp.FSub:
                regs[i.Dest] = Sem.FSub(a, b);
                break;
            case IrOp.FMul:
                regs[i.Dest] = Sem.FMul(a, b);
                break;
            case IrOp.FDiv:
                regs[i.Dest] = Sem.FDiv(a, b);
                break;
            case IrOp.FSqrt:
                regs[i.Dest] = Sem.FSqrt(a);
                break;
            case IrOp.FAbs:
                regs[i.Dest] = Sem.FAbs(a);
                break;
            case IrOp.FNeg:
                regs[i.Dest] = Sem.FNeg(a);
                break;
            case IrOp.CvtSW:
                regs[i.Dest] = Sem.CvtSW(a);
                break;
            case IrOp.CvtWS:
                regs[i.Dest] = Sem.CvtWS(a);
                break;
            case IrOp.TruncWS:
                regs[i.Dest] = Sem.TruncWS(a);
                break;
            case IrOp.FEq:
                regs[i.Dest] = Sem.FEq(a, b);
                break;
            case IrOp.FLt:
                regs[i.Dest] = Sem.FLt(a, b);
                break;
            case IrOp.FLe:
                regs[i.Dest] = Sem.FLe(a, b);
                break;
            default:
                throw new GuestFaultException($"Unknown IR operation {i.Op}.");
        }

        // $zero is hard-wired; translators redirect writes elsewhere, but keep the invariant explicit.
        regs[0] = 0;
    }
}
