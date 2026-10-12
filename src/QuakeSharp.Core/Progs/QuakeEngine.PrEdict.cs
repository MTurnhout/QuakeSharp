using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Progs;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Progs.PrComp;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

// pr_edict.c (sv_edict.c): entity dictionary
public sealed partial class QuakeEngine
{
    public dprograms_t? progs;
    public dfunction_t[] pr_functions = [];
    public ddef_t[] pr_fielddefs = [];
    public ddef_t[] pr_globaldefs = [];
    public dstatement_t[] pr_statements = [];

    /// <summary>pr_edict.c: pr_globals, the progs' globals as 4-byte slots (pr_global_struct is their start)</summary>
    public int[] pr_globals = [];

    /// <summary>pr_edict.c: pr_global_struct</summary>
    public GlobalVars pr_global_struct;

    /// <summary>in bytes</summary>
    public int pr_edict_size;

    public ushort pr_crc;

    public static readonly int[] type_size = [1, 1, 1, 3, 1, 1, 1, 1];

    public readonly Cvar nomonsters = new("nomonsters", "0");
    public readonly Cvar gamecfg = new("gamecfg", "0");
    public readonly Cvar scratch1 = new("scratch1", "0");
    public readonly Cvar scratch2 = new("scratch2", "0");
    public readonly Cvar scratch3 = new("scratch3", "0");
    public readonly Cvar scratch4 = new("scratch4", "0");
    public readonly Cvar savedgamecfg = new("savedgamecfg", "0", true);
    public readonly Cvar saved1 = new("saved1", "0", true);
    public readonly Cvar saved2 = new("saved2", "0", true);
    public readonly Cvar saved3 = new("saved3", "0", true);
    public readonly Cvar saved4 = new("saved4", "0", true);

    private const int MAX_FIELD_LEN = 64;
    private const int GEFV_CACHESIZE = 2;

    // pr_edict.c: gefvCache, GetEdictFieldValue's static rep
    private readonly (int? pcache, string field)[] gefvCache = [(null, ""), (null, "")];
    private int gefvRep;

    // ===============================================================================
    // The strings (pr_strings, design.md §11.1). An offset from 0 up to the progs' string table's size is in the
    // table; past it, the heap: what ED_NewString copies and the C buffers QuakeSharp has no address for (a model's
    // name, localmodels, sv.name...), one after another as on the C's hunk. The buffers that change while QuakeC points
    // into them have fixed offsets below 0: pr_string_temp, and each client's name.
    // ===============================================================================

    private byte[] pr_stringtable = [0];
    private readonly List<byte> pr_stringheap = [];
    private readonly Dictionary<string, int> pr_stringconstants = new(StringComparer.Ordinal);

    /// <summary>pr_cmds.c: pr_string_temp's offset (ftos, vtos and etos write into it)</summary>
    public const int PR_STRING_TEMP = -1;

    /// <summary>The offset of client <paramref name="n"/>'s name buffer (host_client->name - pr_strings).</summary>
    public static int PR_ClientNameString(int n) => -2 - n;

    /// <summary>A new progs' string table (from PR_LoadProgs), with an empty heap.</summary>
    public void PR_SetStringTable(byte[] table)
    {
        pr_stringtable = table;
        pr_stringheap.Clear();
        pr_stringconstants.Clear();
    }

    /// <summary>pr_strings + ofs: the string at the offset, up to its 0.</summary>
    public string PR_GetString(int ofs)
    {
        if (ofs >= 0 && ofs < pr_stringtable.Length)
        {
            int end = Array.IndexOf(pr_stringtable, (byte)0, ofs);
            return Encoding.Latin1.GetString(pr_stringtable, ofs, (end < 0 ? pr_stringtable.Length : end) - ofs);
        }

        int heap = ofs - pr_stringtable.Length;
        if (heap >= 0 && heap < pr_stringheap.Count)
        {
            var sb = new StringBuilder();
            for (int i = heap; i < pr_stringheap.Count && pr_stringheap[i] != 0; i++)
            {
                sb.Append((char)pr_stringheap[i]);
            }

            return sb.ToString();
        }

        if (ofs == PR_STRING_TEMP)
        {
            return pr_string_temp;
        }

        int client = -2 - ofs;
        if (client >= 0 && client < svs.clients.Length)
        {
            return svs.clients[client].name;
        }

        throw Sys_Error("PR_GetString: bad string offset %i", ofs);
    }

