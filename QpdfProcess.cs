using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PdfPageSplitter
{
    /// <summary>为 qpdf 提供原始标准输入管道，避免 Framework StreamWriter 自动写入 BOM。</summary>
    internal sealed class QpdfProcess : IDisposable
    {
        private const uint CreateNoWindow = 0x08000000;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const int StartUseStdHandles = 0x00000100;
        private const int HandleListAttribute = 0x00020002;
        private SafeWaitHandle processHandle;
        private AnonymousPipeServerStream input;
        private AnonymousPipeServerStream output;
        private AnonymousPipeServerStream error;

        public int Id { get; private set; }
        public Stream StandardInput { get { return input; } }
        public StreamReader StandardOutput { get; private set; }
        public StreamReader StandardError { get; private set; }
        public bool HasExited { get { return WaitForExit(0); } }
        public int ExitCode
        {
            get
            {
                uint code;
                if (!GetExitCodeProcess(processHandle, out code)) throw new Win32Exception();
                return unchecked((int)code);
            }
        }

        public static QpdfProcess Start(string exe, string commandLine, string workingDirectory)
        {
            var process = new QpdfProcess();
            try
            {
                process.input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                process.output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                process.error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                process.StandardOutput = new StreamReader(process.output, Encoding.UTF8);
                process.StandardError = new StreamReader(process.error, Encoding.UTF8);
                process.StartNative(exe, commandLine, workingDirectory);
                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        private void StartNative(string exe, string commandLine, string workingDirectory)
        {
            var startup = new StartupInfoEx();
            startup.Info.Size = Marshal.SizeOf(typeof(StartupInfoEx));
            startup.Info.Flags = StartUseStdHandles;
            startup.Info.StandardInput = input.ClientSafePipeHandle.DangerousGetHandle();
            startup.Info.StandardOutput = output.ClientSafePipeHandle.DangerousGetHandle();
            startup.Info.StandardError = error.ClientSafePipeHandle.DangerousGetHandle();
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero) throw new Win32Exception();
            IntPtr handles = IntPtr.Zero;
            bool initialized = false;
            startup.Attributes = Marshal.AllocHGlobal(size);
            try
            {
                if (!InitializeProcThreadAttributeList(startup.Attributes, 1, 0, ref size)) throw new Win32Exception();
                initialized = true;
                handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
                Marshal.WriteIntPtr(handles, 0, startup.Info.StandardInput);
                Marshal.WriteIntPtr(handles, IntPtr.Size, startup.Info.StandardOutput);
                Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, startup.Info.StandardError);
                // Each child inherits only its own pipe ends, including during concurrent page reads.
                if (!UpdateProcThreadAttribute(startup.Attributes, 0, new IntPtr(HandleListAttribute),
                    handles, new IntPtr(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                ProcessInformation result;
                if (!CreateProcess(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
                    CreateNoWindow | ExtendedStartupInfoPresent, IntPtr.Zero, workingDirectory, ref startup, out result))
                    throw new Win32Exception();
                processHandle = new SafeWaitHandle(result.Process, true);
                CloseHandle(result.Thread);
                Id = result.ProcessId;
            }
            finally
            {
                // The handle list's value storage must remain alive until the list is deleted.
                if (initialized) DeleteProcThreadAttributeList(startup.Attributes);
                Marshal.FreeHGlobal(startup.Attributes);
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                input.DisposeLocalCopyOfClientHandle();
                output.DisposeLocalCopyOfClientHandle();
                error.DisposeLocalCopyOfClientHandle();
            }
        }

        public bool WaitForExit(int milliseconds)
        {
            uint result = WaitForSingleObject(processHandle, unchecked((uint)milliseconds));
            if (result == UInt32.MaxValue) throw new Win32Exception();
            return result == 0;
        }

        public void WaitForExit() { WaitForExit(-1); }

        public void Kill()
        {
            if (!TerminateProcess(processHandle, 1) && !HasExited) throw new Win32Exception();
        }

        public void Dispose()
        {
            if (StandardOutput != null) StandardOutput.Dispose();
            if (StandardError != null) StandardError.Dispose();
            if (input != null) input.Dispose();
            if (output != null) output.Dispose();
            if (error != null) error.Dispose();
            if (processHandle != null) processHandle.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved, Desktop, Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, ReservedSize;
            public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo Info;
            public IntPtr Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process, Thread;
            public int ProcessId, ThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
            IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
            IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(SafeWaitHandle process, uint exitCode);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
