using RecompRadar.Recompilation.IR;

namespace RecompRadar.Recompilation.Analysis;

/// <summary>
/// Block-local constant propagation: which registers hold a known value at the end of a basic block.
/// It is used to resolve jumps and calls through registers (for example "lui/ori then jr") and system call numbers.
/// Values that depend on memory, on registers from earlier blocks or on conditional moves are unknown.
/// </summary>
public static class BlockConstants
{
    public static IReadOnlyDictionary<int, uint> AtEnd(IrBlock block)
    {
        var known = new Dictionary<int, uint>();

        bool Get(IrValue value, out uint result)
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

            uint? value = null;
            if (!IrInstruction.IsLoad(i.Op) && Get(i.A, out var a))
            {
                if (IrInstruction.IsUnary(i.Op))
                {
                    value = i.Op is IrOp.Const or IrOp.Move ? a : null;
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
                        IrOp.Shl => a << (int)(b & 31),
                        IrOp.Shr => a >> (int)(b & 31),
                        IrOp.Sar => (uint)((int)a >> (int)(b & 31)),
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
