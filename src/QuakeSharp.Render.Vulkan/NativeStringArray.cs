using System.Runtime.InteropServices;

namespace QuakeSharp.Render.Vulkan;

/// <summary>A null-terminated UTF-8 string array in native memory (for ppEnabledExtensionNames and the like).</summary>
internal sealed unsafe class NativeStringArray : IDisposable
{
    public NativeStringArray(IReadOnlyList<string> strings)
    {
        Count = (uint)strings.Count;
        Pointer = (byte**)NativeMemory.Alloc((nuint)Math.Max(strings.Count, 1), (nuint)sizeof(byte*));
        for (int i = 0; i < strings.Count; i++)
        {
            Pointer[i] = (byte*)Marshal.StringToCoTaskMemUTF8(strings[i]);
        }
    }

    public byte** Pointer { get; }

    public uint Count { get; }

    public void Dispose()
    {
        for (int i = 0; i < Count; i++)
        {
            Marshal.FreeCoTaskMem((nint)Pointer[i]);
        }

        NativeMemory.Free(Pointer);
    }
}