    // a copy on the heap, as Hunk_Alloc puts it there: its offset
    private int PR_HeapString(string s)
    {
        int ofs = pr_stringtable.Length + pr_stringheap.Count;
        int end = s.IndexOf('\0', StringComparison.Ordinal);
        foreach (char c in end < 0 ? s : s[..end])
        {
            pr_stringheap.Add((byte)c);
        }

        pr_stringheap.Add(0);
        return ofs;
    }

    /// <summary>
    /// The offset of a C buffer that does not change while the progs run (a model's name, localmodels[i], a precache
    /// entry, sv.name): one offset for each contents, as the C has one address for each buffer.
    /// </summary>
    public int PR_SetString(string s)
    {
        if (!pr_stringconstants.TryGetValue(s, out int ofs))
        {
            ofs = PR_HeapString(s);
            pr_stringconstants[s] = ofs;
        }

        return ofs;
    }

    // ===============================================================================

    // progs.h: EDICT_NUM
    public Edict EDICT_NUM(int n)
    {
        if (n < 0 || n >= sv.max_edicts)
        {
            throw Sys_Error("EDICT_NUM: bad number %i", n);
        }

        return sv.edicts[n];
    }

    // progs.h: NUM_FOR_EDICT
    public int NUM_FOR_EDICT(Edict e)
    {
        int b = e.num;

        if (b < 0 || b >= sv.num_edicts)
        {
            throw Sys_Error("NUM_FOR_EDICT: bad pointer");
        }

        return b;
    }

    // progs.h: NEXT_EDICT
    public Edict NEXT_EDICT(Edict e) => sv.edicts[e.num + 1];

    // progs.h: EDICT_TO_PROG
    public int EDICT_TO_PROG(Edict e) => e.num * pr_edict_size;

    // progs.h: PROG_TO_EDICT (an offset that is not an edict's start would point into the middle of one in C)
    public Edict PROG_TO_EDICT(int e)
    {
        if (e % pr_edict_size != 0 || (uint)(e / pr_edict_size) >= (uint)sv.edicts.Length)
        {
            throw Sys_Error("PROG_TO_EDICT: %i is not an edict", e);
        }

        return sv.edicts[e / pr_edict_size];
    }

    // progs.h: G_FLOAT, G_INT, G_EDICT, G_EDICTNUM, G_VECTOR, G_STRING, G_FUNCTION
    public ref float G_FLOAT(int o) => ref Unsafe.As<int, float>(ref pr_globals[o]);

    public ref int G_INT(int o) => ref pr_globals[o];

    public Edict G_EDICT(int o) => PROG_TO_EDICT(pr_globals[o]);

    public int G_EDICTNUM(int o) => NUM_FOR_EDICT(G_EDICT(o));

    public ref Vec3 G_VECTOR(int o) => ref Unsafe.As<int, Vec3>(ref pr_globals[o]);

    public string G_STRING(int o) => PR_GetString(pr_globals[o]);

    public int G_FUNCTION(int o) => pr_globals[o];

    // progs.h: E_FLOAT, E_INT, E_VECTOR, E_STRING (o: a field's slot)
    public ref float E_FLOAT(Edict e, int o) => ref Unsafe.As<int, float>(ref sv.edictmem[e.fields + o]);

    public ref int E_INT(Edict e, int o) => ref sv.edictmem[e.fields + o];

    public ref Vec3 E_VECTOR(Edict e, int o) => ref Unsafe.As<int, Vec3>(ref sv.edictmem[e.fields + o]);

    public string E_STRING(Edict e, int o) => PR_GetString(sv.edictmem[e.fields + o]);

    // pr_edict.c: ED_ClearEdict
    // Sets everything to NULL
    public void ED_ClearEdict(Edict e)
    {
        Array.Clear(sv.edictmem, e.fields, progs!.entityfields);
        e.free = false;
    }

    // pr_edict.c: ED_Alloc
    // Either finds a free edict, or allocates a new one. Try to avoid reusing an entity that was recently freed,
    // because it can cause the client to think the entity morphed into something else instead of being removed and
    // recreated, which can cause interpolated angles and bad trails.
    public Edict ED_Alloc()
    {
        int i;
        Edict e;

        for (i = svs.maxclients + 1; i < sv.num_edicts; i++)
        {
            e = EDICT_NUM(i);

            // the first couple seconds of server time can involve a lot of freeing and allocating, so relax the
            // replacement policy
            if (e.free && (e.freetime < 2 || sv.time - e.freetime > 0.5))
            {
                ED_ClearEdict(e);
                return e;
            }
        }

        if (i == MAX_EDICTS)
        {
            throw Sys_Error("ED_Alloc: no free edicts");
        }

        sv.num_edicts++;
        e = EDICT_NUM(i);
        ED_ClearEdict(e);

        return e;
    }

