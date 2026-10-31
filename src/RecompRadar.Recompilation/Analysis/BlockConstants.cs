using RecompRadar.Recompilation.IR;
using RecompRadar.Semantics;

namespace RecompRadar.Recompilation.Analysis;

/// <summary>
/// Block-local constant propagation: which registers hold a known value at the end of a basic block.
/// It is used to resolve jumps and calls through registers (for example "lui/ori then jr") and system call numbers.
/// Values that depend on memory, on registers from earlier blocks or on conditional moves are unknown.
/// </summary>
public static class BlockConstants
{
    public static IReadOnlyDictionary<int, ulong> AtEnd(IrBlock block)
    {
        var known = new Dictionary<int, ulong>();

        bool Get(IrValue value, out ulong result)
        {
            if (value.IsImmediate)
            {
                result = value.Value;
                return true;
            }

            if (value.Value == 0)
            {
                result = 0;
                return true;
            }

            return known.TryGetValue((int)value.Value, out result);
        }

        foreach (var i in block.Instructions)
        {
            if (IrInstruction.IsStore(i.Op))
            {
                continue;
            }

            ulong? value = null;
            if (!IrInstruction.IsLoad(i.Op) && Get(i.A, out var a))
            {
                if (IrInstruction.IsUnary(i.Op))
                {
                    value = i.Op switch
                    {
                        IrOp.Const or IrOp.Move => a,
                        IrOp.Sext32 => Sem.Sx((uint)a),
                        IrOp.Zext32 => (uint)a,
                        _ => null,
                    };
                }
                else if (Get(i.B, out var b))
                {
                    value = i.Op switch
                    {
                        IrOp.Add => unchecked(a + b),
                        IrOp.Sub => unchecked(a - b),
                        IrOp.Mul => unchecked(a * b),
                        IrOp.And => a & b,
                        IrOp.Or => a | b,
                        IrOp.Xor => a ^ b,
                        IrOp.Nor => ~(a | b),
                        IrOp.Shl => a << (int)(b & 63),
                        IrOp.Shr => a >> (int)(b & 63),
                        IrOp.Sar => unchecked((ulong)((long)a >> (int)(b & 63))),
                        IrOp.AddW => Sem.Sx(unchecked((uint)(a + b))),
                        IrOp.SubW => Sem.Sx(unchecked((uint)(a - b))),
                        IrOp.MulW => Sem.Sx(unchecked((uint)(a * b))),
                        IrOp.ShlW => Sem.Sx((uint)a << (int)(b & 31)),
                        IrOp.ShrW => Sem.Sx((uint)a >> (int)(b & 31)),
                        IrOp.SarW => Sem.Sx(unchecked((uint)((int)(uint)a >> (int)(b & 31)))),
                        _ => null,
                    };
                }
            }

            if (value is { } v)
            {
                known[i.Dest] = v;
            }
            else
            {
                known.Remove(i.Dest);
            }
        }

        return known;
    }
}
