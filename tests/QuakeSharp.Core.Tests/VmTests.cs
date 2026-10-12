using QuakeSharp.Core.Common;
using QuakeSharp.Core.Platform;
using QuakeSharp.Core.Progs;
using static QuakeSharp.Core.Progs.PrComp;

namespace QuakeSharp.Core.Tests;

/// <summary>
/// pr_exec.c's interpreter on small progs built here: arithmetic as single-precision floats, comparisons, branches,
/// calls with parameters and locals, builtins, entity fields through OP_ADDRESS/OP_STOREP/OP_LOAD, OP_STATE and the
/// truth of strings and of -0.0 (OP_IF tests the bits).
/// </summary>
public class VmTests
{
    private const int EntityFields = EntVars.Slots + 2;

    // globals: 0-27 reserved, then globalvars_t, then the test's own from G0
    private const int G0 = GlobalVars.Slots;

    private sealed class Progs
    {
        public readonly List<dstatement_t> Statements = [new()];
        public readonly List<dfunction_t> Functions = [new()];
        public readonly int[] Globals = new int[G0 + 64];
        public string Strings = "\0";

        public void Op(int op, int a = 0, int b = 0, int c = 0) =>
            Statements.Add(new dstatement_t { op = (ushort)op, a = (short)a, b = (short)b, c = (short)c });

        // a function starting at the next statement: its number
        public int Function(int parm_start = 0, int locals = 0, params byte[] parms)
        {
            var f = new dfunction_t
            {
                first_statement = Statements.Count,
                parm_start = parm_start,
                locals = locals,
                numparms = parms.Length,
                index = Functions.Count,
            };
            parms.CopyTo(f.parm_size, 0);
            Functions.Add(f);
            return f.index;
        }

        public int Builtin(int number)
        {
            Functions.Add(new dfunction_t { first_statement = -number, index = Functions.Count });
            return Functions.Count - 1;
        }

        public int String(string s)
        {
            int ofs = Strings.Length;
            Strings += s + "\0";
            return ofs;
        }

        public void Float(int g, float f) => Globals[g] = BitConverter.SingleToInt32Bits(f);

        public float F(QuakeEngine e, int g) => BitConverter.Int32BitsToSingle(e.pr_globals[g]);
    }

    private static QuakeEngine Load(Progs p)
    {
        var e = new QuakeEngine(new QuakeParms(".", new CommandLine(["quake"])), new VirtualSystem());
        e.progs = new dprograms_t
        {
            version = PROG_VERSION,
            numstatements = p.Statements.Count,
            numfunctions = p.Functions.Count,
            numglobals = p.Globals.Length,
            entityfields = EntityFields,
        };
        e.pr_statements = [.. p.Statements];
        e.pr_functions = [.. p.Functions];
        e.pr_globals = (int[])p.Globals.Clone();
        e.pr_global_struct = new GlobalVars(e.pr_globals, 0);
        e.pr_edict_size = (EntityFields * 4) + Edict.HeaderSize;
        e.PR_SetStringTable(System.Text.Encoding.Latin1.GetBytes(p.Strings));

        e.sv.max_edicts = 4;
        e.sv.num_edicts = 4;
        e.sv.edictmem = new int[e.sv.max_edicts * e.pr_edict_size / 4];
        e.sv.edicts = [.. Enumerable.Range(0, 4).Select(n => new Edict(n, e.sv.edictmem, ((n * e.pr_edict_size) + Edict.HeaderSize) / 4))];
        return e;
    }

    [Fact]
    public void FloatArithmeticIsSinglePrecision()
    {
        var p = new Progs();
        p.Float(G0, 0.1f);
        p.Float(G0 + 1, 0.2f);
        int f = p.Function();
        p.Op(OP_ADD_F, G0, G0 + 1, G0 + 2);
        p.Op(OP_MUL_F, G0, G0 + 1, G0 + 3);
        p.Op(OP_DIV_F, G0, G0 + 1, G0 + 4);
        p.Op(OP_SUB_F, G0, G0 + 1, G0 + 5);
        p.Op(OP_DONE);

        QuakeEngine e = Load(p);
        e.PR_ExecuteProgram(f);

        Assert.Equal(0.1f + 0.2f, p.F(e, G0 + 2));
        Assert.Equal(0.1f * 0.2f, p.F(e, G0 + 3));
        Assert.Equal(0.1f / 0.2f, p.F(e, G0 + 4));
        Assert.Equal(0.1f - 0.2f, p.F(e, G0 + 5));
    }

