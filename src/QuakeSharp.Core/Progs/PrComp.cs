namespace QuakeSharp.Core.Progs;

/// <summary>pr_comp.h: etype_t, the types of QuakeC's definitions</summary>
public enum etype_t
{
    ev_void,
    ev_string,
    ev_float,
    ev_vector,
    ev_entity,
    ev_field,
    ev_function,
    ev_pointer,
}

/// <summary>pr_comp.h: the constants shared by Quake and qcc, and the opcodes.</summary>
public static class PrComp
{
    public const int OFS_NULL = 0;
    public const int OFS_RETURN = 1;
    public const int OFS_PARM0 = 4; // leave 3 ofs for each parm to hold vectors
    public const int OFS_PARM1 = 7;
    public const int OFS_PARM2 = 10;
    public const int OFS_PARM3 = 13;
    public const int OFS_PARM4 = 16;
    public const int OFS_PARM5 = 19;
    public const int OFS_PARM6 = 22;
    public const int OFS_PARM7 = 25;
    public const int RESERVED_OFS = 28;

    public const int OP_DONE = 0;
    public const int OP_MUL_F = 1;
    public const int OP_MUL_V = 2;
    public const int OP_MUL_FV = 3;
    public const int OP_MUL_VF = 4;
    public const int OP_DIV_F = 5;
    public const int OP_ADD_F = 6;
    public const int OP_ADD_V = 7;
    public const int OP_SUB_F = 8;
    public const int OP_SUB_V = 9;

    public const int OP_EQ_F = 10;
    public const int OP_EQ_V = 11;
    public const int OP_EQ_S = 12;
    public const int OP_EQ_E = 13;
    public const int OP_EQ_FNC = 14;

    public const int OP_NE_F = 15;
    public const int OP_NE_V = 16;
    public const int OP_NE_S = 17;
    public const int OP_NE_E = 18;
    public const int OP_NE_FNC = 19;

    public const int OP_LE = 20;
    public const int OP_GE = 21;
    public const int OP_LT = 22;
    public const int OP_GT = 23;

    public const int OP_LOAD_F = 24;
    public const int OP_LOAD_V = 25;
    public const int OP_LOAD_S = 26;
    public const int OP_LOAD_ENT = 27;
    public const int OP_LOAD_FLD = 28;
    public const int OP_LOAD_FNC = 29;

    public const int OP_ADDRESS = 30;

    public const int OP_STORE_F = 31;
    public const int OP_STORE_V = 32;
    public const int OP_STORE_S = 33;
    public const int OP_STORE_ENT = 34;
    public const int OP_STORE_FLD = 35;
    public const int OP_STORE_FNC = 36;

    public const int OP_STOREP_F = 37;
    public const int OP_STOREP_V = 38;
    public const int OP_STOREP_S = 39;
    public const int OP_STOREP_ENT = 40;
    public const int OP_STOREP_FLD = 41;
    public const int OP_STOREP_FNC = 42;

    public const int OP_RETURN = 43;
    public const int OP_NOT_F = 44;
    public const int OP_NOT_V = 45;
    public const int OP_NOT_S = 46;
    public const int OP_NOT_ENT = 47;
    public const int OP_NOT_FNC = 48;
    public const int OP_IF = 49;
    public const int OP_IFNOT = 50;
    public const int OP_CALL0 = 51;
    public const int OP_CALL1 = 52;
    public const int OP_CALL2 = 53;
    public const int OP_CALL3 = 54;
    public const int OP_CALL4 = 55;
    public const int OP_CALL5 = 56;
    public const int OP_CALL6 = 57;
    public const int OP_CALL7 = 58;
    public const int OP_CALL8 = 59;
    public const int OP_STATE = 60;
    public const int OP_GOTO = 61;
    public const int OP_AND = 62;
    public const int OP_OR = 63;

    public const int OP_BITAND = 64;
    public const int OP_BITOR = 65;

    /// <summary>if the DEF_SAVEGLOBAL bit is set, the variable needs to be saved in savegames</summary>
    public const int DEF_SAVEGLOBAL = 1 << 15;

    public const int MAX_PARMS = 8;

    public const int PROG_VERSION = 6;
}

/// <summary>pr_comp.h: dstatement_t</summary>
public struct dstatement_t
{
    public ushort op;
    public short a, b, c;
}

/// <summary>pr_comp.h: ddef_t, a global or a field</summary>
public struct ddef_t
{
    /// <summary>if the DEF_SAVEGLOBAL bit is set, the variable needs to be saved in savegames</summary>
    public ushort type;

    public ushort ofs;

    public int s_name;
}

/// <summary>pr_comp.h: dfunction_t</summary>
public sealed class dfunction_t
{
    /// <summary>negative numbers are builtins</summary>
    public int first_statement;

    public int parm_start;

    /// <summary>total ints of parms + locals</summary>
    public int locals;

    /// <summary>runtime</summary>
    public int profile;

    public int s_name;

    /// <summary>source file defined in</summary>
    public int s_file;

    public int numparms;

    public byte[] parm_size = new byte[PrComp.MAX_PARMS];

    /// <summary>Its index in pr_functions (func - pr_functions).</summary>
    public int index;
}

/// <summary>pr_comp.h: dprograms_t, progs.dat's header</summary>
public sealed class dprograms_t
{
    public int version;

    /// <summary>check of header file</summary>
    public int crc;

    public int ofs_statements;

    /// <summary>statement 0 is an error</summary>
    public int numstatements;

    public int ofs_globaldefs;
    public int numglobaldefs;

    public int ofs_fielddefs;
    public int numfielddefs;

    public int ofs_functions;

    /// <summary>function 0 is an empty</summary>
    public int numfunctions;

    public int ofs_strings;

    /// <summary>first string is a null string</summary>
    public int numstrings;

    public int ofs_globals;
    public int numglobals;

    public int entityfields;
}
