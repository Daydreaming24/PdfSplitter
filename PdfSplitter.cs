using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 版本号与 app.manifest 中的 assemblyIdentity 保持一致。
[assembly: AssemblyTitle("PDF 分片工具")]
[assembly: AssemblyProduct("PdfSplitter")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Chen Lingshi")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace PdfPageSplitter
{
    /// <summary>
    /// 程序入口。带 <c>--split</c> 时按命令行批处理运行，否则打开主窗口。
    /// 这里只负责选择运行方式和承接最外层的错误，具体解析交给 <see cref="CommandLine"/>，
    /// 具体分片交给 <see cref="Splitter"/>。
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--split") return RunCommandLine(args);
            RunDesktop(args.Length > 0 ? args[0] : null);
            return 0;
        }

        private static int RunCommandLine(string[] args)
        {
            try
            {
                UseUtf8Console();
                CommandLineRequest request = CommandLine.Parse(args);
                // 密码在解析之后读取，这样解析逻辑不依赖控制台。
                string password = request.PasswordFromStdin ? (Console.ReadLine() ?? "") : "";
                SplitResult result = Splitter.Split(request.Input, request.Settings.QpdfPath, password, CancellationToken.None,
                    s => Console.Error.WriteLine(s), request.Settings, request.OutputDirectory);
                // 标准输出只给出输出目录，方便脚本直接取用；进度和警告走标准错误。
                Console.WriteLine(result.DirectoryPath);
                if (result.Warnings.Length > 0) Console.Error.WriteLine(result.Warnings);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }

        /// <summary>
        /// 让重定向的三个标准流都使用无 BOM 的 UTF-8，中文路径和密码才不会乱码。
        /// </summary>
        private static void UseUtf8Console()
        {
            // GUI-subsystem processes have no console code page. Wrap redirected handles
            // directly instead of setting Console.InputEncoding/OutputEncoding.
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)));
        }

        private static void RunDesktop(string initialPath)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(initialPath));
        }
    }
}
