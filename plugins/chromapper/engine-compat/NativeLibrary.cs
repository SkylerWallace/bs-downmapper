using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BsDownmap.Engine
{
    internal static class NativeLibrary
    {
        private static bool IsWindows => System.IO.Path.DirectorySeparatorChar == '\\';

        internal static IntPtr Load(string path)
        {
            if (!IsWindows)
            {
                IntPtr library = dlopen(path, 2); // RTLD_NOW
                if (library == IntPtr.Zero)
                    throw new DllNotFoundException($"Could not load {path}: {Marshal.PtrToStringAnsi(dlerror())}");
                return library;
            }

            IntPtr handle = LoadLibrary(path, IntPtr.Zero, 0x1100);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load " + path);
            return handle;
        }

        internal static IntPtr GetExport(IntPtr handle, string name)
        {
            IntPtr address = IsWindows ? GetProcAddress(handle, name) : dlsym(handle, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return address;
        }

        internal static void Free(IntPtr handle)
        {
            if (IsWindows) FreeLibrary(handle);
            else dlclose(handle);
        }

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string path, int flags);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string name);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        [DllImport("libdl.so.2")]
        private static extern int dlclose(IntPtr handle);

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr LoadLibrary(string path, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr handle, string name);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr handle);
    }
}
