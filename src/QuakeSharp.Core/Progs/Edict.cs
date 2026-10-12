using System.Runtime.CompilerServices;
using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Progs;

/// <summary>
/// progs.h: edict_t, a server entity. Its fields (ed->v and the progs' own after them) are slots of the server's edict
/// memory (sv.edictmem): edict n's start at n * pr_edict_size bytes plus the 32-bit C's 96-byte header, so QuakeC's
/// entity values and field pointers are the C's byte offsets (design.md §11.1). The header's contents are kept here.
/// </summary>
public sealed class Edict
{
    // progs.h: MAX_ENT_LEAFS
    public const int MAX_ENT_LEAFS = 16;

    /// <summary>sizeof(edict_t) - sizeof(entvars_t) in the 32-bit C: free, area, num_leafs, leafnums, baseline, freetime</summary>
    public const int HeaderSize = 96;

    public Edict(int num, int[] mem, int fields)
    {
        this.num = num;
        this.fields = fields;
        area = new Link(this);
        v = new EntVars(mem, fields);
    }

    public bool free;

    /// <summary>linked to a division node or leaf</summary>
    public readonly Link area;

    public int num_leafs;

    public readonly short[] leafnums = new short[MAX_ENT_LEAFS];

    public EntityState baseline;

    /// <summary>sv.time when the object was freed</summary>
    public float freetime;

    /// <summary>C exported fields from progs (other fields from progs come immediately after)</summary>
    public readonly EntVars v;

    /// <summary>Its number (NUM_FOR_EDICT without the check).</summary>
    public readonly int num;

    /// <summary>The index of its first field slot in the edict memory.</summary>
    public readonly int fields;
}

/// <summary>common.h: link_t, a node of a doubly linked list (an edict's area link, or a list's head)</summary>
public sealed class Link
{
    public Link(Edict? owner = null)
    {
        this.owner = owner;
    }

    public Link? prev, next;

    /// <summary>STRUCT_FROM_LINK: the edict whose area this is (null for a list's head)</summary>
    public readonly Edict? owner;
}