    // pr_edict.c: ED_Free
    // Marks the edict as free
    // FIXME: walk all entities and NULL out references to this entity
    public void ED_Free(Edict ed)
    {
        SV_UnlinkEdict(ed); // unlink from world bsp

        ed.free = true;
        ed.v.model = 0;
        ed.v.takedamage = 0;
        ed.v.modelindex = 0;
        ed.v.colormap = 0;
        ed.v.skin = 0;
        ed.v.frame = 0;
        ed.v.origin = MathLib.vec3_origin;
        ed.v.angles = MathLib.vec3_origin;
        ed.v.nextthink = -1;
        ed.v.solid = 0;

        ed.freetime = (float)sv.time;
    }

    // pr_edict.c: ED_GlobalAtOfs
    public int? ED_GlobalAtOfs(int ofs)
    {
        for (int i = 0; i < progs!.numglobaldefs; i++)
        {
            if (pr_globaldefs[i].ofs == ofs)
            {
                return i;
            }
        }

        return null;
    }

    // pr_edict.c: ED_FieldAtOfs
    public int? ED_FieldAtOfs(int ofs)
    {
        for (int i = 0; i < progs!.numfielddefs; i++)
        {
            if (pr_fielddefs[i].ofs == ofs)
            {
                return i;
            }
        }

        return null;
    }

    // pr_edict.c: ED_FindField
    public int? ED_FindField(string name)
    {
        for (int i = 0; i < progs!.numfielddefs; i++)
        {
            if (Q_strcmp(PR_GetString(pr_fielddefs[i].s_name), name) == 0)
            {
                return i;
            }
        }

        return null;
    }

    // pr_edict.c: ED_FindGlobal
    public int? ED_FindGlobal(string name)
    {
        for (int i = 0; i < progs!.numglobaldefs; i++)
        {
            if (Q_strcmp(PR_GetString(pr_globaldefs[i].s_name), name) == 0)
            {
                return i;
            }
        }

        return null;
    }

    // pr_edict.c: ED_FindFunction
    public dfunction_t? ED_FindFunction(string name)
    {
        for (int i = 0; i < progs!.numfunctions; i++)
        {
            if (Q_strcmp(PR_GetString(pr_functions[i].s_name), name) == 0)
            {
                return pr_functions[i];
            }
        }

        return null;
    }

    // pr_edict.c: GetEdictFieldValue: the slot of the named field in the edict's memory, or -1 (NULL)
    public int GetEdictFieldValue(Edict ed, string field)
    {
        int? def = null;
        int i;

        for (i = 0; i < GEFV_CACHESIZE; i++)
        {
            if (Q_strcmp(field, gefvCache[i].field) == 0)
            {
                def = gefvCache[i].pcache;
                goto Done;
            }
        }

        def = ED_FindField(field);

        if (Q_strlen(field) < MAX_FIELD_LEN)
        {
            gefvCache[gefvRep] = (def, field);
            gefvRep ^= 1;
        }

    Done:
        if (def == null)
        {
            return -1;
        }

        return ed.fields + pr_fielddefs[def.Value].ofs;
    }

    // the type of a definition without DEF_SAVEGLOBAL
    private static etype_t DefType(int type) => (etype_t)(type & ~DEF_SAVEGLOBAL);

    // pr_edict.c: PR_ValueString
    // Returns a string describing *data in a type specific manner (mem[ofs] is *val)
    public string PR_ValueString(int type, int[] mem, int ofs)
    {
        type &= ~DEF_SAVEGLOBAL;

        switch ((etype_t)type)
        {
            case etype_t.ev_string:
                return PR_GetString(mem[ofs]);
            case etype_t.ev_entity:
                return CFormat.Sprintf("entity %i", NUM_FOR_EDICT(PROG_TO_EDICT(mem[ofs])));
            case etype_t.ev_function:
                return CFormat.Sprintf("%s()", PR_GetString(pr_functions[mem[ofs]].s_name));
            case etype_t.ev_field:
                return CFormat.Sprintf(".%s", PR_GetString(pr_fielddefs[ED_FieldAtOfs(mem[ofs])!.Value].s_name));
            case etype_t.ev_void:
                return "void";
            case etype_t.ev_float:
                return CFormat.Sprintf("%5.1f", BitConverter.Int32BitsToSingle(mem[ofs]));
            case etype_t.ev_vector:
                return CFormat.Sprintf("'%5.1f %5.1f %5.1f'", BitConverter.Int32BitsToSingle(mem[ofs]),
                    BitConverter.Int32BitsToSingle(mem[ofs + 1]), BitConverter.Int32BitsToSingle(mem[ofs + 2]));
            case etype_t.ev_pointer:
                return "pointer";
            default:
                return CFormat.Sprintf("bad type %i", type);
        }
    }

