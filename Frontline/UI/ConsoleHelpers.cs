using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Frontline.UI;

internal static partial class ConsoleHelpers
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    internal static void AllocateConsoleIfNeeded()
    {
        // If a console is already attached (e.g. started from cmd.exe), do nothing
        if (GetConsoleWindow() != IntPtr.Zero)
            return; // we already have one

        // GUI subsystem ➜ create a fresh console
        if (!AllocConsole())
            return; // couldn't allocate – bail silently

        RewireStdStreams();
        Console.OutputEncoding = Encoding.UTF8;
    }

    private static void RewireStdStreams()
    {
        var stdout = OpenConsoleFile("CONOUT$", FileAccess.Write, FileShare.Write, 0x40000000); // GENERIC_WRITE
        var stdin = OpenConsoleFile("CONIN$", FileAccess.Read, FileShare.Read, 0x80000000); // GENERIC_READ
        var stderr = OpenConsoleFile("CONOUT$", FileAccess.Write, FileShare.Write, 0x40000000);

        Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
        Console.SetError(new StreamWriter(stderr) { AutoFlush = true });
        Console.SetIn(new StreamReader(stdin));
        return;

        // reopen CONIN$ / CONOUT$ and hook them into System.Console
        static FileStream OpenConsoleFile(string name, FileAccess access, FileShare share, uint desiredAccess)
        {
            const uint openExisting = 3;
            var handle = CreateFile(name, desiredAccess, (uint)share, IntPtr.Zero,
                openExisting, 0, IntPtr.Zero);

            return new FileStream(new SafeFileHandle(handle, true), access);
        }
    }
}