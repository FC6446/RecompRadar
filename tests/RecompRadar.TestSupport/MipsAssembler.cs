namespace RecompRadar.TestSupport;

/// <summary>MIPS register numbers.</summary>
public static class Reg
{
    public const int Zero = 0, At = 1, V0 = 2, V1 = 3, A0 = 4, A1 = 5, A2 = 6, A3 = 7;
    public const int T0 = 8, T1 = 9, T2 = 10, T3 = 11, T4 = 12, T5 = 13, T6 = 14, T7 = 15;
    public const int S0 = 16, S1 = 17, Sp = 29, Ra = 31;
}

/// <summary>
/// Tiny two-pass assembler for the MIPS32 integer subset used by the proof of concept.
/// It exists only to create self-made test programs; the programs it produces contain no third-party code.
/// </summary>
public sealed class MipsProgram
{
    private readonly List<Func<ulong, IReadOnlyDictionary<string, ulong>, uint>> _items = [];
    private readonly Dictionary<string, int> _labels = [];

    public int Count => _items.Count;

    public MipsProgram Label(string name)
    {
        _labels[name] = _items.Count;
        return this;
    }

    public MipsProgram Word(uint word)
    {
        _items.Add((_, _) => word);
        return this;
    }

    private MipsProgram R(uint funct, int rd, int rs, int rt, int sa = 0) =>
        Word(((uint)rs << 21) | ((uint)rt << 16) | ((uint)rd << 11) | ((uint)sa << 6) | funct);

    private MipsProgram I(uint op, int rt, int rs, int imm) =>
        Word((op << 26) | ((uint)rs << 21) | ((uint)rt << 16) | (ushort)imm);

    private MipsProgram Branch(uint op, int rs, int rt, string label)
    {
        _items.Add((pc, labels) =>
        {
            var offset = ((long)labels[label] - (long)(pc + 4)) / 4;
            return (op << 26) | ((uint)rs << 21) | ((uint)rt << 16) | (ushort)(short)offset;
        });
        return this;
    }

    private MipsProgram Jump(uint op, string label)
    {
        _items.Add((_, labels) => (op << 26) | (uint)((labels[label] >> 2) & 0x03FFFFFF));
        return this;
    }

    public MipsProgram Nop() => Word(0);

    public MipsProgram Sll(int rd, int rt, int sa) => R(0x00, rd, 0, rt, sa);

    public MipsProgram Srl(int rd, int rt, int sa) => R(0x02, rd, 0, rt, sa);

    public MipsProgram Sra(int rd, int rt, int sa) => R(0x03, rd, 0, rt, sa);

    public MipsProgram Jr(int rs) => R(0x08, 0, rs, 0);

    public MipsProgram Jalr(int rd, int rs) => R(0x09, rd, rs, 0);

    public MipsProgram Syscall() => Word(0x0C);

    public MipsProgram Addu(int rd, int rs, int rt) => R(0x21, rd, rs, rt);

    public MipsProgram Subu(int rd, int rs, int rt) => R(0x23, rd, rs, rt);

    public MipsProgram And(int rd, int rs, int rt) => R(0x24, rd, rs, rt);

    public MipsProgram Or(int rd, int rs, int rt) => R(0x25, rd, rs, rt);

    public MipsProgram Xor(int rd, int rs, int rt) => R(0x26, rd, rs, rt);

    public MipsProgram Nor(int rd, int rs, int rt) => R(0x27, rd, rs, rt);

    public MipsProgram Slt(int rd, int rs, int rt) => R(0x2A, rd, rs, rt);

    public MipsProgram Sltu(int rd, int rs, int rt) => R(0x2B, rd, rs, rt);

    public MipsProgram Mul(int rd, int rs, int rt) => Word((0x1Cu << 26) | ((uint)rs << 21) | ((uint)rt << 16) | ((uint)rd << 11) | 0x02);

    public MipsProgram Move(int rd, int rs) => Addu(rd, rs, Reg.Zero);

    public MipsProgram Addiu(int rt, int rs, int imm) => I(0x09, rt, rs, imm);

    public MipsProgram Slti(int rt, int rs, int imm) => I(0x0A, rt, rs, imm);

    public MipsProgram Sltiu(int rt, int rs, int imm) => I(0x0B, rt, rs, imm);

    public MipsProgram Andi(int rt, int rs, int imm) => I(0x0C, rt, rs, imm);

    public MipsProgram Ori(int rt, int rs, int imm) => I(0x0D, rt, rs, imm);

    public MipsProgram Xori(int rt, int rs, int imm) => I(0x0E, rt, rs, imm);

    public MipsProgram Lui(int rt, int imm) => I(0x0F, rt, 0, imm);

    public MipsProgram Lb(int rt, int offset, int rs) => I(0x20, rt, rs, offset);

    public MipsProgram Lh(int rt, int offset, int rs) => I(0x21, rt, rs, offset);

    public MipsProgram Lw(int rt, int offset, int rs) => I(0x23, rt, rs, offset);

    public MipsProgram Lbu(int rt, int offset, int rs) => I(0x24, rt, rs, offset);

    public MipsProgram Lhu(int rt, int offset, int rs) => I(0x25, rt, rs, offset);

    public MipsProgram Sb(int rt, int offset, int rs) => I(0x28, rt, rs, offset);

    public MipsProgram Sh(int rt, int offset, int rs) => I(0x29, rt, rs, offset);

    public MipsProgram Sw(int rt, int offset, int rs) => I(0x2B, rt, rs, offset);

    public MipsProgram Beq(int rs, int rt, string label) => Branch(0x04, rs, rt, label);

    public MipsProgram Bne(int rs, int rt, string label) => Branch(0x05, rs, rt, label);

    public MipsProgram Blez(int rs, string label) => Branch(0x06, rs, 0, label);

    public MipsProgram Bgtz(int rs, string label) => Branch(0x07, rs, 0, label);

    public MipsProgram Bltz(int rs, string label) => Branch(0x01, rs, 0, label);

    public MipsProgram Bgez(int rs, string label) => Branch(0x01, rs, 1, label);

    public MipsProgram J(string label) => Jump(0x02, label);

    public MipsProgram Jal(string label) => Jump(0x03, label);

    /// <summary>Loads a 32-bit constant with lui + ori (always two instructions).</summary>
    public MipsProgram Li32(int rt, uint value) => Lui(rt, (int)(value >> 16)).Ori(rt, rt, (int)(value & 0xFFFF));

    public uint[] Assemble(ulong baseAddress)
    {
        var addresses = _labels.ToDictionary(p => p.Key, p => baseAddress + ((ulong)p.Value * 4));
        var words = new uint[_items.Count];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = _items[i](baseAddress + ((ulong)i * 4), addresses);
        }

        return words;
    }

    public byte[] ToBytes(ulong baseAddress, bool bigEndian)
    {
        var words = Assemble(baseAddress);
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
        {
            for (var b = 0; b < 4; b++)
            {
                var shift = bigEndian ? (3 - b) * 8 : b * 8;
                bytes[(i * 4) + b] = (byte)(words[i] >> shift);
            }
        }

        return bytes;
    }
}
