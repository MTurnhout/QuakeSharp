using QuakeSharp.Core.Common;

namespace QuakeSharp.Core;

// common.c: COM_Init, and wad.c's gfx.wad
public sealed partial class QuakeEngine
{
    // common.c: registered, cmdline
    public readonly Cvar registered = new("registered", "0");
    public readonly Cvar cmdline = new("cmdline", "0", false, true);

    /// <summary>wad.c: wad_base and wad_lumps (gfx.wad)</summary>
    public Wad? wad;

    // common.c: COM_Init (byte order needs no setting up: the data is little-endian, as the machines .NET runs on)
    public void COM_Init(string basedir)
    {
        Cvar_RegisterVariable(registered);
        Cvar_RegisterVariable(cmdline);
        Cmd_AddCommand("path", fileSystem.Path_f);

        fileSystem.InitFilesystem(basedir);
        COM_CheckRegistered();
    }

    // common.c: COM_CheckRegistered: FileSystem checks gfx/pop.lmp; the cvars are set here
    public void COM_CheckRegistered()
    {
        if (fileSystem.CheckRegistered())
        {
            Cvar_Set("cmdline", com_argv.Cmdline);
            Cvar_Set("registered", "1");
        }
    }

    // wad.c: W_LoadWadFile
    public void W_LoadWadFile(string filename) => wad = Wad.LoadWadFile(fileSystem, filename);
}
