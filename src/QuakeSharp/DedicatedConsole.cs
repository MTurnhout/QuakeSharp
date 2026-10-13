using System.Collections.Concurrent;

/// <summary>
/// sys_win.c's dedicated server console (AllocConsole's): its text written to standard output as bytes, and the keys
/// typed for Sys_ConsoleInput, which edits and echoes them itself. In a terminal the keys come one by one, unechoed
/// (WinQuake takes their releases, a terminal gives their presses: each a character, Enter '\r', Backspace '\b');
/// from redirected input, its bytes, each line ended by Enter.
/// </summary>
internal sealed class DedicatedConsole : IDisposable
{
    private readonly Stream output = Console.OpenStandardOutput();
    private readonly ConcurrentQueue<byte> piped = new();
    private readonly bool redirected = Console.IsInputRedirected;

    public DedicatedConsole()
    {
        if (redirected)
        {
            var reader = new Thread(ReadInput) { IsBackground = true, Name = "stdin" };
            reader.Start();
        }
    }

    // WriteFile (houtput, ...)
    public void Write(ReadOnlySpan<byte> text)
    {
        output.Write(text);
        output.Flush();
    }

    // GetNumberOfConsoleInputEvents and ReadConsoleInput: the next key's character, false when none waits
    public bool Read(out byte ch)
    {
        if (redirected)
        {
            return piped.TryDequeue(out ch);
        }

        while (Console.KeyAvailable)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            char c = key.Key switch
            {
                ConsoleKey.Enter => '\r',
                ConsoleKey.Backspace => '\b',
                _ => key.KeyChar,
            };

            // the console's characters as bytes (Latin-1); a key without one is passed over, as WinQuake's are
            if (c != '\0' && c <= '\xff')
            {
                ch = (byte)c;
                return true;
            }
        }

        ch = 0;
        return false;
    }

    public void Dispose() => output.Dispose();

    // redirected input: its bytes as typed, "\n" or "\r\n" as Enter
    private void ReadInput()
    {
        using Stream input = Console.OpenStandardInput();
        int b;
        while ((b = input.ReadByte()) >= 0)
        {
            if (b == '\n')
            {
                piped.Enqueue((byte)'\r');
            }
            else if (b != '\r')
            {
                piped.Enqueue((byte)b);
            }
        }
    }
}
