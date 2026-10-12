namespace QuakeSharp.Core;

/// <summary>
/// A fatal engine error. Replaces sys_win.c: Sys_Error, which showed the message and quit.
/// </summary>
public sealed class QuakeException : Exception
{
    public QuakeException(string message)
        : base(message)
    {
    }

    public QuakeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