    [Fact]
    public void VectorOperations()
    {
        var p = new Progs();
        for (int i = 0; i < 3; i++)
        {
            p.Float(G0 + i, i + 1);
            p.Float(G0 + 3 + i, 10 * (i + 1));
        }

        p.Float(G0 + 6, 0.5f);
        int f = p.Function();
        p.Op(OP_ADD_V, G0, G0 + 3, G0 + 10);
        p.Op(OP_MUL_V, G0, G0 + 3, G0 + 13);
        p.Op(OP_MUL_FV, G0 + 6, G0, G0 + 14);
        p.Op(OP_MUL_VF, G0, G0 + 6, G0 + 17);
        p.Op(OP_EQ_V, G0, G0, G0 + 20);
        p.Op(OP_NE_V, G0, G0 + 3, G0 + 21);
        p.Op(OP_NOT_V, G0, 0, G0 + 22);
        p.Op(OP_DONE);

        QuakeEngine e = Load(p);
        e.PR_ExecuteProgram(f);

        Assert.Equal([11f, 22f, 33f], [p.F(e, G0 + 10), p.F(e, G0 + 11), p.F(e, G0 + 12)]);
        Assert.Equal(140f, p.F(e, G0 + 13));
        Assert.Equal([0.5f, 1f, 1.5f], [p.F(e, G0 + 14), p.F(e, G0 + 15), p.F(e, G0 + 16)]);
        Assert.Equal([0.5f, 1f, 1.5f], [p.F(e, G0 + 17), p.F(e, G0 + 18), p.F(e, G0 + 19)]);
        Assert.Equal(1f, p.F(e, G0 + 20));
        Assert.Equal(1f, p.F(e, G0 + 21));
        Assert.Equal(0f, p.F(e, G0 + 22));
    }

    [Fact]
    public void BitOperationsTruncateAsX86()
    {
        var p = new Progs();
        p.Float(G0, 7.9f);
        p.Float(G0 + 1, 12f);
        p.Float(G0 + 2, 3e9f); // out of int's range: cvttss2si gives 0x80000000
        int f = p.Function();
        p.Op(OP_BITAND, G0, G0 + 1, G0 + 3);
        p.Op(OP_BITOR, G0, G0 + 1, G0 + 4);
        p.Op(OP_BITOR, G0 + 2, G0 + 1, G0 + 5);
        p.Op(OP_DONE);

        QuakeEngine e = Load(p);
        e.PR_ExecuteProgram(f);

        Assert.Equal(4f, p.F(e, G0 + 3));
        Assert.Equal(15f, p.F(e, G0 + 4));
        Assert.Equal(unchecked((int)0x8000000c), p.F(e, G0 + 5));
    }

    [Fact]
    public void IfTestsTheBitsSoMinusZeroIsTrue()
    {
        var p = new Progs();
        p.Float(G0, -0.0f);
        p.Float(G0 + 1, 1);
        int f = p.Function();
        p.Op(OP_IF, G0, 2); // taken: skips the next statement
        p.Op(OP_STORE_F, G0 + 1, G0 + 2);
        p.Op(OP_NOT_F, G0, 0, G0 + 3); // !-0.0f is true
        p.Op(OP_IFNOT, G0, 2); // not taken
        p.Op(OP_STORE_F, G0 + 1, G0 + 4);
        p.Op(OP_DONE);

        QuakeEngine e = Load(p);
        e.PR_ExecuteProgram(f);

        Assert.Equal(0f, p.F(e, G0 + 2));
        Assert.Equal(1f, p.F(e, G0 + 3));
        Assert.Equal(1f, p.F(e, G0 + 4));
    }

    [Fact]
    public void ComparisonsAndLogic()
    {
        var p = new Progs();
        p.Float(G0, 1);
        p.Float(G0 + 1, 2);
        p.Float(G0 + 2, float.NaN);
        int f = p.Function();
        p.Op(OP_LT, G0, G0 + 1, G0 + 10);
        p.Op(OP_GE, G0, G0 + 1, G0 + 11);
        p.Op(OP_EQ_F, G0 + 2, G0 + 2, G0 + 12); // NaN == NaN is false
        p.Op(OP_AND, G0, G0 + 2, G0 + 13); // NaN is true
        p.Op(OP_OR, G0 + 3, G0 + 3, G0 + 14);
        p.Op(OP_NOT_F, G0 + 2, 0, G0 + 15);
        p.Op(OP_DONE);

        QuakeEngine e = Load(p);
        e.PR_ExecuteProgram(f);

        Assert.Equal([1f, 0f, 0f, 1f, 0f, 0f], Enumerable.Range(G0 + 10, 6).Select(g => p.F(e, g)));
    }

    [Fact]
    public void StringsCompareByContents()
    {
        var p = new Progs();
        int f = p.Function();
        p.Globals[G0] = p.String("abc");
        p.Globals[G0 + 1] = p.String("abc");
        p.Globals[G0 + 2] = p.String("");
        p.Globals[G0 + 3] = p.String("abd");
        p.Op(OP_EQ_S, G0, G0 + 1, G0 + 10);
        p.Op(OP_NOT_S, G0 + 2, 0, G0 + 11); // empty: true
        p.Op(OP_NOT_S, G0 + 4, 0, G0 + 12); // offset 0: true
        p.Op(OP_NOT_S, G0, 0, G0 + 13);
        p.Op(OP_NE_S, G0, G0 + 3, G0 + 14); // strcmp's -1
        p.Op(OP_DONE);

        QuakeEngine e = Load(p);
        e.PR_ExecuteProgram(f);

        Assert.Equal([1f, 1f, 1f, 0f, -1f], Enumerable.Range(G0 + 10, 5).Select(g => p.F(e, g)));
    }

