using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsPowerShellRc;

public sealed class EngineUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

// PowerShell's SDK redirects native stdin only for commands expecting pipeline input.
// Isolate the OS handle too, while no approval prompt is active. No private SDK APIs.
internal sealed class NativeInputScope : IDisposable
{
    internal static readonly object Gate = new();
    private readonly IntPtr originalHandle;
    private readonly SafeFileHandle? nullHandle;
    private readonly int originalFd = -1;

    public NativeInputScope()
    {
        if (OperatingSystem.IsWindows())
        {
            originalHandle = GetStdHandle(-10);
            nullHandle = CreateFileW("NUL", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (nullHandle.IsInvalid || !SetStdHandle(-10, nullHandle.DangerousGetHandle()))
            {
                var error = new Win32Exception(Marshal.GetLastPInvokeError());
                nullHandle.Dispose();
                throw new EngineUnavailableException("Cannot isolate native stdin; restart required.", error);
            }
        }
        else
        {
            originalFd = dup(0);
            int nullFd = open("/dev/null", 0);
            if (originalFd < 0 || nullFd < 0 || dup2(nullFd, 0) < 0)
            {
                var error = new Win32Exception(Marshal.GetLastPInvokeError());
                if (nullFd >= 0) close(nullFd);
                if (originalFd >= 0) close(originalFd);
                throw new EngineUnavailableException("Cannot isolate native stdin; restart required.", error);
            }
            close(nullFd);
        }
    }

    public void Dispose()
    {
        bool restored;
        if (OperatingSystem.IsWindows())
        {
            restored = SetStdHandle(-10, originalHandle);
            nullHandle!.Dispose();
        }
        else { restored = dup2(originalFd, 0) >= 0; close(originalFd); }
        if (!restored) throw new EngineUnavailableException("Cannot restore console stdin; execution disabled, restart required.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetStdHandle(int handle, IntPtr value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("libc", SetLastError = true)] private static extern int dup(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int dup2(int oldFd, int newFd);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
}
