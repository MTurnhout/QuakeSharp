namespace QuakeSharp.Core.Common;

/// <summary>cvar.h: cvar_t, a console variable. Defined by the module that owns it, registered at init.</summary>
public sealed class Cvar
{
    public Cvar(string name, string @string, bool archive = false, bool server = false)
    {
        this.name = name;
        this.@string = @string;
        this.archive = archive;
        this.server = server;
    }

    public string name;

    public string @string;

    /// <summary>set to true to cause it to be saved to vars.rc</summary>
    public bool archive;

    /// <summary>notifies players when changed</summary>
    public bool server;

    public float value;

    public Cvar? next;
}
