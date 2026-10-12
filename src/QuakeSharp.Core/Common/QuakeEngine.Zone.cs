namespace QuakeSharp.Core;

// zone.c: the memory allocator. QuakeSharp uses managed memory (design.md §3, §6.1): what remains is what the game
// sees of it, the "flush" command.
public sealed partial class QuakeEngine
{
    // zone.c: Memory_Init (allocates nothing here; registers the cache's command, as the C does first in Host_Init)
    public void Memory_Init() => Cmd_AddCommand("flush", Cache_Flush);

    // zone.c: Cache_Flush (throw everything out, so new data will be demand cached). Nothing is cached here: the data
    // stays loaded, as if the cache never filled.
    public void Cache_Flush()
    {
    }

    // zone.c: Cache_Report, Hunk_Check (nothing to report or check)
    public void Cache_Report()
    {
    }

    public void Hunk_Check()
    {
    }
}
