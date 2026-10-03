using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PdfPageSplitter
{
    /// <summary>一次 qpdf 调用的结果：标准输出与（作为警告收集的）标准错误。</summary>
    internal sealed class QpdfResult
    {
        public string Output;
        public string Warnings;
    }

    /// <summary>
    /// 负责"如何调用 qpdf"这一件事：查找可执行文件、拼装命令行、
    /// 通过标准输入传递密码、支持取消，并把非零退出码翻译成中文提示。
    /// 分片流程（<see cref="Splitter"/>）只描述要做什么，不关心进程细节。
    /// </summary>
    internal static class QpdfRunner
    {
        /// <summary>退出码 3 表示"有警告但已成功产出"，按成功处理。</summary>
        private const int ExitCodeWarning = 3;

        /// <summary>轮询进程退出的间隔，取消请求最多延迟这么久才会生效。</summary>
        private const int PollMilliseconds = 100;

        /// <summary>
        /// 按优先级查找 qpdf.exe：环境变量指定 → 程序同级目录 → Chocolatey 安装目录 → PATH。
        /// 找不到时返回空字符串，由调用方给出提示。
        /// </summary>
        public static string Find()
        {
            var candidates = new List<string>();
            candidates.Add(Environment.GetEnvironmentVariable("QPDF_EXECUTABLE"));
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qpdf.exe"));
            // Prefer Chocolatey's actual binary so cancellation owns the qpdf process directly.
            string chocolatey = Environment.GetEnvironmentVariable("ChocolateyInstall");
            if (String.IsNullOrEmpty(chocolatey))
                chocolatey = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
            string package = Path.Combine(chocolatey, "lib", "qpdf", "tools");
            try
            {
                if (Directory.Exists(package))
                    candidates.AddRange(Directory.GetFiles(package, "qpdf.exe", SearchOption.AllDirectories));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (string part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { if (!String.IsNullOrWhiteSpace(part)) candidates.Add(Path.Combine(part.Trim().Trim('"'), "qpdf.exe")); }
                catch (ArgumentException) { }
            }
            return candidates.FirstOrDefault(p => !String.IsNullOrWhiteSpace(p) && File.Exists(p)) ?? "";
        }

        /// <summary>
        /// 解析调用方给出的 qpdf 路径：留空则自动查找，随后校验文件存在并规范化。
        /// </summary>
        public static string Resolve(string qpdf)
        {
            if (String.IsNullOrWhiteSpace(qpdf)) qpdf = Find();
            if (!File.Exists(qpdf)) throw new FileNotFoundException("找不到 qpdf.exe，请在“设置”中选择本机的 qpdf.exe。", qpdf);
            return qpdf;
        }

        // Windows CommandLineToArgvW/CRT quoting; no command shell is involved.
        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }

        /// <summary>
        /// 启动 qpdf 并等待结束。密码经标准输入传入，因此不会出现在命令行或磁盘文件中；
        /// 取消时连同子进程一并结束，避免留下孤儿进程继续写临时文件。
        /// </summary>
        public static QpdfResult Run(string exe, IEnumerable<string> arguments, string password, CancellationToken token, string workingDirectory = null)
        {
            token.ThrowIfCancellationRequested();
            // Framework's Process.Start writes Console.InputEncoding's BOM before we can
            // access StandardInput. Own the pipes without changing the host's console state.
            using (var process = QpdfProcess.Start(exe, Quote(exe) + " " + String.Join(" ", arguments.Select(Quote)), workingDirectory))
            {
                // Read both pipes concurrently; a full pipe would otherwise deadlock the child.
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                try
                {
                    WritePassword(process.StandardInput, password);
                    WaitForExit(process, stdout, stderr, token);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        KillTree(process);
                        process.WaitForExit();
                    }
                }
                Task.WaitAll(stdout, stderr);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0 && process.ExitCode != ExitCodeWarning) throw Failure(process.ExitCode, stdout.Result, stderr.Result);
                return new QpdfResult { Output = stdout.Result, Warnings = stderr.Result.Trim() };
            }
        }

        /// <summary>把密码写入标准输入后立即关闭，并抹去内存中的明文副本。</summary>
        private static void WritePassword(Stream input, string password)
        {
            // Send UTF-8 without BOM over stdin; passwords never enter argv or a disk file.
            byte[] secret = new UTF8Encoding(false).GetBytes((password ?? "") + "\n");
            try
            {
                input.Write(secret, 0, secret.Length);
                input.Flush();
            }
            catch (IOException) { /* A failing executable may already have closed stdin. */ }
            finally
            {
                Array.Clear(secret, 0, secret.Length);
                input.Close();
            }
        }

        /// <summary>轮询等待退出；一旦请求取消就结束整棵进程树，然后抛出取消异常。</summary>
        private static void WaitForExit(QpdfProcess process, Task<string> stdout, Task<string> stderr, CancellationToken token)
        {
            while (!process.WaitForExit(PollMilliseconds))
            {
                if (!token.IsCancellationRequested) continue;
                KillTree(process);
                process.WaitForExit();
                Task.WaitAll(stdout, stderr);
                token.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// 结束 qpdf 及其子进程。若用户选择的是 Chocolatey 等启动器，真正干活的是它的子进程，
        /// 所以先用 taskkill /T 结束整棵树，再兜底调用 Kill。
        /// </summary>
        private static void KillTree(QpdfProcess process)
        {
            try
            {
                // Also handles an explicitly selected launcher such as a Chocolatey shim.
                var kill = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                    "/PID " + process.Id.ToString(CultureInfo.InvariantCulture) + " /T /F");
                kill.UseShellExecute = false;
                kill.CreateNoWindow = true;
                kill.RedirectStandardOutput = true;
                kill.RedirectStandardError = true;
                using (var killer = Process.Start(kill))
                {
                    var killerOut = killer.StandardOutput.ReadToEndAsync();
                    var killerError = killer.StandardError.ReadToEndAsync();
                    killer.WaitForExit();
                    Task.WaitAll(killerOut, killerError);
                }
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException) { }
        }

        /// <summary>把 qpdf 的退出码和错误输出翻译成面向用户的中文提示。</summary>
        private static Exception Failure(int exitCode, string output, string error)
        {
            string detail = error.Trim();
            if (detail.Length == 0) detail = output.Trim();
            if (detail.IndexOf("invalid password", StringComparison.OrdinalIgnoreCase) >= 0)
                return new InvalidOperationException("PDF 需要正确的打开密码，请在密码框中填写后重试。\r\n\r\n" + detail);
            return new InvalidOperationException("qpdf 处理失败（退出码 " + exitCode + "）。\r\n" + detail);
        }
    }
}
