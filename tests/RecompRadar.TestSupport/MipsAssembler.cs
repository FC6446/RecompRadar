namespace RecompRadar.TestSupport;

/// <summary>MIPS register numbers.</summary>
public static class Reg
{
    public const int Zero = 0, At = 1, V0 = 2, V1 = 3, A0 = 4, A1 = 5, A2 = 6, A3 = 7;
    public const int T0 = 8, T1 = 9, T2 = 10, T3 = 11, T4 = 12, T5 = 13, T6 = 14, T7 = 15;
    public const int S0 = 16, S1 = 17, S2 = 18, S3 = 19, S4 = 20, S5 = 21, S6 = 22, S7 = 23, T8 = 24, T9 = 25, Gp = 28, Fp = 30, Sp = 29, Ra = 31;
}

/// <summary>
/// Tiny two-pass assembler for the MIPS32 subset (integer, HI/LO, single-precision FPU) that RecompRadar translates.
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

    // ---- 64-bit instructions (MIPS III / R5900) ----
    public MipsProgram Dadd(int rd, int rs, int rt) => R(0x2C, rd, rs, rt);

    public MipsProgram Daddu(int rd, int rs, int rt) => R(0x2D, rd, rs, rt);

    public MipsProgram Dsub(int rd, int rs, int rt) => R(0x2E, rd, rs, rt);

    public MipsProgram Dsubu(int rd, int rs, int rt) => R(0x2F, rd, rs, rt);

    public MipsProgram Dsll(int rd, int rt, int sa) => R(0x38, rd, 0, rt, sa);

    public MipsProgram Dsrl(int rd, int rt, int sa) => R(0x3A, rd, 0, rt, sa);

    public MipsProgram Dsra(int rd, int rt, int sa) => R(0x3B, rd, 0, rt, sa);

    public MipsProgram Dsll32(int rd, int rt, int sa) => R(0x3C, rd, 0, rt, sa);

    public MipsProgram Dsrl32(int rd, int rt, int sa) => R(0x3E, rd, 0, rt, sa);

    public MipsProgram Dsra32(int rd, int rt, int sa) => R(0x3F, rd, 0, rt, sa);

    public MipsProgram Dsllv(int rd, int rt, int rs) => R(0x14, rd, rs, rt);

    public MipsProgram Dsrlv(int rd, int rt, int rs) => R(0x16, rd, rs, rt);

    public MipsProgram Dsrav(int rd, int rt, int rs) => R(0x17, rd, rs, rt);

    public MipsProgram Daddi(int rt, int rs, int imm) => I(0x18, rt, rs, imm);

    public MipsProgram Daddiu(int rt, int rs, int imm) => I(0x19, rt, rs, imm);

    public MipsProgram Lwu(int rt, int offset, int rs) => I(0x27, rt, rs, offset);

    public MipsProgram Ld(int rt, int offset, int rs) => I(0x37, rt, rs, offset);

    public MipsProgram Sd(int rt, int offset, int rs) => I(0x3F, rt, rs, offset);

    // Recognised by the decoder but not translated: used to test that they are refused by name.
    public MipsProgram Dmult(int rs, int rt) => R(0x1C, 0, rs, rt);

    public MipsProgram Lq(int rt, int offset, int rs) => I(0x1E, rt, rs, offset);

    public MipsProgram Sq(int rt, int offset, int rs) => I(0x1F, rt, rs, offset);

    public MipsProgram Lwl(int rt, int offset, int rs) => I(0x22, rt, rs, offset);

    public MipsProgram Ldl(int rt, int offset, int rs) => I(0x1A, rt, rs, offset);

    /// <summary>
    /// Loads a 64-bit constant. Values that are the sign extension of 32 bits take the short form; others are built from two
    /// halves with <paramref name="scratch"/> as a temporary.
    /// </summary>
    public MipsProgram Li64(int rt, ulong value, int scratch = Reg.At)
    {
        if ((long)value == (int)(uint)value)
        {
            return Li32(rt, (uint)value);
        }

        Li32(rt, (uint)(value >> 32)).Dsll32(rt, rt, 0);
        var low = (uint)value;
        if (low != 0)
        {
            Li32(scratch, low).Dsll32(scratch, scratch, 0).Dsrl32(scratch, scratch, 0).Or(rt, rt, scratch);
        }

        return this;
    }

    /// <summary>Prints a 64-bit register as two signed 32-bit integers (upper half first) with the RecompRadar test call (needs $v1 = 0 in the PS2 profile).</summary>
    public MipsProgram Print64(int reg) =>
        Dsra32(Reg.A0, reg, 0).Addiu(Reg.V0, Reg.Zero, 3).Addiu(Reg.V1, Reg.Zero, 0).Syscall()
            .Sll(Reg.A0, reg, 0).Addiu(Reg.V0, Reg.Zero, 3).Addiu(Reg.V1, Reg.Zero, 0).Syscall();

    // ---- HI/LO, conditional moves, wrapping add/sub, linking branches ----
    public MipsProgram Add(int rd, int rs, int rt) => R(0x20, rd, rs, rt);

    public MipsProgram Sub(int rd, int rs, int rt) => R(0x22, rd, rs, rt);

    public MipsProgram Addi(int rt, int rs, int imm) => I(0x08, rt, rs, imm);

    public MipsProgram Mult(int rs, int rt) => R(0x18, 0, rs, rt);

    public MipsProgram Multu(int rs, int rt) => R(0x19, 0, rs, rt);

    public MipsProgram Div(int rs, int rt) => R(0x1A, 0, rs, rt);

    public MipsProgram Divu(int rs, int rt) => R(0x1B, 0, rs, rt);

    public MipsProgram Mfhi(int rd) => R(0x10, rd, 0, 0);

    public MipsProgram Mflo(int rd) => R(0x12, rd, 0, 0);

    public MipsProgram Mthi(int rs) => R(0x11, 0, rs, 0);

    public MipsProgram Mtlo(int rs) => R(0x13, 0, rs, 0);

    public MipsProgram Movz(int rd, int rs, int rt) => R(0x0A, rd, rs, rt);

    public MipsProgram Movn(int rd, int rs, int rt) => R(0x0B, rd, rs, rt);

    public MipsProgram Break() => Word(0x0D);

    public MipsProgram Bltzal(int rs, string label) => Branch(0x01, rs, 0x10, label);

    public MipsProgram Bgezal(int rs, string label) => Branch(0x01, rs, 0x11, label);

    /// <summary>Branch-likely: valid MIPS, but not translated by RecompRadar yet (used to test the unsupported-instruction report).</summary>
    public MipsProgram Beql(int rs, int rt, string label) => Branch(0x14, rs, rt, label);

    // ---- single-precision FPU (COP1) ----
    private MipsProgram Cop1(int fmt, int ft, int fs, int fd, uint funct) =>
        Word((0x11u << 26) | ((uint)fmt << 21) | ((uint)ft << 16) | ((uint)fs << 11) | ((uint)fd << 6) | funct);

    public MipsProgram Mfc1(int rt, int fs) => Cop1(0, rt, fs, 0, 0);

    public MipsProgram Mtc1(int rt, int fs) => Cop1(4, rt, fs, 0, 0);

    public MipsProgram Lwc1(int ft, int offset, int rs) => I(0x31, ft, rs, offset);

    public MipsProgram Swc1(int ft, int offset, int rs) => I(0x39, ft, rs, offset);

    public MipsProgram AddS(int fd, int fs, int ft) => Cop1(16, ft, fs, fd, 0x00);

    public MipsProgram SubS(int fd, int fs, int ft) => Cop1(16, ft, fs, fd, 0x01);

    public MipsProgram MulS(int fd, int fs, int ft) => Cop1(16, ft, fs, fd, 0x02);

    public MipsProgram DivS(int fd, int fs, int ft) => Cop1(16, ft, fs, fd, 0x03);

    public MipsProgram SqrtS(int fd, int fs) => Cop1(16, 0, fs, fd, 0x04);

    public MipsProgram AbsS(int fd, int fs) => Cop1(16, 0, fs, fd, 0x05);

    public MipsProgram MovS(int fd, int fs) => Cop1(16, 0, fs, fd, 0x06);

    public MipsProgram NegS(int fd, int fs) => Cop1(16, 0, fs, fd, 0x07);

    public MipsProgram TruncWS(int fd, int fs) => Cop1(16, 0, fs, fd, 0x0D);

    public MipsProgram CvtWS(int fd, int fs) => Cop1(16, 0, fs, fd, 0x24);

    public MipsProgram CvtSW(int fd, int fs) => Cop1(20, 0, fs, fd, 0x20);

    public MipsProgram CEqS(int fs, int ft) => Cop1(16, ft, fs, 0, 0x32);

    public MipsProgram CLtS(int fs, int ft) => Cop1(16, ft, fs, 0, 0x34);

    public MipsProgram CLeS(int fs, int ft) => Cop1(16, ft, fs, 0, 0x36);

    public MipsProgram Bc1t(string label) => Branch(0x11, 8, 1, label);

    public MipsProgram Bc1f(string label) => Branch(0x11, 8, 0, label);

    // ---- calls into RecompRadar's PoC system call ABI (number in $v0, argument in $a0 / $f12) ----
    public MipsProgram PrintInt(int reg) => Move(Reg.A0, reg).Addiu(Reg.V0, Reg.Zero, 3).Syscall();

    /// <summary>A PlayStation 2 style kernel call: the call number goes into $v1, then <c>syscall</c>.</summary>
    public MipsProgram Ee(int number) => Addiu(Reg.V1, Reg.Zero, number).Syscall();

    /// <summary>The RecompRadar "print integer" test call as the PlayStation 2 profile expects it ($v1 must be 0).</summary>
    public MipsProgram PrintIntEe(int reg) => Move(Reg.A0, reg).Addiu(Reg.V0, Reg.Zero, 3).Addiu(Reg.V1, Reg.Zero, 0).Syscall();

    public MipsProgram PrintString(uint address) => Li32(Reg.A0, address).Addiu(Reg.V0, Reg.Zero, 5).Syscall();

    public MipsProgram PrintFloat(int fpr) => MovS(12, fpr).Addiu(Reg.V0, Reg.Zero, 4).Syscall();

    public MipsProgram ExitWith(int code) => Addiu(Reg.A0, Reg.Zero, code).Addiu(Reg.V0, Reg.Zero, 1).Syscall();

    /// <summary>The address a label gets when the program is assembled at <paramref name="baseAddress"/>.</summary>
    public ulong AddressOf(string label, ulong baseAddress) => baseAddress + ((ulong)_labels[label] * 4);

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
