using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace PdfPageSplitter
{
    /// <summary>一次分片完成后的结果，用于界面和命令行汇报。</summary>
    public sealed class SplitResult
    {
        /// <summary>切片最终所在的文件夹。</summary>
        public string DirectoryPath;
        /// <summary>本次实际分出的页数。</summary>
        public int PageCount;
        /// <summary>源 PDF 的总页数。</summary>
        public int TotalPageCount;
        /// <summary>生成的 PDF 个数。</summary>
        public int FileCount;
        /// <summary>生成的文件名（已应用自动编号等改名结果）。</summary>
        public string[] FileNames;
        /// <summary>qpdf 的非致命提示，合并自读页数和分片两步。</summary>
        public string Warnings;
    }

    /// <summary>
    /// 分片流程的编排者。职责划分：
    /// 调用 qpdf 由 <see cref="QpdfRunner"/> 负责，命名规则由 <see cref="AppSettings"/> 负责，
    /// 把结果安全地落到目标文件夹由 <see cref="OutputPublisher"/> 负责，这里只串联步骤。
    ///
    /// 整体策略：先在目标卷上的私有暂存目录里生成全部切片，确认无误后再一次性发布。
    /// 中途失败或取消都不会在用户的文件夹里留下半成品。
    /// </summary>
    public static class Splitter
    {
        /// <summary>暂存目录的前缀，便于识别和清理。</summary>
        private const string StagingPrefix = ".pdf-splitter-";

        /// <summary>qpdf --split-pages 生成的中间文件名模板，序号从 1 开始。</summary>
        private const string PagePrefix = "part-";

        /// <summary>把用户输入的路径去掉引号和空白后转成绝对路径。</summary>
        public static string NormalizePath(string value)
        {
            return Path.GetFullPath((value ?? "").Trim().Trim('"'));
        }

        /// <summary>查找本机的 qpdf.exe，找不到时返回空字符串。</summary>
        public static string FindQpdf()
        {
            return QpdfRunner.Find();
        }

        /// <summary>
        /// 读取 PDF 的总页数。界面用它在选择文件后即时显示页数并校验密码。
        /// </summary>
        public static int GetPageCount(string input, string qpdf, string password, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            input = ValidateInput(input);
            qpdf = QpdfRunner.Resolve(qpdf);
            ValidatePassword(password);
            string warnings;
            return ReadPageCount(NormalizePath(qpdf), input, password, token, out warnings);
        }

        /// <summary>校验源文件存在且确为 .pdf，返回规范化后的绝对路径。</summary>
        private static string ValidateInput(string input)
        {
            input = NormalizePath(input);
            if (!File.Exists(input)) throw new FileNotFoundException("找不到 PDF 文件，请检查输入路径。", input);
            if (!String.Equals(Path.GetExtension(input), ".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择扩展名为 .pdf 的文件。");
            return input;
        }

        /// <summary>密码经标准输入逐行传递，因此不能自带换行符。</summary>
        private static void ValidatePassword(string password)
        {
            if ((password ?? "").IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new ArgumentException("密码不能包含换行符。");
        }

        private static int ReadPageCount(string qpdf, string input, string password, CancellationToken token, out string warnings)
        {
            QpdfResult count = QpdfRunner.Run(qpdf, new[] { "--password-file=-", input, "--show-npages" }, password, token);
            int total;
            if (!Int32.TryParse(count.Output.Trim(), out total) || total < 1)
                throw new InvalidOperationException("PDF 没有可分片的页面，或 qpdf 返回了无效页数。");
            warnings = count.Warnings;
            return total;
        }

        /// <summary>
        /// 推算默认输出位置：设置里指定了文件夹就用它；
        /// 块状分片直接输出到源 PDF 旁；每页分片输出到同级的命名文件夹。
        /// </summary>
        public static string SuggestedDirectory(string input, AppSettings settings = null)
        {
            settings = settings ?? new AppSettings();
            if (!String.IsNullOrWhiteSpace(settings.OutputDirectory)) return NormalizePath(settings.OutputDirectory);
            if (settings.Mode == SplitMode.PageRange) return Path.GetDirectoryName(input);
            return Path.Combine(Path.GetDirectoryName(input), settings.FolderName(Path.GetFileNameWithoutExtension(input)));
        }

        /// <summary>自动创建的文件夹必须是新建的，绝不合并到已存在的同名对象上。</summary>
        private static void CheckNewDirectory(string destination)
        {
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new IOException("自动创建的输出路径已存在：" + destination + "\r\n请选择现有文件夹，或在“设置”中修改文件夹名称后重试。");
        }

        private static void PublishToExistingDirectory(string ready, string destination, string[] names, CancellationToken token)
        {
            OutputPublisher.Publish(ready, destination, names, null, CollisionPolicy.Stop, false, token);
        }

        /// <summary>
        /// 确定切片要落到哪个文件夹。返回 null 表示"稍后新建一个文件夹"，
        /// 此时切片先留在暂存目录，最后整体改名成目标文件夹。
        /// </summary>
        private static string ResolveDestination(string input, AppSettings settings, string outputDirectory)
        {
            if (outputDirectory == null && !String.IsNullOrWhiteSpace(settings.OutputDirectory)) outputDirectory = settings.OutputDirectory;
            if (outputDirectory != null)
            {
                if (String.IsNullOrWhiteSpace(outputDirectory) || outputDirectory.Trim().Trim('"').Length == 0)
                    throw new ArgumentException("请选择现有的输出文件夹。");
                string chosen = NormalizePath(outputDirectory);
                if (!Directory.Exists(chosen))
                    throw new DirectoryNotFoundException("所选输出文件夹不存在，请重新选择：" + chosen);
                return chosen;
            }
            // 块状分片只产出一个文件，直接放在源 PDF 旁边。
            if (settings.Mode == SplitMode.PageRange) return Path.GetDirectoryName(input);

            // 每页分片默认新建文件夹；若同名文件夹已存在则改为写入其中。
            string suggested = SuggestedDirectory(input, settings);
            if (!settings.OverwriteExisting && settings.ActiveCollision == CollisionPolicy.Stop) CheckNewDirectory(suggested);
            if (File.Exists(suggested)) CheckNewDirectory(suggested);
            return Directory.Exists(suggested) ? suggested : null;
        }

        /// <summary>按当前模式算出每个输出文件的最终名称。</summary>
        private static string[] BuildOutputNames(AppSettings settings, string stem, int selectedPages)
        {
            if (settings.Mode == SplitMode.PageRange) return new[] { settings.RangeName(stem) };
            return Enumerable.Range(settings.StartPage, selectedPages).Select(page => settings.PageName(stem, page)).ToArray();
        }

        /// <summary>一次分片过程中在各步骤之间传递的上下文。</summary>
        private sealed class Job
        {
            public string Qpdf;
            public string Input;
            public string Password;
            public AppSettings Settings;
            public CancellationToken Token;
            public Action<string> Status;
            public string Raw;
            public string Ready;
            public string[] Names;
            public int SelectedPages;
            public string Range;
        }

        /// <summary>
        /// 块状分片：一次 qpdf 调用把选定范围抽成单个 PDF，再改名进 ready 目录。
        /// </summary>
        private static QpdfResult ExtractRange(Job job)
        {
            job.Status("正在提取第 " + job.Range + " 页，请稍候…");
            QpdfResult split = QpdfRunner.Run(job.Qpdf,
                new[] { "--password-file=-", job.Input, "--decrypt", "--pages", ".", job.Range, "--", "range.pdf" },
                job.Password, job.Token, job.Raw);
            string block = Path.Combine(job.Raw, "range.pdf");
            if (!File.Exists(block) || new FileInfo(block).Length == 0) throw new IOException("块状分片输出为空。");
            job.Status("正在整理范围分片…");
            File.Move(block, Path.Combine(job.Ready, job.Names[0]));
            return split;
        }

        /// <summary>
        /// 每页分片：qpdf 把范围内每页拆成 part-N.pdf，再按源页码改成最终文件名。
        /// </summary>
        private static QpdfResult ExtractPages(Job job)
        {
            job.Status("正在拆分第 " + job.Range + " 页（共 " + job.SelectedPages + " 页），请稍候…");
            // A relative, fixed template protects '%' characters in source and parent paths.
            QpdfResult split = QpdfRunner.Run(job.Qpdf,
                new[] { "--password-file=-", job.Input, "--decrypt", "--pages", ".", job.Range, "--", "--split-pages=1", PagePrefix + "%d.pdf" },
                job.Password, job.Token, job.Raw);
            Dictionary<int, string> indexed = IndexPages(job);
            job.Status("正在整理 " + job.SelectedPages + " 个切片文件…");
            // qpdf numbers selected output pages from 1; final names use source page numbers.
            for (int index = 0; index < job.SelectedPages; index++)
            {
                job.Token.ThrowIfCancellationRequested();
                string file = indexed[index + 1];
                if (new FileInfo(file).Length == 0) throw new IOException("第 " + (job.Settings.StartPage + index) + " 页输出为空。");
                File.Move(file, Path.Combine(job.Ready, job.Names[index]));
            }
            return split;
        }

        /// <summary>核对 qpdf 产出的切片数量和编号，并按编号建立索引。</summary>
        private static Dictionary<int, string> IndexPages(Job job)
        {
            string[] pages = Directory.GetFiles(job.Raw, PagePrefix + "*.pdf");
            if (pages.Length != job.SelectedPages) throw new IOException("生成的切片数量与分片范围页数不符，请重试。");
            var indexed = new Dictionary<int, string>();
            foreach (string page in pages)
            {
                int number;
                if (!Int32.TryParse(Path.GetFileNameWithoutExtension(page).Substring(PagePrefix.Length), out number) ||
                    number < 1 || number > job.SelectedPages || indexed.ContainsKey(number))
                    throw new IOException("qpdf 生成了无法识别的页码。");
                indexed.Add(number, page);
            }
            return indexed;
        }

        /// <summary>
        /// 执行一次分片。<paramref name="outputDirectory"/> 为 null 时按设置推算输出位置。
        /// </summary>
        /// <param name="status">进度文本回调，可为 null。</param>
        public static SplitResult Split(string input, string qpdf, string password, CancellationToken token, Action<string> status, AppSettings settings = null, string outputDirectory = null)
        {
            settings = (settings ?? new AppSettings()).Copy();
            settings.Validate();
            input = ValidateInput(input);

            // 先把"写到哪里"定下来，越早发现目标不可用越好，避免白跑一次 qpdf。
            string destination = ResolveDestination(input, settings, outputDirectory);
            qpdf = NormalizePath(QpdfRunner.Resolve(qpdf));
            ValidatePassword(password);
            status = status ?? delegate { };

            status("正在读取 PDF 页数…");
            string countWarnings;
            int total = ReadPageCount(qpdf, input, password, token, out countWarnings);
            if (settings.EndPage > total)
                throw new ArgumentException("结束页超出 PDF 总页数（" + total + " 页）。");
            int selectedPages = settings.EndPage - settings.StartPage + 1;

            // Stage on the destination volume, so publishing also works across drives and
            // when the source directory is read-only.
            string parent = destination ?? Path.GetDirectoryName(input);
            string stem = Path.GetFileNameWithoutExtension(input);
            // 新建文件夹的情况下提前校验文件夹名，免得分片完才发现命名非法。
            if (destination == null) settings.FolderName(stem);
            string[] names = BuildOutputNames(settings, stem, selectedPages);
            // 目标文件夹已存在时先试算一遍同名处理，冲突能在动手前就被发现。
            if (destination != null) OutputPublisher.Plan(destination, names, input, settings.ActiveCollision, settings.OverwriteExisting, token, settings.Numbering);

            string staging = Path.Combine(parent, StagingPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            bool preserveStaging = false;
            try
            {
                var job = new Job {
                    Qpdf = qpdf, Input = input, Password = password, Settings = settings, Token = token, Status = status,
                    Raw = Path.Combine(staging, "raw"), Ready = Path.Combine(staging, "ready"),
                    Names = names, SelectedPages = selectedPages,
                    Range = settings.StartPage.ToString(CultureInfo.InvariantCulture) + "-" + settings.EndPage.ToString(CultureInfo.InvariantCulture)
                };
                Directory.CreateDirectory(job.Raw);
                Directory.CreateDirectory(job.Ready);
                QpdfResult split = settings.Mode == SplitMode.PageRange ? ExtractRange(job) : ExtractPages(job);

                token.ThrowIfCancellationRequested();
                if (destination != null)
                {
                    status("正在保存切片到所选文件夹…");
                    names = OutputPublisher.Publish(job.Ready, destination, names, input, settings.ActiveCollision, settings.OverwriteExisting, token, settings.Numbering);
                }
                else
                {
                    // Publish a complete new directory; never merge a directory created concurrently.
                    destination = SuggestedDirectory(input, settings);
                    CheckNewDirectory(destination);
                    try { Directory.Move(job.Ready, destination); }
                    catch (IOException)
                    {
                        CheckNewDirectory(destination);
                        throw;
                    }
                }
                return new SplitResult {
                    DirectoryPath = destination,
                    PageCount = selectedPages,
                    TotalPageCount = total,
                    FileCount = names.Length,
                    FileNames = names,
                    Warnings = String.Join("\r\n", new[] { countWarnings, split.Warnings }.Where(s => s.Length > 0))
                };
            }
            catch (PublicationRecoveryException)
            {
                preserveStaging = true;
                throw;
            }
            finally
            {
                // Keep recovery backups if restoring original files failed.
                // Only this run's private staging directory can be removed here.
                if (!preserveStaging && Directory.Exists(staging))
                {
                    try { Directory.Delete(staging, true); }
                    catch (Exception ex) { throw new IOException("临时文件夹清理失败，请检查：" + staging, ex); }
                }
            }
        }
    }
}
