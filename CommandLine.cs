using System;
using System.Collections.Generic;
using System.Globalization;

namespace PdfPageSplitter
{
    /// <summary>命令行解析后的结果，供 <see cref="Program"/> 直接执行。</summary>
    internal sealed class CommandLineRequest
    {
        /// <summary>要分片的源 PDF。</summary>
        public string Input;
        /// <summary>已应用全部命令行覆盖项的设置。</summary>
        public AppSettings Settings;
        /// <summary>显式指定的输出文件夹；null 表示按设置推算。</summary>
        public string OutputDirectory;
        /// <summary>是否要求从标准输入读取打开密码。</summary>
        public bool PasswordFromStdin;
        /// <summary>覆盖同名文件的显式确认，缺少时拒绝执行。</summary>
        public bool ConfirmOverwrite;
    }

    /// <summary>
    /// 解析 <c>--split</c> 模式的命令行。与界面完全解耦：这里只把参数翻译成
    /// <see cref="AppSettings"/>，不读写控制台，也不触碰文件系统（读取设置文件除外）。
    /// </summary>
    internal static class CommandLine
    {
        public const string Usage = "Usage: PdfSplitter.exe --split input.pdf [--qpdf qpdf.exe] [--password-stdin] [--settings settings.xml] [--folder-pattern pattern] [--file-pattern pattern] [--page-digits 1-8] [--output-directory existing-folder] [--mode pages|range] [--start-page N --end-page N] [--range-suffix suffix] [--collision stop|rename|overwrite] [--numbering next|max] [--confirm-overwrite]";

        /// <summary>
        /// 需要紧跟一个取值的选项。两趟扫描共用这一份清单，
        /// 新增带值选项时只改这里，不会出现两处清单不同步的问题。
        /// </summary>
        private static readonly HashSet<string> ValueOptions = new HashSet<string>(StringComparer.Ordinal) {
            "--settings", "--qpdf", "--folder-pattern", "--file-pattern", "--page-digits",
            "--output-directory", "--mode", "--start-page", "--end-page", "--range-suffix",
            "--collision", "--numbering"
        };

        /// <summary>第一个选项的位置：args[0] 是 --split，args[1] 是源 PDF。</summary>
        private const int FirstOption = 2;

        public static CommandLineRequest Parse(string[] args)
        {
            if (args.Length < FirstOption) throw new ArgumentException(Usage);
            // 先取出 --settings，才知道该以哪个文件作为覆盖前的基准设置。
            var request = new CommandLineRequest { Input = args[1], Settings = AppSettings.Load(FindSettingsPath(args)) };
            for (int i = FirstOption; i < args.Length; i++) i = ApplyOption(args, i, request);
            if (request.Settings.OverwriteExisting && !request.ConfirmOverwrite)
                throw new ArgumentException("警告：覆盖会替换同名文件，原内容不会进入回收站。确认后请添加 --confirm-overwrite。");
            return request;
        }

        /// <summary>
        /// 第一趟扫描：只找 --settings。跳过其他带值选项的取值，
        /// 以免把某个选项的值（例如文件名恰好是 --settings）误当成选项。
        /// </summary>
        private static string FindSettingsPath(string[] args)
        {
            string path = AppSettings.DefaultPath;
            for (int i = FirstOption; i < args.Length; i++)
            {
                if (args[i] == "--settings" && i + 1 < args.Length) path = args[++i];
                else if (ValueOptions.Contains(args[i]) && i + 1 < args.Length) i++;
            }
            return path;
        }

        /// <summary>
        /// 应用 args[i] 处的一个选项，返回消费到的最后一个下标（带值选项会多吃一个）。
        /// </summary>
        private static int ApplyOption(string[] args, int i, CommandLineRequest request)
        {
            string option = args[i];
            // 带值选项的取值缺失时不在这里报错，与历史行为一致：落到末尾的未知参数分支。
            bool hasValue = ValueOptions.Contains(option) && i + 1 < args.Length;
            string value = hasValue ? args[i + 1] : null;
            AppSettings settings = request.Settings;
            if (hasValue)
            {
                switch (option)
                {
                    case "--settings": break; // 已在第一趟处理。
                    case "--qpdf": settings.QpdfPath = value; break;
                    case "--folder-pattern": settings.FolderPattern = value; break;
                    case "--file-pattern": settings.FilePattern = value; break;
                    case "--page-digits": settings.PageDigits = Int32.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--output-directory": request.OutputDirectory = value; break;
                    case "--mode": settings.Mode = ParseMode(value); break;
                    case "--start-page": settings.StartPage = Int32.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--end-page": settings.EndPage = Int32.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--range-suffix": settings.RangeSuffix = value; break;
                    case "--collision": ApplyCollision(settings, value); break;
                    case "--numbering": settings.Numbering = ParseNumbering(value); break;
                }
                return i + 1;
            }
            if (option == "--confirm-overwrite") { request.ConfirmOverwrite = true; return i; }
            if (option == "--password-stdin") { request.PasswordFromStdin = true; return i; }
            throw new ArgumentException("Unknown argument: " + option);
        }

        private static SplitMode ParseMode(string value)
        {
            if (value != "pages" && value != "range") throw new ArgumentException("--mode 应为 pages 或 range。");
            return value == "range" ? SplitMode.PageRange : SplitMode.EveryPage;
        }

        /// <summary>覆盖是独立开关，rename/stop 则同时作用于两种分片模式。</summary>
        private static void ApplyCollision(AppSettings settings, string value)
        {
            if (value != "stop" && value != "rename" && value != "overwrite")
                throw new ArgumentException("--collision 应为 stop、rename 或 overwrite。");
            settings.OverwriteExisting = value == "overwrite";
            settings.PageCollision = settings.RangeCollision = value == "rename" ? CollisionPolicy.Rename : CollisionPolicy.Stop;
        }

        private static NumberingStyle ParseNumbering(string value)
        {
            if (value != "next" && value != "max") throw new ArgumentException("--numbering 应为 next 或 max。");
            return value == "max" ? NumberingStyle.BatchMaximum : NumberingStyle.NextAvailable;
        }
    }
}