    // pr_edict.c: PR_UglyValueString
    // Returns a string describing *data in a type specific manner. Easier to parse than PR_ValueString
    public string PR_UglyValueString(int type, int[] mem, int ofs)
    {
        type &= ~DEF_SAVEGLOBAL;

        switch ((etype_t)type)
        {
            case etype_t.ev_string:
                return PR_GetString(mem[ofs]);
            case etype_t.ev_entity:
                return CFormat.Sprintf("%i", NUM_FOR_EDICT(PROG_TO_EDICT(mem[ofs])));
            case etype_t.ev_function:
                return PR_GetString(pr_functions[mem[ofs]].s_name);
            case etype_t.ev_field:
                return PR_GetString(pr_fielddefs[ED_FieldAtOfs(mem[ofs])!.Value].s_name);
            case etype_t.ev_void:
                return "void";
            case etype_t.ev_float:
                return CFormat.Sprintf("%f", BitConverter.Int32BitsToSingle(mem[ofs]));
            case etype_t.ev_vector:
                return CFormat.Sprintf("%f %f %f", BitConverter.Int32BitsToSingle(mem[ofs]),
                    BitConverter.Int32BitsToSingle(mem[ofs + 1]), BitConverter.Int32BitsToSingle(mem[ofs + 2]));
            default:
                return CFormat.Sprintf("bad type %i", type);
        }
    }

    // pr_edict.c: PR_GlobalString
    // Returns a string with a description and the contents of a global, padded to 20 field width
    public string PR_GlobalString(int ofs)
    {
        string line;
        int? def = ED_GlobalAtOfs(ofs);

        if (def == null)
        {
            line = CFormat.Sprintf("%i(???)", ofs);
        }
        else
        {
            string s = PR_ValueString(pr_globaldefs[def.Value].type, pr_globals, ofs);
            line = CFormat.Sprintf("%i(%s)%s", ofs, PR_GetString(pr_globaldefs[def.Value].s_name), s);
        }

        return line.PadRight(20) + " ";
    }

    // pr_edict.c: PR_GlobalStringNoContents
    public string PR_GlobalStringNoContents(int ofs)
    {
        string line;
        int? def = ED_GlobalAtOfs(ofs);

        if (def == null)
        {
            line = CFormat.Sprintf("%i(???)", ofs);
        }
        else
        {
            line = CFormat.Sprintf("%i(%s)", ofs, PR_GetString(pr_globaldefs[def.Value].s_name));
        }

        return line.PadRight(20) + " ";
    }

    // name[strlen(name)-2] == '_': the _x, _y, _z vars (a name shorter than 2 characters reads before its start in C;
    // here it is not one of them)
    private static bool IsVectorComponent(string name)
    {
        int l = Q_strlen(name);
        return l >= 2 && name[l - 2] == '_';
    }

    // the slots of a field's value all 0
    private bool FieldIsZero(Edict ed, int ofs, int type)
    {
        int j;
        for (j = 0; j < type_size[type]; j++)
        {
            if (sv.edictmem[ed.fields + ofs + j] != 0)
            {
                break;
            }
        }

        return j == type_size[type];
    }

    // pr_edict.c: ED_Print
    // For debugging
    public void ED_Print(Edict ed)
    {
        int l;
        int i;
        string name;
        int type;

        if (ed.free)
        {
            Con_Printf("FREE\n");
            return;
        }

        Con_Printf("\nEDICT %i:\n", NUM_FOR_EDICT(ed));
        for (i = 1; i < progs!.numfielddefs; i++)
        {
            ref ddef_t d = ref pr_fielddefs[i];
            name = PR_GetString(d.s_name);
            if (IsVectorComponent(name))
            {
                continue; // skip _x, _y, _z vars
            }

            // if the value is still all 0, skip the field
            type = d.type & ~DEF_SAVEGLOBAL;
            if (FieldIsZero(ed, d.ofs, type))
            {
                continue;
            }

            Con_Printf("%s", name);
            l = Q_strlen(name);
            while (l++ < 15)
            {
                Con_Printf(" ");
            }

            Con_Printf("%s\n", PR_ValueString(d.type, sv.edictmem, ed.fields + d.ofs));
        }
    }

