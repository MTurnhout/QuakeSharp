namespace QuakeSharp.Core;

// pr_cmds.c: the builtins, the engine's functions QuakeC calls
public sealed partial class QuakeEngine
{
    /// <summary>pr_cmds.c: pr_string_temp (char[128]), where ftos, vtos and etos write</summary>
    public string pr_string_temp = "";
}
