using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AiAssistant.Engine.SkeletonEngine.TreeSitter;

public static class NativeLoader
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr hModule);

    public static IntPtr Load(string dllPath)
    {
        var handle = LoadLibrary(dllPath);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Failed to load native library: {dllPath} (error {error})");
        }
        return handle;
    }

    public static T GetFunction<T>(IntPtr module, string procName) where T : Delegate
    {
        var ptr = GetProcAddress(module, procName);
        if (ptr == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException($"Native export '{procName}' not found in module.");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    public static void Free(IntPtr module)
    {
        if (module != IntPtr.Zero)
        {
            FreeLibrary(module);
        }
    }
}
