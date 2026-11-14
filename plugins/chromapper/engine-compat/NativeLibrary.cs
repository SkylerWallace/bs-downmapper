using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BsDownmap.Engine
{
    internal static class NativeLibrary
    {
        internal static IntPtr Load(string path)
        {
            IntPtr handle = LoadLibrary(path, IntPtr.Zero, 0x1100);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load " + path);
            return handle;
        }

        internal static IntPtr GetExport(IntPtr handle, string name)
        {
            IntPtr address = GetProcAddress(handle, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return address;
        }

        internal static void Free(IntPtr handle) => FreeLibrary(handle);

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr LoadLibrary(string path, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr handle, string name);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr handle);
    }
}
