using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PdfPageSplitter
{
    /// <summary>
    /// 发布失败、且回滚也没能把原文件放回去时抛出。
    /// 调用方据此保留暂存目录，让用户能手动找回备份。
    /// </summary>
    internal sealed class PublicationRecoveryException : IOException
    {
        public PublicationRecoveryException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// 把暂存目录里已经就绪的切片搬到目标文件夹。
    ///
    /// 两阶段设计：<see cref="Plan"/> 先算出每个文件的最终名字并检查全部冲突，
    /// <see cref="Publish"/> 才真正移动。这样绝大多数问题在动手之前就被发现。
    /// 移动过程中一旦出错，已搬过去的文件会被逐个退回（覆盖过的用备份还原），
    /// 保证用户的文件夹要么完整更新，要么原封不动。
    /// </summary>
    internal static class OutputPublisher
    {
        /// <summary>已经搬过去的一个文件，用于失败时回滚。</summary>
        private sealed class PublishedFile
        {
            public string Source;
            public string Target;
            /// <summary>覆盖模式下原文件的备份路径；未覆盖时为 null。</summary>
            public string Backup;
        }

        /// <summary>
        /// 识别文件名末尾的重复编号，兼容半角 (1) 和全角（1）。
        /// 第 1 组是主干，第 2/3 组分别是两种括号里的数字。
        /// </summary>
        private static readonly Regex NumberedName = new Regex(@"^(.*?)[ ]*(?:\(([0-9]+)\)|（([0-9]+)）)$");

        /// <summary>自动编号的最大尝试次数，避免异常情况下无限循环。</summary>
        private const int MaxAttempts = 10000;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdentity
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity identity);

        /// <summary>
        /// 判断两个路径是否指向同一个文件。除了比较路径，还要比对卷序号和文件索引，
        /// 这样硬链接和不同写法指向同一文件的情况也能识别，避免把源 PDF 覆盖掉。
        /// </summary>
        private static bool SameFile(string first, string second)
        {
            if (String.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase)) return true;
            if (!File.Exists(second)) return false;
            using (var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                FileIdentity x, y;
                if (!GetFileInformationByHandle(a.SafeFileHandle, out x) || !GetFileInformationByHandle(b.SafeFileHandle, out y))
                    throw new IOException("无法核对源文件与输出文件，请关闭占用文件的程序后重试。");
                return x.Volume == y.Volume && x.IndexHigh == y.IndexHigh && x.IndexLow == y.IndexLow;
            }
        }

        private static bool Exists(string path)
        {
            // Attributes also detect dangling links and objects File.Exists cannot open.
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        /// <summary>
        /// 检查一个目标路径是否可以写入。守住四条底线：
        /// 不能覆盖源 PDF、不能在未启用覆盖时占用已有名字、
        /// 不能覆盖文件夹，也不能覆盖只读文件或链接。
        /// </summary>
        private static void CheckTarget(string target, string input, bool overwrite)
        {
            if (input != null && String.Equals(Path.GetFullPath(input), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                throw new IOException("输出文件不能替换源 PDF：" + target);
            if (!Exists(target)) return;
            if (!overwrite || Directory.Exists(target))
                throw new IOException("输出文件夹中已存在同名文件或文件夹：" + target);
            if ((File.GetAttributes(target) & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
                throw new IOException("无法覆盖只读文件或链接，请更改同名处理方式：" + target);
            if (input != null && SameFile(input, target))
                throw new IOException("输出文件指向源 PDF，已停止以保护源文件：" + target);
        }

        /// <summary>
        /// 十进制字符串加一。用字符串而非整数，才能处理超出 Int64 的超长编号。
        /// </summary>
        private static string Increment(string value)
        {
            char[] digits = value.TrimStart('0').ToCharArray();
            for (int i = digits.Length - 1; i >= 0; i--)
            {
                if (digits[i] == '9') { digits[i] = '0'; continue; }
                digits[i]++;
                return new string(digits);
            }
            return "1" + new string(digits);
        }

        /// <summary>把文件名拆成主干、扩展名和末尾编号；没有编号时编号为 "0"。</summary>
        private static void ParseNumber(string name, out string stem, out string extension, out string number)
        {
            extension = Path.GetExtension(name);
            stem = Path.GetFileNameWithoutExtension(name);
            number = "0";
            Match match = NumberedName.Match(stem);
            if (match.Success && match.Groups[1].Value.Length > 0)
            {
                stem = match.Groups[1].Value;
                number = (match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value).TrimStart('0');
                if (number.Length == 0) number = "0";
            }
        }

        /// <summary>比较两个十进制字符串的大小：先比位数，再按字典序。</summary>
        private static string Maximum(string a, string b)
        {
            return a.Length > b.Length || (a.Length == b.Length && String.CompareOrdinal(a, b) > 0) ? a : b;
        }

        /// <summary>
        /// 整批编号：扫描目标文件夹里所有同族文件，取最大编号 + 1 作为全批的起点，
        /// 让本次输出的编号保持一致（例如各页都是 (6)）。
        /// </summary>
        private static string[] PlanBatch(string destination, string[] names, string input, CancellationToken token)
        {
            var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string maximum = "0";
            foreach (string name in names)
            {
                string stem, extension, number;
                ParseNumber(name, out stem, out extension, out number);
                families.Add(stem + extension);
                maximum = Maximum(maximum, number);
            }
            foreach (string entry in Directory.EnumerateFileSystemEntries(destination))
            {
                token.ThrowIfCancellationRequested();
                string stem, extension, number;
                ParseNumber(Path.GetFileName(entry), out stem, out extension, out number);
                if (families.Contains(stem + extension)) maximum = Maximum(maximum, number);
            }
            string baseline = Increment(maximum);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                string stem, extension, ignored;
                ParseNumber(names[i], out stem, out extension, out ignored);
                string number = baseline;
                string candidate = stem + " (" + number + ")" + extension;
                // Unusual templates may produce several names from the same family.
                while (reserved.Contains(candidate) || Exists(Path.Combine(destination, candidate)))
                {
                    token.ThrowIfCancellationRequested();
                    number = Increment(number);
                    candidate = stem + " (" + number + ")" + extension;
                }
                AppSettings.ValidateName(candidate, "整批自动编号");
                CheckTarget(Path.Combine(destination, candidate), input, false);
                reserved.Add(candidate);
                result[i] = candidate;
            }
            return result;
        }

        /// <summary>
        /// 逐个递增：从名字自带的编号往后找，跳过已占用和本批已预定的名字。
        /// </summary>
        private static string UniqueName(string destination, string name, HashSet<string> reserved, CancellationToken token)
        {
            string extension = Path.GetExtension(name);
            string stem = Path.GetFileNameWithoutExtension(name);
            string number = "1";
            Match match = NumberedName.Match(stem);
            if (match.Success && match.Groups[1].Value.Length > 0)
            {
                stem = match.Groups[1].Value;
                number = Increment(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);
            }
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                string candidate = stem + " (" + number + ")" + extension;
                AppSettings.ValidateName(candidate, "自动编号");
                if (!reserved.Contains(candidate) && !Exists(Path.Combine(destination, candidate))) return candidate;
                number = Increment(number);
            }
            throw new IOException("同名文件过多，请修改后缀或选择其他输出目录。");
        }

        /// <summary>
        /// 试算每个文件的最终名字并检查全部冲突，不改动任何文件。
        /// 分片开始前先跑一次，冲突能在耗时的 qpdf 调用之前就被发现。
        /// </summary>
        /// <returns>与 names 一一对应的最终文件名。</returns>
        internal static string[] Plan(string destination, string[] names, string input, CollisionPolicy policy, bool overwrite, CancellationToken token, NumberingStyle numbering = NumberingStyle.NextAvailable)
        {
            if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("所选输出文件夹不存在，请重新选择：" + destination);
            var reserved = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            if (reserved.Count != names.Length) throw new IOException("命名规则生成了重复的文件名。");
            if (!overwrite && policy == CollisionPolicy.Rename && numbering == NumberingStyle.BatchMaximum &&
                Array.Exists(names, name => Exists(Path.Combine(destination, name))))
                return PlanBatch(destination, names, input, token);
            var result = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                string name = names[i];
                if (!overwrite && policy == CollisionPolicy.Rename && Exists(Path.Combine(destination, name)))
                    name = UniqueName(destination, name, reserved, token);
                CheckTarget(Path.Combine(destination, name), input, overwrite);
                reserved.Add(name);
                result[i] = name;
            }
            return result;
        }

        /// <summary>
        /// 把 ready 目录里的切片正式搬到目标文件夹，失败时整体回滚。
        /// Plan 与实际移动之间仍可能有别的程序插进来建同名文件，
        /// 因此这里会再检查一次并重试，把这段竞态也收拢掉。
        /// </summary>
        /// <returns>实际写入的文件名。</returns>
        internal static string[] Publish(string ready, string destination, string[] names, string input, CollisionPolicy policy, bool overwrite, CancellationToken token, NumberingStyle numbering = NumberingStyle.NextAvailable)
        {
            string[] targets = Plan(destination, names, input, policy, overwrite, token, numbering);
            var reserved = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
            var published = new List<PublishedFile>();
            string backupDirectory = Path.Combine(ready, ".backups");
            if (overwrite) Directory.CreateDirectory(backupDirectory);
            try
            {
                for (int i = 0; i < names.Length; i++)
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        token.ThrowIfCancellationRequested();
                        string source = Path.Combine(ready, names[i]);
                        string target = Path.Combine(destination, targets[i]);
                        if (!overwrite && policy == CollisionPolicy.Rename && Exists(target))
                        {
                            if (numbering == NumberingStyle.BatchMaximum) throw new IOException("保存期间出现新的同名文件，本次输出已回滚，请重试以重新计算整批编号。");
                            if (attempt >= MaxAttempts) throw new IOException("保存期间同名文件持续变化，请重试。");
                            targets[i] = UniqueName(destination, targets[i], reserved, token);
                            reserved.Add(targets[i]);
                            continue;
                        }
                        CheckTarget(target, input, overwrite);
                        string backup = overwrite && File.Exists(target) ? Path.Combine(backupDirectory, i.ToString(CultureInfo.InvariantCulture)) : null;
                        try
                        {
                            if (backup != null) File.Replace(source, target, backup);
                            else File.Move(source, target);
                        }
                        catch (IOException)
                        {
                            // A no-overwrite move closes the race between the existence check and publication.
                            if (!overwrite && policy == CollisionPolicy.Rename && numbering == NumberingStyle.NextAvailable && File.Exists(source) && Exists(target) && attempt < MaxAttempts) continue;
                            throw;
                        }
                        published.Add(new PublishedFile { Source = source, Target = target, Backup = backup });
                        break;
                    }
                }
                token.ThrowIfCancellationRequested();
                return targets;
            }
            catch (Exception failure)
            {
                // 回滚：按相反顺序把已发布的文件退回暂存目录，覆盖过的用备份还原。
                var remaining = new List<string>();
                for (int i = published.Count - 1; i >= 0; i--)
                {
                    PublishedFile entry = published[i];
                    try
                    {
                        if (entry.Backup == null) File.Move(entry.Target, entry.Source);
                        else File.Replace(entry.Backup, entry.Target, entry.Source);
                    }
                    catch (Exception) { remaining.Add(entry.Target + (entry.Backup == null ? "" : "；原文件备份：" + entry.Backup)); }
                }
                if (remaining.Count > 0)
                    throw new PublicationRecoveryException("保存失败，部分文件未能恢复。临时目录将保留，请检查以下文件及备份：\r\n" +
                        String.Join("\r\n", remaining) + "\r\n" + failure.Message, failure);
                throw;
            }
        }
    }
}
