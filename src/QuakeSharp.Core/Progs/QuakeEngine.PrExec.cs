using System.Runtime.CompilerServices;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Progs;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Progs.PrComp;

namespace QuakeSharp.Core;

// pr_exec.c: the QuakeC interpreter
public sealed partial class QuakeEngine
{
    // pr_exec.c: prstack_t
    private struct prstack_t
    {
        public int s;
        public dfunction_t? f;
    }

    private const int MAX_STACK_DEPTH = 32;

    // pr_stack has one more entry than MAX_STACK_DEPTH: PR_StackTrace writes pr_stack[pr_depth] with pr_depth up to
    // MAX_STACK_DEPTH (in C, past the array, into pr_depth itself's neighbour)
    private readonly prstack_t[] pr_stack = new prstack_t[MAX_STACK_DEPTH + 1];
    public int pr_depth;

    private const int LOCALSTACK_SIZE = 2048;
    private readonly int[] localstack = new int[LOCALSTACK_SIZE];
    private int localstack_used;

    public bool pr_trace;
    public dfunction_t? pr_xfunction;
    public int pr_xstatement;

    public int pr_argc;

    /// <summary>pr_cmds.c: pr_builtins, the engine's functions QuakeC calls (negative first_statement)</summary>
    public Action[] pr_builtins = [];

    public int pr_numbuiltins;

    private static readonly string[] pr_opnames =
    [
        "DONE",

        "MUL_F",
        "MUL_V",
        "MUL_FV",
        "MUL_VF",

        "DIV",

        "ADD_F",
        "ADD_V",

        "SUB_F",
        "SUB_V",

        "EQ_F",
        "EQ_V",
        "EQ_S",
        "EQ_E",
        "EQ_FNC",

        "NE_F",
        "NE_V",
        "NE_S",
        "NE_E",
        "NE_FNC",

        "LE",
        "GE",
        "LT",
        "GT",

        "INDIRECT",
        "INDIRECT",
        "INDIRECT",
        "INDIRECT",
        "INDIRECT",
        "INDIRECT",

        "ADDRESS",

        "STORE_F",
        "STORE_V",
        "STORE_S",
        "STORE_ENT",
        "STORE_FLD",
        "STORE_FNC",

        "STOREP_F",
        "STOREP_V",
        "STOREP_S",
        "STOREP_ENT",
        "STOREP_FLD",
        "STOREP_FNC",

        "RETURN",

        "NOT_F",
        "NOT_V",
        "NOT_S",
        "NOT_ENT",
        "NOT_FNC",

        "IF",
        "IFNOT",

        "CALL0",
        "CALL1",
        "CALL2",
        "CALL3",
        "CALL4",
        "CALL5",
        "CALL6",
        "CALL7",
        "CALL8",

        "STATE",

        "GOTO",

        "AND",
        "OR",

        "BITAND",
        "BITOR",
    ];

    // pr_exec.c: PR_PrintStatement
    public void PR_PrintStatement(in dstatement_t s)
    {
        int i;

        if (s.op < pr_opnames.Length)
        {
            Con_Printf("%s ", pr_opnames[s.op]);
            i = pr_opnames[s.op].Length;
            for (; i < 10; i++)
            {
                Con_Printf(" ");
            }
        }

        if (s.op is OP_IF or OP_IFNOT)
        {
            Con_Printf("%sbranch %i", PR_GlobalString(s.a), (int)s.b);
        }
        else if (s.op == OP_GOTO)
        {
            Con_Printf("branch %i", (int)s.a);
        }
        else if ((uint)(s.op - OP_STORE_F) < 6)
        {
            Con_Printf("%s", PR_GlobalString(s.a));
            Con_Printf("%s", PR_GlobalStringNoContents(s.b));
        }
        else
        {
            if (s.a != 0)
            {
                Con_Printf("%s", PR_GlobalString(s.a));
            }

            if (s.b != 0)
            {
                Con_Printf("%s", PR_GlobalString(s.b));
            }

            if (s.c != 0)
            {
                Con_Printf("%s", PR_GlobalStringNoContents(s.c));
            }
        }

        Con_Printf("\n");
    }

    // pr_exec.c: PR_StackTrace
    public void PR_StackTrace()
    {
        dfunction_t? f;
        int i;

        if (pr_depth == 0)
        {
            Con_Printf("<NO STACK>\n");
            return;
        }

        pr_stack[pr_depth].f = pr_xfunction;
        for (i = pr_depth; i >= 0; i--)
        {
            f = pr_stack[i].f;

            if (f == null)
            {
                Con_Printf("<NO FUNCTION>\n");
            }
            else
            {
                Con_Printf("%12s : %s\n", PR_GetString(f.s_file), PR_GetString(f.s_name));
            }
        }
    }