    [Fact]
    public void CallsCopyParametersAndRestoreLocals()
    {
        // square(x) = x * x, with its parameter and a local at G0 + 20; the caller's values there survive the call
        var p = new Progs();
        p.Float(G0 + 20, 99);
        p.Float(G0 + 21, 98);
        p.Float(G0, 3);
        int main = p.Function();
        p.Op(OP_STORE_F, G0, OFS_PARM0);
        int callAt = p.Statements.Count;
        p.Op(OP_CALL1, G0 + 30);
        p.Op(OP_STORE_F, OFS_RETURN, G0 + 1);
        p.Op(OP_DONE);
        int square = p.Function(G0 + 20, 2, 1);
        p.Op(OP_MUL_F, G0 + 20, G0 + 20, G0 + 21);
        p.Op(OP_RETURN, G0 + 21);
        p.Globals[G0 + 30] = square;

        QuakeEngine e = Load(p);
        e.PR_ExecuteProgram(main);

        Assert.Equal(9f, p.F(e, G0 + 1));
        Assert.Equal(99f, p.F(e, G0 + 20));
        Assert.Equal(98f, p.F(e, G0 + 21));
        Assert.Equal(0, e.pr_depth);
        Assert.True(callAt > 0);
    }

    [Fact]
    public void BuiltinsAreCalledWithTheirArgumentCount()
    {
        var p = new Progs();
        int main = p.Function();
        p.Op(OP_CALL2, G0);
        p.Op(OP_DONE);
        p.Globals[G0] = p.Builtin(1);

        QuakeEngine e = Load(p);
        int calls = 0, argc = -1;
        e.pr_builtins = [() => { }, () => { calls++; argc = e.pr_argc; }];
        e.pr_numbuiltins = 2;
        e.PR_ExecuteProgram(main);

        Assert.Equal(1, calls);
        Assert.Equal(2, argc);
    }

    [Fact]
    public void FieldsThroughAddressesAndLoads()
    {
        // self.health = 50; then a load of it back, and of origin as a vector
        var p = new Progs();
        p.Float(G0 + 1, 50);
        int main = p.Function();
        p.Op(OP_ADDRESS, G0, G0 + 2, G0 + 3); // &ent.health
        p.Op(OP_STOREP_F, G0 + 1, G0 + 3);
        p.Op(OP_LOAD_F, G0, G0 + 2, G0 + 4);
        p.Op(OP_LOAD_V, G0, G0 + 5, G0 + 6);
        p.Op(OP_DONE);

        QuakeEngine e = Load(p);
        Edict ent = e.sv.edicts[2];
        e.pr_globals[G0] = e.EDICT_TO_PROG(ent);
        e.pr_globals[G0 + 2] = HealthSlot(e, ent);
        e.pr_globals[G0 + 5] = OriginSlot(e, ent);
        ent.v.origin = new Vec3(1, 2, 3);
        e.PR_ExecuteProgram(main);

        Assert.Equal(50f, ent.v.health);
        Assert.Equal(50f, p.F(e, G0 + 4));
        Assert.Equal([1f, 2f, 3f], [p.F(e, G0 + 6), p.F(e, G0 + 7), p.F(e, G0 + 8)]);
        Assert.Equal(e.EDICT_TO_PROG(ent) + Edict.HeaderSize + (HealthSlot(e, ent) * 4), e.pr_globals[G0 + 3]);
    }

    // a field's slot from where its ref points in the edicts' memory
    private static int HealthSlot(QuakeEngine e, Edict ent) => Slot(e, ent, ref ent.v.health);

    private static int OriginSlot(QuakeEngine e, Edict ent) => Slot(e, ent, ref ent.v.origin[0]);

    private static int Slot(QuakeEngine e, Edict ent, ref float field)
    {
        for (int i = 0; i < EntityFields; i++)
        {
            if (System.Runtime.CompilerServices.Unsafe.AreSame(ref field, ref e.E_FLOAT(ent, i)))
            {
                return i;
            }
        }

        throw new InvalidOperationException("not a field");
    }

    [Fact]
    public void StateSetsFrameThinkAndNextthink()
    {
        var p = new Progs();
        p.Float(G0, 7);
        int main = p.Function();
        p.Op(OP_STATE, G0, G0 + 1);
        p.Op(OP_DONE);
        int think = p.Function();
        p.Op(OP_DONE);
        p.Globals[G0 + 1] = think;

        QuakeEngine e = Load(p);
        Edict ent = e.sv.edicts[1];
        e.pr_global_struct.self = e.EDICT_TO_PROG(ent);
        e.pr_global_struct.time = 1.3f;
        e.PR_ExecuteProgram(main);

        Assert.Equal(7f, ent.v.frame);
        Assert.Equal(think, ent.v.think);
        Assert.Equal((float)(1.3f + 0.1), ent.v.nextthink);
    }
}