    // pr_edict.c: ED_Write
    // For savegames
    public void ED_Write(StringBuilder f, Edict ed)
    {
        int i;
        string name;
        int type;

        f.Append("{\n");

        if (ed.free)
        {
            f.Append("}\n");
            return;
        }

        for (i = 1; i < progs!.numfielddefs; i++)
        {
            ref ddef_t d = ref pr_fielddefs[i];
            name = PR_GetString(d.s_name);
            if (IsVectorComponent(name))
            {
                continue; // skip _x, _y, _z vars
            }

            // if the value is still all 0, skip the field
            type = d.type & ~DEF_SAVEGLOBAL;
            if (FieldIsZero(ed, d.ofs, type))
            {
                continue;
            }

            f.Append(CFormat.Sprintf("\"%s\" ", name));
            f.Append(CFormat.Sprintf("\"%s\"\n", PR_UglyValueString(d.type, sv.edictmem, ed.fields + d.ofs)));
        }

        f.Append("}\n");
    }

    // pr_edict.c: ED_PrintNum
    public void ED_PrintNum(int ent) => ED_Print(EDICT_NUM(ent));

    // pr_edict.c: ED_PrintEdicts
    // For debugging, prints all the entities in the current server
    public void ED_PrintEdicts()
    {
        int i;

        Con_Printf("%i entities\n", sv.num_edicts);
        for (i = 0; i < sv.num_edicts; i++)
        {
            ED_PrintNum(i);
        }
    }

    // pr_edict.c: ED_PrintEdict_f
    // For debugging, prints a single edicy
    public void ED_PrintEdict_f()
    {
        int i;

        i = Q_atoi(Cmd_Argv(1));
        if (i >= sv.num_edicts)
        {
            Con_Printf("Bad edict number\n");
            return;
        }

        ED_PrintNum(i);
    }

    // pr_edict.c: ED_Count
    // For debugging
    public void ED_Count()
    {
        int i;
        Edict ent;
        int active, models, solid, step;

        active = models = solid = step = 0;
        for (i = 0; i < sv.num_edicts; i++)
        {
            ent = EDICT_NUM(i);
            if (ent.free)
            {
                continue;
            }

            active++;
            if (ent.v.solid != 0)
            {
                solid++;
            }

            if (ent.v.model != 0)
            {
                models++;
            }

            if (ent.v.movetype == MOVETYPE_STEP)
            {
                step++;
            }
        }

        Con_Printf("num_edicts:%3i\n", sv.num_edicts);
        Con_Printf("active    :%3i\n", active);
        Con_Printf("view      :%3i\n", models);
        Con_Printf("touch     :%3i\n", solid);
        Con_Printf("step      :%3i\n", step);
    }

    // ==============================================================================
    // ARCHIVING GLOBALS
    //
    // FIXME: need to tag constants, doesn't really work
    // ==============================================================================

    // pr_edict.c: ED_WriteGlobals
    public void ED_WriteGlobals(StringBuilder f)
    {
        int i;
        string name;
        int type;

        f.Append("{\n");
        for (i = 0; i < progs!.numglobaldefs; i++)
        {
            ref ddef_t def = ref pr_globaldefs[i];
            type = def.type;
            if ((def.type & DEF_SAVEGLOBAL) == 0)
            {
                continue;
            }

            type &= ~DEF_SAVEGLOBAL;

            if (type != (int)etype_t.ev_string
                && type != (int)etype_t.ev_float
                && type != (int)etype_t.ev_entity)
            {
                continue;
            }

            name = PR_GetString(def.s_name);
            f.Append(CFormat.Sprintf("\"%s\" ", name));
            f.Append(CFormat.Sprintf("\"%s\"\n", PR_UglyValueString(type, pr_globals, def.ofs)));
        }

        f.Append("}\n");
    }

    // pr_edict.c: ED_ParseGlobals
    public void ED_ParseGlobals(string data, int pos)
    {
        string keyname;
        int? key;

        while (true)
        {
            // parse key
            pos = COM_Parse(data, pos);
            if (Ch(com_token, 0) == '}')
            {
                break;
            }

            if (pos < 0)
            {
                throw Sys_Error("ED_ParseEntity: EOF without closing brace");
            }

            keyname = com_token;

            // parse value
            pos = COM_Parse(data, pos);
            if (pos < 0)
            {
                throw Sys_Error("ED_ParseEntity: EOF without closing brace");
            }

            if (Ch(com_token, 0) == '}')
            {
                throw Sys_Error("ED_ParseEntity: closing brace without data");
            }

            key = ED_FindGlobal(keyname);
            if (key == null)
            {
                Con_Printf("'%s' is not a global\n", keyname);
                continue;
            }

            if (!ED_ParseEpair(pr_globals, 0, pr_globaldefs[key.Value], com_token))
            {
                throw Host_Error("ED_ParseGlobals: parse error");
            }
        }
    }

