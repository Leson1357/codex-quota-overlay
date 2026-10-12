using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// .NET Framework lacks ProcessStartInfo.StandardInputEncoding. Own the pipes so that
// process creation cannot inject a console-dependent BOM before the JSON protocol.
internal sealed class WindowsStdio : IDisposable
{
    private Process process;
    private FileStream input;
    private StreamReader output, errors;
    public int Id { get { return process.Id; } }
    public bool HasExited { get { return process.HasExited; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public short Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, int flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    public WindowsStdio(string executable, Action<WindowsStdio, string> onLine)
    {
        IntPtr childInput = IntPtr.Zero, parentInput = IntPtr.Zero, parentOutput = IntPtr.Zero,
            childOutput = IntPtr.Zero, parentError = IntPtr.Zero, childError = IntPtr.Zero;
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Inherit = true };
        try
        {
            Check(CreatePipe(out childInput, out parentInput, ref attributes, 0));
            Check(CreatePipe(out parentOutput, out childOutput, ref attributes, 0));
            Check(CreatePipe(out parentError, out childError, ref attributes, 0));
            Check(SetHandleInformation(parentInput, 1, 0));
            Check(SetHandleInformation(parentOutput, 1, 0));
            Check(SetHandleInformation(parentError, 1, 0));
            var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Flags = 0x100,
                Input = childInput, Output = childOutput, Error = childError };
            ProcessInformation information;
            Check(CreateProcess(executable, new StringBuilder("\"" + executable + "\" app-server --stdio"),
                IntPtr.Zero, IntPtr.Zero, true, 0x08000000, IntPtr.Zero, null, ref startup, out information));
            try { process = Process.GetProcessById(information.ProcessId); }
            finally { CloseHandle(information.Thread); CloseHandle(information.Process); }
            input = OwnPipe(ref parentInput, FileAccess.Write);
            output = new StreamReader(OwnPipe(ref parentOutput, FileAccess.Read), new UTF8Encoding(false));
            errors = new StreamReader(OwnPipe(ref parentError, FileAccess.Read), new UTF8Encoding(false));
        }
        catch { Dispose(); throw; }
        finally
        {
            foreach (var handle in new[] { childInput, parentInput, parentOutput, childOutput, parentError, childError })
                if (handle != IntPtr.Zero) CloseHandle(handle);
        }
        // Both streams must be drained; diagnostics are discarded rather than persisted.
        Pump(output, onLine);
        Pump(errors, null);
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    private static FileStream OwnPipe(ref IntPtr rawHandle, FileAccess access)
    {
        var handle = new SafeFileHandle(rawHandle, true);
        rawHandle = IntPtr.Zero;
        try { return new FileStream(handle, access); }
        catch { handle.Dispose(); throw; }
    }

    private async void Pump(StreamReader reader, Action<WindowsStdio, string> onLine)
    {
        try
        {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                if (onLine != null) onLine(this, line);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void WriteLine(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message + "\n");
        input.Write(bytes, 0, bytes.Length);
        input.Flush();
    }

    public void Dispose()
    {
        try
        {
            if (input != null) { input.Dispose(); input = null; }
            if (process != null && !process.HasExited && !process.WaitForExit(250))
            {
                process.Kill();
                process.WaitForExit(250);
            }
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        finally
        {
            if (output != null) { output.Dispose(); output = null; }
            if (errors != null) { errors.Dispose(); errors = null; }
            if (process != null) { process.Dispose(); process = null; }
        }
    }
}