    // pr_exec.c: PR_Profile_f
    public void PR_Profile_f()
    {
        dfunction_t? best;
        int max;
        int num;
        int i;

        num = 0;
        do
        {
            max = 0;
            best = null;
            for (i = 0; i < progs!.numfunctions; i++)
            {
                dfunction_t f = pr_functions[i];
                if (f.profile > max)
                {
                    max = f.profile;
                    best = f;
                }
            }

            if (best != null)
            {
                if (num < 10)
                {
                    Con_Printf("%7i %s\n", best.profile, PR_GetString(best.s_name));
                }

                num++;
                best.profile = 0;
            }
        }
        while (best != null);
    }

    // pr_exec.c: PR_RunError
    // Aborts the currently executing function
    public HostAbortException PR_RunError(string error, params ReadOnlySpan<PrintfArg> args)
    {
        string @string = CFormat.Sprintf(error, args);

        PR_PrintStatement(pr_statements[pr_xstatement]);
        PR_StackTrace();
        Con_Printf("%s\n", @string);

        pr_depth = 0; // dump the stack so host_error can shutdown functions

        return Host_Error("Program error");
    }

    // ============================================================================
    // PR_ExecuteProgram
    //
    // The interpretation main loop
    // ============================================================================

    // pr_exec.c: PR_EnterFunction
    // Returns the new program statement counter
    public int PR_EnterFunction(dfunction_t f)
    {
        int i, j, c, o;

        pr_stack[pr_depth].s = pr_xstatement;
        pr_stack[pr_depth].f = pr_xfunction;
        pr_depth++;
        if (pr_depth >= MAX_STACK_DEPTH)
        {
            throw PR_RunError("stack overflow");
        }

        // save off any locals that the new function steps on
        c = f.locals;
        if (localstack_used + c > LOCALSTACK_SIZE)
        {
            throw PR_RunError("PR_ExecuteProgram: locals stack overflow\n");
        }

        for (i = 0; i < c; i++)
        {
            localstack[localstack_used + i] = pr_globals[f.parm_start + i];
        }

        localstack_used += c;

        // copy parameters
        o = f.parm_start;
        for (i = 0; i < f.numparms; i++)
        {
            for (j = 0; j < f.parm_size[i]; j++)
            {
                pr_globals[o] = pr_globals[OFS_PARM0 + (i * 3) + j];
                o++;
            }
        }

        pr_xfunction = f;
        return f.first_statement - 1; // offset the s++
    }

    // pr_exec.c: PR_LeaveFunction
    public int PR_LeaveFunction()
    {
        int i, c;

        if (pr_depth <= 0)
        {
            throw Sys_Error("prog stack underflow");
        }

        // restore locals from the stack
        c = pr_xfunction!.locals;
        localstack_used -= c;
        if (localstack_used < 0)
        {
            throw PR_RunError("PR_ExecuteProgram: locals stack underflow\n");
        }

        for (i = 0; i < c; i++)
        {
            pr_globals[pr_xfunction.parm_start + i] = localstack[localstack_used + i];
        }

        // up stack
        pr_depth--;
        pr_xfunction = pr_stack[pr_depth].f;
        return pr_stack[pr_depth].s;
    }

    // a float slot of the globals
    private ref float GF(int o) => ref Unsafe.As<int, float>(ref pr_globals[o]);

    // a float slot of the edicts' memory
    private ref float EF(int o) => ref Unsafe.As<int, float>(ref sv.edictmem[o]);