    // ============================================================================

    // pr_edict.c: ED_NewString: a copy on the hunk with \n made a newline (and \ before anything else a \)
    public int ED_NewString(string @string)
    {
        var sb = new StringBuilder();
        int i, l;

        l = Q_strlen(@string) + 1;

        for (i = 0; i < l; i++)
        {
            if (Ch(@string, i) == '\\' && i < l - 1)
            {
                i++;
                if (Ch(@string, i) == 'n')
                {
                    sb.Append('\n');
                }
                else
                {
                    sb.Append('\\');
                }
            }
            else if (i < l - 1)
            {
                sb.Append(@string[i]);
            }
        }

        return PR_HeapString(sb.ToString());
    }

    // pr_edict.c: ED_ParseEpair
    // Can parse either fields or globals (base: the slots; mem[@base + key.ofs] is the value)
    // returns false if error
    public bool ED_ParseEpair(int[] mem, int @base, in ddef_t key, string s)
    {
        int i;
        int? def;
        dfunction_t? func;
        int d = @base + key.ofs;

        switch (DefType(key.type))
        {
            case etype_t.ev_string:
                mem[d] = ED_NewString(s);
                break;

            case etype_t.ev_float:
                mem[d] = BitConverter.SingleToInt32Bits((float)atof(s));
                break;

            case etype_t.ev_vector:
                {
                    // strcpy (string, s), then three numbers at the spaces; past the copy's end the C reads what
                    // its char[128] held before: zeros here (design.md §3.3)
                    byte[] @string = new byte[Math.Max(128, s.Length + 1) + 8];
                    Encoding.Latin1.GetBytes(s, 0, Q_strlen(s), @string, 0);
                    int v = 0, w = 0;
                    for (i = 0; i < 3; i++)
                    {
                        while (v < @string.Length - 1 && @string[v] != 0 && @string[v] != ' ')
                        {
                            v++;
                        }

                        @string[v] = 0;
                        int end = Array.IndexOf(@string, (byte)0, w);
                        mem[d + i] = BitConverter.SingleToInt32Bits((float)atof(Encoding.Latin1.GetString(@string, w, end - w)));
                        w = v = Math.Min(v + 1, @string.Length - 1);
                    }

                    break;
                }

            case etype_t.ev_entity:
                mem[d] = EDICT_TO_PROG(EDICT_NUM(atoi(s)));
                break;

            case etype_t.ev_field:
                def = ED_FindField(s);
                if (def == null)
                {
                    Con_Printf("Can't find field %s\n", s);
                    return false;
                }

                mem[d] = G_INT(pr_fielddefs[def.Value].ofs);
                break;

            case etype_t.ev_function:
                func = ED_FindFunction(s);
                if (func == null)
                {
                    Con_Printf("Can't find function %s\n", s);
                    return false;
                }

                mem[d] = func.index;
                break;

            default:
                break;
        }

        return true;
    }

    // pr_edict.c: ED_ParseEdict
    // Parses an edict out of the given string, returning the new position. ed should be a properly initialized empty
    // edict. Used for initial level load and for savegames.
    public int ED_ParseEdict(string data, int pos, Edict ent)
    {
        int? key;
        bool anglehack;
        bool init;
        string keyname;
        int n;

        init = false;

        // clear it
        if (ent != sv.edicts[0])
        {
            // hack
            Array.Clear(sv.edictmem, ent.fields, progs!.entityfields);
        }

        // go through all the dictionary pairs
        while (true)
        {
            // parse key
            pos = COM_Parse(data, pos);
            if (Ch(com_token, 0) == '}')
            {
                break;
            }

            if (pos < 0)
            {
                throw Sys_Error("ED_ParseEntity: EOF without closing brace");
            }

            // anglehack is to allow QuakeEd to write single scalar angles and allow them to be turned into vectors.
            // (FIXME...)
            if (Q_strcmp(com_token, "angle") == 0)
            {
                com_token = "angles";
                anglehack = true;
            }
            else
            {
                anglehack = false;
            }

            // FIXME: change light to _light to get rid of this hack
            if (Q_strcmp(com_token, "light") == 0)
            {
                com_token = "light_lev"; // hack for single light def
            }

            keyname = com_token;

            // another hack to fix heynames with trailing spaces
            n = Q_strlen(keyname);
            while (n != 0 && keyname[n - 1] == ' ')
            {
                n--;
            }

            keyname = keyname[..n];

            // parse value
            pos = COM_Parse(data, pos);
            if (pos < 0)
            {
                throw Sys_Error("ED_ParseEntity: EOF without closing brace");
            }

            if (Ch(com_token, 0) == '}')
            {
                throw Sys_Error("ED_ParseEntity: closing brace without data");
            }

            init = true;

            // keynames with a leading underscore are used for utility comments, and are immediately discarded by
            // quake
            if (Ch(keyname, 0) == '_')
            {
                continue;
            }

            key = ED_FindField(keyname);
            if (key == null)
            {
                Con_Printf("'%s' is not a field\n", keyname);
                continue;
            }

            if (anglehack)
            {
                string temp = com_token;
                com_token = CFormat.Sprintf("0 %s 0", temp);
            }

            if (!ED_ParseEpair(sv.edictmem, ent.fields, pr_fielddefs[key.Value], com_token))
            {
                throw Host_Error("ED_ParseEdict: parse error");
            }
        }

        if (!init)
        {
            ent.free = true;
        }

        return pos;
    }

