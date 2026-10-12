using System.Text;
using QuakeSharp.Core.Common;
using static QuakeSharp.Core.Common.QString;

namespace QuakeSharp.Core;

// cvar.c: dynamic variable tracking
public sealed partial class QuakeEngine
{
    /// <summary>cvar.c: cvar_vars, the registered variables, newest first</summary>
    public Cvar? cvar_vars;

    // cvar.c: Cvar_FindVar
    public Cvar? Cvar_FindVar(string var_name)
    {
        for (Cvar? var = cvar_vars; var != null; var = var.next)
        {
            if (Q_strcmp(var_name, var.name) == 0)
            {
                return var;
            }
        }

        return null;
    }

    // cvar.c: Cvar_VariableValue
    public float Cvar_VariableValue(string var_name)
    {
        Cvar? var = Cvar_FindVar(var_name);
        if (var == null)
        {
            return 0;
        }

        return Q_atof(var.@string);
    }

    // cvar.c: Cvar_VariableString
    public string Cvar_VariableString(string var_name)
    {
        Cvar? var = Cvar_FindVar(var_name);
        if (var == null)
        {
            return "";
        }

        return var.@string;
    }

    // cvar.c: Cvar_CompleteVariable
    public string? Cvar_CompleteVariable(string partial)
    {
        int len = Q_strlen(partial);

        if (len == 0)
        {
            return null;
        }

        // check functions
        for (Cvar? cvar = cvar_vars; cvar != null; cvar = cvar.next)
        {
            if (Q_strncmp(partial, cvar.name, len) == 0)
            {
                return cvar.name;
            }
        }

        return null;
    }

    // cvar.c: Cvar_Set
    public void Cvar_Set(string var_name, string value)
    {
        bool changed;

        Cvar? var = Cvar_FindVar(var_name);
        if (var == null)
        {
            // there is an error in C code if this happens
            Con_Printf("Cvar_Set: variable %s not found\n", var_name);
            return;
        }

        changed = Q_strcmp(var.@string, value) != 0;

        var.@string = value[..Q_strlen(value)];
        var.value = Q_atof(var.@string);
        if (var.server && changed)
        {
            if (sv.active)
            {
                SV_BroadcastPrintf("\"%s\" changed to \"%s\"\n", var.name, var.@string);
            }
        }
    }

    // cvar.c: Cvar_SetValue
    public void Cvar_SetValue(string var_name, float value) => Cvar_Set(var_name, va("%f", value));

    // cvar.c: Cvar_RegisterVariable
    // Adds a freestanding variable to the variable list.
    public void Cvar_RegisterVariable(Cvar variable)
    {
        // first check to see if it has allready been defined
        if (Cvar_FindVar(variable.name) != null)
        {
            Con_Printf("Can't register variable %s, allready defined\n", variable.name);
            return;
        }

        // check for overlap with a command
        if (Cmd_Exists(variable.name))
        {
            Con_Printf("Cvar_RegisterVariable: %s is a command\n", variable.name);
            return;
        }

        // copy the value off, because future sets will Z_Free it
        variable.@string = variable.@string[..Q_strlen(variable.@string)];
        variable.value = Q_atof(variable.@string);

        // link the variable in
        variable.next = cvar_vars;
        cvar_vars = variable;
    }

    // cvar.c: Cvar_Command
    // Handles variable inspection and changing from the console
    public bool Cvar_Command()
    {
        // check variables
        Cvar? v = Cvar_FindVar(Cmd_Argv(0));
        if (v == null)
        {
            return false;
        }

        // perform a variable print or set
        if (Cmd_Argc() == 1)
        {
            Con_Printf("\"%s\" is \"%s\"\n", v.name, v.@string);
            return true;
        }

        Cvar_Set(v.name, Cmd_Argv(1));
        return true;
    }

    // cvar.c: Cvar_WriteVariables
    // Writes lines containing "set variable value" for all variables with the archive flag set to true.
    public void Cvar_WriteVariables(StringBuilder f)
    {
        for (Cvar? var = cvar_vars; var != null; var = var.next)
        {
            if (var.archive)
            {
                f.Append(va("%s \"%s\"\n", var.name, var.@string));
            }
        }
    }
}