    // pr_exec.c: PR_ExecuteProgram
    public void PR_ExecuteProgram(int fnum)
    {
        int a, b, c;
        int s;
        dfunction_t f, newf;
        int runaway;
        int i;
        Edict ed;
        int exitdepth;
        int ptr;
        int[] g = pr_globals;

        if (fnum == 0 || fnum >= progs!.numfunctions)
        {
            if (pr_global_struct.self != 0)
            {
                ED_Print(PROG_TO_EDICT(pr_global_struct.self));
            }

            throw Host_Error("PR_ExecuteProgram: NULL function");
        }

        f = pr_functions[fnum];

        runaway = 100000;
        pr_trace = false;

        // make a stack frame
        exitdepth = pr_depth;

        s = PR_EnterFunction(f);

        while (true)
        {
            s++; // next statement

            ref dstatement_t st = ref pr_statements[s];
            a = st.a;
            b = st.b;
            c = st.c;

            if (--runaway == 0)
            {
                throw PR_RunError("runaway loop error");
            }

            pr_xfunction!.profile++;
            pr_xstatement = s;

            if (pr_trace)
            {
                PR_PrintStatement(st);
            }

            switch (st.op)
            {
                case OP_ADD_F:
                    GF(c) = GF(a) + GF(b);
                    break;
                case OP_ADD_V:
                    GF(c) = GF(a) + GF(b);
                    GF(c + 1) = GF(a + 1) + GF(b + 1);
                    GF(c + 2) = GF(a + 2) + GF(b + 2);
                    break;

                case OP_SUB_F:
                    GF(c) = GF(a) - GF(b);
                    break;
                case OP_SUB_V:
                    GF(c) = GF(a) - GF(b);
                    GF(c + 1) = GF(a + 1) - GF(b + 1);
                    GF(c + 2) = GF(a + 2) - GF(b + 2);
                    break;

                case OP_MUL_F:
                    GF(c) = GF(a) * GF(b);
                    break;
                case OP_MUL_V:
                    GF(c) = (GF(a) * GF(b)) + (GF(a + 1) * GF(b + 1)) + (GF(a + 2) * GF(b + 2));
                    break;
                case OP_MUL_FV:
                    GF(c) = GF(a) * GF(b);
                    GF(c + 1) = GF(a) * GF(b + 1);
                    GF(c + 2) = GF(a) * GF(b + 2);
                    break;
                case OP_MUL_VF:
                    GF(c) = GF(b) * GF(a);
                    GF(c + 1) = GF(b) * GF(a + 1);
                    GF(c + 2) = GF(b) * GF(a + 2);
                    break;

                case OP_DIV_F:
                    GF(c) = GF(a) / GF(b);
                    break;

                case OP_BITAND:
                    GF(c) = X86.FtoI(GF(a)) & X86.FtoI(GF(b));
                    break;

                case OP_BITOR:
                    GF(c) = X86.FtoI(GF(a)) | X86.FtoI(GF(b));
                    break;

                case OP_GE:
                    GF(c) = GF(a) >= GF(b) ? 1 : 0;
                    break;
                case OP_LE:
                    GF(c) = GF(a) <= GF(b) ? 1 : 0;
                    break;
                case OP_GT:
                    GF(c) = GF(a) > GF(b) ? 1 : 0;
                    break;
                case OP_LT:
                    GF(c) = GF(a) < GF(b) ? 1 : 0;
                    break;
                case OP_AND:
                    GF(c) = GF(a) != 0 && GF(b) != 0 ? 1 : 0;
                    break;
                case OP_OR:
                    GF(c) = GF(a) != 0 || GF(b) != 0 ? 1 : 0;
                    break;

                case OP_NOT_F:
                    GF(c) = GF(a) == 0 ? 1 : 0;
                    break;
                case OP_NOT_V:
                    GF(c) = GF(a) == 0 && GF(a + 1) == 0 && GF(a + 2) == 0 ? 1 : 0;
                    break;
                case OP_NOT_S:
                    GF(c) = g[a] == 0 || PR_GetString(g[a]).Length == 0 ? 1 : 0;
                    break;
                case OP_NOT_FNC:
                    GF(c) = g[a] == 0 ? 1 : 0;
                    break;
                case OP_NOT_ENT:
                    // PROG_TO_EDICT(a->edict) == sv.edicts
                    GF(c) = g[a] == 0 ? 1 : 0;
                    break;

                case OP_EQ_F:
                    GF(c) = GF(a) == GF(b) ? 1 : 0;
                    break;
                case OP_EQ_V:
                    GF(c) = GF(a) == GF(b) && GF(a + 1) == GF(b + 1) && GF(a + 2) == GF(b + 2) ? 1 : 0;
                    break;
                case OP_EQ_S:
                    GF(c) = Q_strcmp(PR_GetString(g[a]), PR_GetString(g[b])) == 0 ? 1 : 0;
                    break;
                case OP_EQ_E:
                    GF(c) = g[a] == g[b] ? 1 : 0;
                    break;
                case OP_EQ_FNC:
                    GF(c) = g[a] == g[b] ? 1 : 0;
                    break;

                case OP_NE_F:
                    GF(c) = GF(a) != GF(b) ? 1 : 0;
                    break;
                case OP_NE_V:
                    GF(c) = GF(a) != GF(b) || GF(a + 1) != GF(b + 1) || GF(a + 2) != GF(b + 2) ? 1 : 0;
                    break;
                case OP_NE_S:
                    // strcmp's result itself, as a float
                    GF(c) = CStrcmp(PR_GetString(g[a]), PR_GetString(g[b]));
                    break;
                case OP_NE_E:
                    GF(c) = g[a] != g[b] ? 1 : 0;
                    break;
                case OP_NE_FNC:
                    GF(c) = g[a] != g[b] ? 1 : 0;
                    break;

                // ==================
                case OP_STORE_F:
                case OP_STORE_ENT:
                case OP_STORE_FLD: // integers
                case OP_STORE_S:
                case OP_STORE_FNC: // pointers
                    g[b] = g[a];
                    break;
                case OP_STORE_V:
                    g[b] = g[a];
                    g[b + 1] = g[a + 1];
                    g[b + 2] = g[a + 2];
                    break;

                case OP_STOREP_F:
                case OP_STOREP_ENT:
                case OP_STOREP_FLD: // integers
                case OP_STOREP_S:
                case OP_STOREP_FNC: // pointers
                    ptr = EdictSlot(g[b]);
                    sv.edictmem[ptr] = g[a];
                    break;
                case OP_STOREP_V:
                    ptr = EdictSlot(g[b]);
                    sv.edictmem[ptr] = g[a];
                    sv.edictmem[ptr + 1] = g[a + 1];
                    sv.edictmem[ptr + 2] = g[a + 2];
                    break;

                case OP_ADDRESS:
                    ed = PROG_TO_EDICT(g[a]);
                    if (ed == sv.edicts[0] && sv.state == Server.server_state_t.ss_active)
                    {
                        throw PR_RunError("assignment to world entity");
                    }

                    g[c] = (ed.fields + g[b]) * 4;
                    break;

                case OP_LOAD_F:
                case OP_LOAD_FLD:
                case OP_LOAD_ENT:
                case OP_LOAD_S:
                case OP_LOAD_FNC:
                    ed = PROG_TO_EDICT(g[a]);
                    g[c] = sv.edictmem[ed.fields + g[b]];
                    break;

                case OP_LOAD_V:
                    ed = PROG_TO_EDICT(g[a]);
                    ptr = ed.fields + g[b];
                    g[c] = sv.edictmem[ptr];
                    g[c + 1] = sv.edictmem[ptr + 1];
                    g[c + 2] = sv.edictmem[ptr + 2];
                    break;

                // ==================
                case OP_IFNOT:
                    if (g[a] == 0)
                    {
                        s += st.b - 1; // offset the s++
                    }

                    break;

                case OP_IF:
                    if (g[a] != 0)
                    {
                        s += st.b - 1; // offset the s++
                    }

                    break;

                case OP_GOTO:
                    s += st.a - 1; // offset the s++
                    break;

                case OP_CALL0:
                case OP_CALL1:
                case OP_CALL2:
                case OP_CALL3:
                case OP_CALL4:
                case OP_CALL5:
                case OP_CALL6:
                case OP_CALL7:
                case OP_CALL8:
                    pr_argc = st.op - OP_CALL0;
                    if (g[a] == 0)
                    {
                        throw PR_RunError("NULL function");
                    }

                    newf = pr_functions[g[a]];

                    if (newf.first_statement < 0)
                    {
                        // negative statements are built in functions
                        i = -newf.first_statement;
                        if (i >= pr_numbuiltins)
                        {
                            throw PR_RunError("Bad builtin call number");
                        }

                        pr_builtins[i]();
                        break;
                    }

                    s = PR_EnterFunction(newf);
                    break;

                case OP_DONE:
                case OP_RETURN:
                    g[OFS_RETURN] = g[a];
                    g[OFS_RETURN + 1] = g[a + 1];
                    g[OFS_RETURN + 2] = g[a + 2];

                    s = PR_LeaveFunction();
                    if (pr_depth == exitdepth)
                    {
                        return; // all done
                    }

                    break;

                case OP_STATE:
                    ed = PROG_TO_EDICT(pr_global_struct.self);
                    ed.v.nextthink = (float)(pr_global_struct.time + 0.1);
                    if (GF(a) != ed.v.frame)
                    {
                        ed.v.frame = GF(a);
                    }

                    ed.v.think = g[b];
                    break;

                default:
                    throw PR_RunError("Bad opcode %i", (int)st.op);
            }
        }
    }

    // (byte *)sv.edicts + ofs as a slot of the edicts' memory (QuakeC's pointers are 4-byte aligned)
    private int EdictSlot(int ofs)
    {
        if ((ofs & 3) != 0 || (uint)(ofs >> 2) >= (uint)sv.edictmem.Length)
        {
            throw Sys_Error("QuakeC pointer %i outside the edicts", ofs);
        }

        return ofs >> 2;
    }

    // strcmp's result as the Microsoft C runtime gives it (-1, 0 or 1), comparing unsigned characters
    private static int CStrcmp(string s1, string s2)
    {
        int i = 0;
        while (true)
        {
            int c1 = i < s1.Length ? (byte)s1[i] : 0;
            int c2 = i < s2.Length ? (byte)s2[i] : 0;
            if (c1 != c2)
            {
                return c1 < c2 ? -1 : 1;
            }

            if (c1 == 0)
            {
                return 0;
            }

            i++;
        }
    }
}