    // pr_edict.c: ED_LoadFromFile
    // The entities are directly placed in the array, rather than allocated with ED_Alloc, because otherwise an error
    // loading the map would have entity number references out of order.
    //
    // Creates a server's entity / program execution context by parsing textual entity definitions out of an ent file.
    //
    // Used for both fresh maps and savegame loads. A fresh map would also need to call ED_CallSpawnFunctions () to
    // let the objects initialize themselves.
    public void ED_LoadFromFile(string data)
    {
        Edict? ent;
        int inhibit;
        dfunction_t? func;
        int pos = 0;

        ent = null;
        inhibit = 0;
        pr_global_struct.time = (float)sv.time;

        // parse ents
        while (true)
        {
            // parse the opening brace
            pos = COM_Parse(data, pos);
            if (pos < 0)
            {
                break;
            }

            if (Ch(com_token, 0) != '{')
            {
                throw Sys_Error("ED_LoadFromFile: found %s when expecting {", com_token);
            }

            if (ent == null)
            {
                ent = EDICT_NUM(0);
            }
            else
            {
                ent = ED_Alloc();
            }

            pos = ED_ParseEdict(data, pos, ent);

            // remove things from different skill levels or deathmatch
            if (deathmatch.value != 0)
            {
                if ((X86.FtoI(ent.v.spawnflags) & SPAWNFLAG_NOT_DEATHMATCH) != 0)
                {
                    ED_Free(ent);
                    inhibit++;
                    continue;
                }
            }
            else if ((current_skill == 0 && (X86.FtoI(ent.v.spawnflags) & SPAWNFLAG_NOT_EASY) != 0)
                || (current_skill == 1 && (X86.FtoI(ent.v.spawnflags) & SPAWNFLAG_NOT_MEDIUM) != 0)
                || (current_skill >= 2 && (X86.FtoI(ent.v.spawnflags) & SPAWNFLAG_NOT_HARD) != 0))
            {
                ED_Free(ent);
                inhibit++;
                continue;
            }

            // immediately call spawn function
            if (ent.v.classname == 0)
            {
                Con_Printf("No classname for:\n");
                ED_Print(ent);
                ED_Free(ent);
                continue;
            }

            // look for the spawn function
            func = ED_FindFunction(PR_GetString(ent.v.classname));

            if (func == null)
            {
                Con_Printf("No spawn function for:\n");
                ED_Print(ent);
                ED_Free(ent);
                continue;
            }

            pr_global_struct.self = EDICT_TO_PROG(ent);
            PR_ExecuteProgram(func.index);
        }

        Con_DPrintf("%i entities inhibited\n", inhibit);
    }

