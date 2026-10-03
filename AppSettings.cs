using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace PdfPageSplitter
{
    /// <summary>分片方式。</summary>
    public enum SplitMode
    {
        /// <summary>范围内每页各成一个 PDF。</summary>
        EveryPage,
        /// <summary>整个范围合成一个 PDF。</summary>
        PageRange
    }

    /// <summary>
    /// 目标文件夹已有同名文件时的处理方式。
    /// 注意：设置对话框用下拉框的下标直接转成本枚举，顺序不能改。
    /// </summary>
    public enum CollisionPolicy
    {
        /// <summary>终止本次分片，保留已有文件。</summary>
        Stop,
        /// <summary>加 (1)、(2)… 后缀区分。</summary>
        Rename
    }

    /// <summary>自动改名时编号的取法；同样与下拉框下标一一对应。</summary>
    public enum NumberingStyle
    {
        /// <summary>每个文件各自找下一个可用编号。</summary>
        NextAvailable,
        /// <summary>整批统一从已有最大编号 + 1 开始，保持批内编号一致。</summary>
        BatchMaximum
    }

    /// <summary>
    /// 全部可持久化的设置，同时充当命名规则的实现者。
    /// 以公共字段（而非属性）声明，是为了让 XmlSerializer 直接序列化。
    ///
    /// 模板约定：{name} 代入原 PDF 名称，{page} 代入页码，{start}/{end} 代入起止页。
    /// 设置窗口把这些记号显示成等价的中文写法，见 <see cref="NamingText"/>。
    /// 所有生成的名称都会经过 <see cref="ValidateName"/>，不合法就抛异常，
    /// 因此界面能在保存前、分片前就把问题暴露出来。
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>留空表示自动查找 qpdf。</summary>
        public string QpdfPath = "";
        public string FolderPattern = "{name}_clips";
        public string FilePattern = "{name}_page{page}";
        /// <summary>页码补零位数，1 表示不补零。</summary>
        public int PageDigits = 1;
        public SplitMode Mode = SplitMode.EveryPage;
        public int StartPage = 1;
        public int EndPage = 1;
        public string RangeSuffix = "pages{start}-{end}";
        /// <summary>留空表示按模式推算默认输出位置。</summary>
        public string OutputDirectory = "";
        public CollisionPolicy PageCollision = CollisionPolicy.Stop;
        public CollisionPolicy RangeCollision = CollisionPolicy.Stop;
        /// <summary>优先于 CollisionPolicy；启用后同名文件被永久替换。</summary>
        public bool OverwriteExisting = false;
        public NumberingStyle Numbering = NumberingStyle.NextAvailable;

        /// <summary>设置文件固定放在程序同级目录，便于绿色部署。</summary>
        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.xml"); }
        }

        /// <summary>
        /// 浅拷贝。分片开始时取一份快照，过程中用户改设置不会影响正在跑的这一次。
        /// 字段全是值类型和字符串，浅拷贝即可。
        /// </summary>
        public AppSettings Copy()
        {
            return (AppSettings)MemberwiseClone();
        }

        /// <summary>
        /// 读取设置；文件不存在时返回默认值。
        /// 禁用 DTD 和外部解析，避免被构造过的 XML 拿来做实体注入。
        /// </summary>
        public static AppSettings Load(string path)
        {
            if (!File.Exists(path)) return new AppSettings();
            var readerSettings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
            using (var reader = System.Xml.XmlReader.Create(path, readerSettings))
            {
                var settings = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(reader);
                settings.Validate();
                return settings;
            }
        }

        /// <summary>
        /// 原子保存：先写临时文件，再整体替换。
        /// 中途掉电也不会留下写了一半的 settings.xml。
        /// </summary>
        public void Save(string path)
        {
            Validate();
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temporary)) new XmlSerializer(typeof(AppSettings)).Serialize(stream, this);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>校验所有字段自洽；任何一项不合法都抛出可直接显示给用户的异常。</summary>
        public void Validate()
        {
            if (PageDigits < 1 || PageDigits > 8) throw new ArgumentException("页码位数应为 1 到 8。");
            ValidatePattern(FolderPattern, false);
            ValidatePattern(FilePattern, true);
            if (!Enum.IsDefined(typeof(SplitMode), Mode) || !Enum.IsDefined(typeof(CollisionPolicy), PageCollision) ||
                !Enum.IsDefined(typeof(CollisionPolicy), RangeCollision) || !Enum.IsDefined(typeof(NumberingStyle), Numbering)) throw new ArgumentException("分片模式或同名处理设置无效。");
            if (StartPage < 1 || EndPage < StartPage) throw new ArgumentException("起始页和结束页应为正整数，且起始页不能大于结束页。");
            if (String.IsNullOrWhiteSpace(RangeSuffix)) throw new ArgumentException("请填写块状分片的文件后缀。");
            string suffix = RangeSuffix.Replace("{start}", "1").Replace("{end}", "2");
            if (suffix.IndexOfAny(new[] { '{', '}' }) >= 0) throw new ArgumentException("块状后缀支持 {start} 和 {end}，也可填写固定文字。");
            ValidateName(suffix, "块状文件后缀");
        }

        private static void ValidatePattern(string pattern, bool file)
        {
            string label = file ? "文件名" : "文件夹名称";
            if (String.IsNullOrWhiteSpace(pattern) || !pattern.Contains("{name}"))
                throw new ArgumentException(label + "需要包含 {name}，用于代入原 PDF 名称。");
            if (file && !pattern.Contains("{page}"))
                throw new ArgumentException("文件名需要包含 {page}，用于区分每一页。");
            string remaining = pattern.Replace("{name}", "原文件名");
            if (file) remaining = remaining.Replace("{page}", "1");
            if (remaining.IndexOfAny(new[] { '{', '}' }) >= 0)
                throw new ArgumentException(label + (file ? "支持 {name} 和 {page}。" : "支持 {name}。"));
            ValidateName(remaining, label);
        }

        /// <summary>
        /// 校验生成的名称能否安全地作为 Windows 文件名：
        /// 排除空名、. 与 ..、非法字符、末尾空格或句点、超长名称和 CON/PRN 等设备名。
        /// </summary>
        internal static void ValidateName(string name, string label)
        {
            if (String.IsNullOrWhiteSpace(name) || name == "." || name == ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".") || name.EndsWith(" ") || name.Length > 255 ||
                Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new ArgumentException(label + "生成的名称无效。请使用单个名称，避免路径分隔符、末尾空格或句点及过长名称。");
        }

        private string Expand(string pattern, string stem, int page)
        {
            // Replace tokens in the template once: braces in an actual PDF name stay literal.
            return Regex.Replace(pattern, @"\{name\}|\{page\}", match => match.Value == "{name}" ? stem : page.ToString("D" + PageDigits, CultureInfo.InvariantCulture));
        }

        public string FolderName(string stem)
        {
            string name = Expand(FolderPattern, stem, 1);
            ValidateName(name, "文件夹名称");
            return name;
        }

        public string PageName(string stem, int page)
        {
            string name = Expand(FilePattern, stem, page);
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
            ValidateName(name, "文件名");
            return name;
        }

        /// <summary>给界面用的文件名预览：最多显示前两个，再多用省略号带过。</summary>
        public string PageNamesPreview(string stem)
        {
            string preview = PageName(stem, StartPage);
            if (EndPage > StartPage) preview += "、" + PageName(stem, StartPage + 1);
            if (EndPage - StartPage > 1) preview += " …";
            return preview;
        }

        public string RangeName(string stem)
        {
            string suffix = Regex.Replace(RangeSuffix, @"\{start\}|\{end\}", match =>
                (match.Value == "{start}" ? StartPage : EndPage).ToString(CultureInfo.InvariantCulture));
            string name = stem + "_" + suffix;
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
            ValidateName(name, "块状分片命名");
            return name;
        }

        /// <summary>当前模式实际生效的同名处理方式；不参与序列化。</summary>
        [XmlIgnore]
        public CollisionPolicy ActiveCollision
        {
            get { return Mode == SplitMode.PageRange ? RangeCollision : PageCollision; }
        }
    }

    /// <summary>
    /// 命名模板的两种写法。设置文件和命令行一直用 {name}/{page}/{start}/{end}，
    /// 设置窗口显示同义的中文写法，用户不必对照说明就能看懂输入框里的内容。
    /// 只在读写控件和显示提示时转换，settings.xml 的格式保持不变；
    /// 用户直接键入英文写法也照样接受，旧的截图和文档因此继续有效。
    /// </summary>
    internal static class NamingText
    {
        /// <summary>成对排列：偶数下标是存储写法，紧随其后的是界面写法。</summary>
        private static readonly string[] Pairs = { "{name}", "{原文件名}", "{page}", "{页码}", "{start}", "{起始页}", "{end}", "{结束页}" };

        /// <summary>存储写法换成界面写法；错误提示里的记号同样适用。</summary>
        public static string ToDisplay(string text) { return Convert(text, 0); }

        /// <summary>界面写法换回存储写法。</summary>
        public static string ToStorage(string text) { return Convert(text, 1); }

        private static string Convert(string text, int from)
        {
            if (String.IsNullOrEmpty(text)) return text;
            for (int i = 0; i < Pairs.Length; i += 2) text = text.Replace(Pairs[i + from], Pairs[i + 1 - from]);
            return text;
        }
    }
}
