using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfPageSplitter;

public static class QpdfProcessTest
{
    private static readonly string[] Passwords = { "", "ascii & %d \" password", "中文 test & password", "\uFEFF密码" };

    [DllImport("kernel32.dll")] private static extern uint GetConsoleCP();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    private static void RunChild(int index)
    {
        using (var bytes = new MemoryStream())
        {
            Console.OpenStandardInput().CopyTo(bytes);
            byte[] expected = new UTF8Encoding(false).GetBytes(Passwords[index] + "\n");
            if (!bytes.ToArray().SequenceEqual(expected))
                throw new Exception("Password stdin bytes differ from the expected BOM-free UTF-8 payload.");
        }
        // Exercise both output pipes beyond the pipe buffer capacity.
        Console.Out.Write(new string('o', 16384));
        Console.Error.Write(new string('e', 16384));
    }

    private static void CheckPipes()
    {
        Encoding encoding = Console.InputEncoding;
        TextReader reader = Console.In;
        MethodInfo run = typeof(Splitter).Assembly.GetType("PdfPageSplitter.QpdfRunner").GetMethod("Run");
        string exe = Assembly.GetExecutingAssembly().Location;
        Task.WaitAll(Enumerable.Range(0, Passwords.Length).Select(index => Task.Run(() => {
            object result;
            try
            {
                result = run.Invoke(null, new object[] { exe, new[] { "--capture", index.ToString() },
                    Passwords[index], CancellationToken.None, AppDomain.CurrentDomain.BaseDirectory });
            }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
            Type type = result.GetType();
            if ((string)type.GetField("Output").GetValue(result) != new string('o', 16384) ||
                (string)type.GetField("Warnings").GetValue(result) != new string('e', 16384))
                throw new Exception("Concurrent stdout/stderr were truncated or mixed between children.");
        })).ToArray());
        if (!Object.ReferenceEquals(encoding, Console.InputEncoding) || !Object.ReferenceEquals(reader, Console.In))
            throw new Exception("qpdf changed the host's console encoding or input reader.");
        Console.WriteLine("PASS: exact password bytes, EOF, concurrent pipes and unchanged console state (CP " + encoding.CodePage + ")");
    }

    public static int Main(string[] args)
    {
        using (var watchdog = new System.Threading.Timer(delegate {
            Console.Error.WriteLine("QpdfProcessTest timed out.");
            Environment.Exit(1);
        }, null, TimeSpan.FromSeconds(30), System.Threading.Timeout.InfiniteTimeSpan))
        {
            return Run(args);
        }
    }

    private static int Run(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--capture") { RunChild(Int32.Parse(args[1])); return 0; }
            if (args.Length > 0 && args[0] == "--detached")
            {
                FreeConsole();
                if (GetConsoleCP() != 0) throw new Exception("Expected a process without a console.");
                CheckPipes();
                return 0;
            }
            Encoding original = Console.InputEncoding;
            try
            {
                foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true), Encoding.GetEncoding(936) })
                {
                    Console.InputEncoding = encoding;
                    CheckPipes();
                }
            }
            finally { Console.InputEncoding = original; }
            var info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--detached") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var child = Process.Start(info))
            {
                Task<string> output = child.StandardOutput.ReadToEndAsync(), error = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(15000)) { child.Kill(); throw new Exception("Detached pipe test timed out."); }
                Task.WaitAll(output, error);
                if (child.ExitCode != 0) throw new Exception("Detached pipe test failed: " + error.Result);
            }
            Console.WriteLine("PASS: password pipes without an attached console");
            return 0;
        }
        catch (Exception ex) { TestDiagnostics.Report(ex); return 1; }
    }
}