    // pr_edict.c: PR_LoadProgs
    public void PR_LoadProgs()
    {
        int i;

        // flush the non-C variable lookup cache
        for (i = 0; i < GEFV_CACHESIZE; i++)
        {
            gefvCache[i] = (gefvCache[i].pcache, "");
        }

        Crc.Init(out pr_crc);

        byte[] file = COM_LoadHunkFile("progs.dat") ?? throw Sys_Error("PR_LoadProgs: couldn't load progs.dat");
        Con_DPrintf("Programs occupy %iK.\n", com_filesize / 1024);

        for (i = 0; i < com_filesize; i++)
        {
            Crc.ProcessByte(ref pr_crc, file[i]);
        }

        ReadOnlySpan<byte> b = file;
        int Int(int at) => BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at));

        progs = new dprograms_t
        {
            version = Int(0),
            crc = Int(4),
            ofs_statements = Int(8),
            numstatements = Int(12),
            ofs_globaldefs = Int(16),
            numglobaldefs = Int(20),
            ofs_fielddefs = Int(24),
            numfielddefs = Int(28),
            ofs_functions = Int(32),
            numfunctions = Int(36),
            ofs_strings = Int(40),
            numstrings = Int(44),
            ofs_globals = Int(48),
            numglobals = Int(52),
            entityfields = Int(56),
        };

        if (progs.version != PROG_VERSION)
        {
            throw Sys_Error("progs.dat has wrong version number (%i should be %i)", progs.version, PROG_VERSION);
        }

        if (progs.crc != ProgDefs.PROGHEADER_CRC)
        {
            throw Sys_Error("progs.dat system vars have been modified, progdefs.h is out of date");
        }

        pr_functions = new dfunction_t[progs.numfunctions];
        for (i = 0; i < progs.numfunctions; i++)
        {
            int at = progs.ofs_functions + (i * 36);
            pr_functions[i] = new dfunction_t
            {
                first_statement = Int(at),
                parm_start = Int(at + 4),
                locals = Int(at + 8),
                profile = Int(at + 12),
                s_name = Int(at + 16),
                s_file = Int(at + 20),
                numparms = Int(at + 24),
                parm_size = b.Slice(at + 28, MAX_PARMS).ToArray(),
                index = i,
            };
        }

        // the string table: from ofs_strings to the end of the file (the C's pr_strings points into the loaded file,
        // so an offset past numstrings reads what follows, up to the 0 COM_LoadFile appends)
        PR_SetStringTable(file[progs.ofs_strings..]);

        pr_globaldefs = ReadDefs(b, progs.ofs_globaldefs, progs.numglobaldefs);
        pr_fielddefs = ReadDefs(b, progs.ofs_fielddefs, progs.numfielddefs);

        pr_statements = new dstatement_t[progs.numstatements];
        for (i = 0; i < progs.numstatements; i++)
        {
            int at = progs.ofs_statements + (i * 8);
            pr_statements[i] = new dstatement_t
            {
                op = BinaryPrimitives.ReadUInt16LittleEndian(b[at..]),
                a = BinaryPrimitives.ReadInt16LittleEndian(b[(at + 2)..]),
                b = BinaryPrimitives.ReadInt16LittleEndian(b[(at + 4)..]),
                c = BinaryPrimitives.ReadInt16LittleEndian(b[(at + 6)..]),
            };
        }

        pr_globals = new int[progs.numglobals];
        for (i = 0; i < progs.numglobals; i++)
        {
            pr_globals[i] = Int(progs.ofs_globals + (i * 4));
        }

        pr_global_struct = new GlobalVars(pr_globals, 0);

        pr_edict_size = (progs.entityfields * 4) + Edict.HeaderSize;

        for (i = 0; i < progs.numfielddefs; i++)
        {
            if ((pr_fielddefs[i].type & DEF_SAVEGLOBAL) != 0)
            {
                throw Sys_Error("PR_LoadProgs: pr_fielddefs[i].type & DEF_SAVEGLOBAL");
            }
        }
    }

    private static ddef_t[] ReadDefs(ReadOnlySpan<byte> b, int ofs, int count)
    {
        var defs = new ddef_t[count];
        for (int i = 0; i < count; i++)
        {
            int at = ofs + (i * 8);
            defs[i] = new ddef_t
            {
                type = BinaryPrimitives.ReadUInt16LittleEndian(b[at..]),
                ofs = BinaryPrimitives.ReadUInt16LittleEndian(b[(at + 2)..]),
                s_name = BinaryPrimitives.ReadInt32LittleEndian(b[(at + 4)..]),
            };
        }

        return defs;
    }

    // pr_edict.c: PR_Init
    public void PR_Init()
    {
        Cmd_AddCommand("edict", ED_PrintEdict_f);
        Cmd_AddCommand("edicts", ED_PrintEdicts);
        Cmd_AddCommand("edictcount", ED_Count);
        Cmd_AddCommand("profile", PR_Profile_f);
        Cvar_RegisterVariable(nomonsters);
        Cvar_RegisterVariable(gamecfg);
        Cvar_RegisterVariable(scratch1);
        Cvar_RegisterVariable(scratch2);
        Cvar_RegisterVariable(scratch3);
        Cvar_RegisterVariable(scratch4);
        Cvar_RegisterVariable(savedgamecfg);
        Cvar_RegisterVariable(saved1);
        Cvar_RegisterVariable(saved2);
        Cvar_RegisterVariable(saved3);
        Cvar_RegisterVariable(saved4);
    }
}
