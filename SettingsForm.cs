using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace PdfPageSplitter
{
    /// <summary>
    /// 设置对话框。三个选项卡分别管：分片与输出、同名文件、命名与 qpdf。
    ///
    /// 与主页的分工：分片模式和起止页属于"这一次要怎么切"，留在主页；
    /// 这里只放相对稳定的偏好。因此 mode/startPage/endPage 只读取、原样带回，
    /// 用户在这里点"恢复默认"也不会把主页的选择改掉。
    ///
    /// 任何一处改动都会走 UpdatePreview：先按当前输入拼出一份 AppSettings 并校验，
    /// 通过就显示预览，不通过就把原因写到 error 上——用户不必等到保存才发现写错。
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private readonly TextBox qpdf = new TextBox();
        private readonly TextBox folder = new TextBox();
        private readonly TextBox file = new TextBox();
        private readonly NumericUpDown digits = new NumericUpDown();
        /// <summary>主页决定的分片模式，这里只读。</summary>
        private readonly SplitMode mode;
        private readonly int startPage;
        private readonly int endPage;
        private readonly TextBox rangeSuffix = new TextBox();
        /// <summary>命名输入框下方的实时示例，用户不必到底部预览框去对照。</summary>
        private readonly Label rangeExample = Ui.Label("", true);
        private readonly Label folderExample = Ui.Label("", true);
        private readonly Label fileExample = Ui.Label("", true);
        private readonly Label digitsExample = Ui.Label("", true);
        /// <summary>块状后缀停用时，它的插入按钮跟着一起停用。</summary>
        private readonly List<Button> rangeTokens = new List<Button>();
        private readonly ToolTip tips = new ToolTip();
        private readonly RadioButton automaticOutput = new RadioButton();
        private readonly RadioButton existingOutput = new RadioButton();
        private readonly TextBox outputDirectory = new TextBox();
        private readonly Button browseOutput = Ui.Button("选择文件夹…", 120);
        private readonly ComboBox pageCollision = new ComboBox();
        private readonly ComboBox rangeCollision = new ComboBox();
        private readonly ComboBox numbering = new ComboBox();
        private readonly CheckBox overwrite = new CheckBox();
        /// <summary>只在覆盖开关打开时出现，见 <see cref="UpdateControls"/>。</summary>
        private readonly Label overwriteWarning = Ui.Label("", false);
        /// <summary>开头点名是哪个开关，免得读者不清楚这句话针对什么。</summary>
        private const string OverwriteWarning = "“自动覆盖同名文件”已打开：同名文件的内容会被直接替换，旧内容不进回收站，也无法找回。";
        private readonly TextBox preview = new TextBox();
        private readonly Label error = new Label();
        /// <summary>
        /// 示例一律用这个占位名，不跟随主页载入的 PDF。
        /// 这里定的是长期生效的命名规则，具体某份 PDF 的实际文件名在主页预览。
        /// </summary>
        private const string Placeholder = "原文件名";
        /// <summary>只用来决定“选择文件夹”对话框的起始位置。</summary>
        private readonly string inputPath;
        /// <summary>正在用已有设置回填控件，期间不要触发预览和确认弹窗。</summary>
        private bool filling;

        /// <summary>点击"保存"后的设置；对话框返回 OK 时才有效。</summary>
        public AppSettings Result { get; private set; }

        public SettingsForm(AppSettings current, string input)
        {
            SuspendLayout();
            mode = current.Mode;
            startPage = current.StartPage;
            endPage = current.EndPage;
            ConfigureWindow();
            inputPath = ResolveInputPath(input);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
            // 选项卡占满剩余空间，下面四行是固定高度的预览、提示和按钮。
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (float h in new[] { 28F, 92F, 46F, 42F }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            Controls.Add(root);
            var tabs = new TabControl { Dock = DockStyle.Fill, AccessibleName = "设置分类" };
            root.Controls.Add(tabs, 0, 0);

            BuildSplitPage(tabs);
            BuildConflictPage(tabs);
            BuildNamingPage(tabs);
            BuildPreviewArea(root);
            BuildActions(root);
            WireEvents();

            Fill(current);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(Theme.DesignDpi, Theme.DesignDpi);
            ResumeLayout(true);
        }

        private void ConfigureWindow()
        {
            Text = "设置";
            Theme.Apply(this);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(820, 640);
            MinimumSize = new Size(760, 660);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
        }

        /// <summary>源 PDF 的路径，无效时按没有载入处理。</summary>
        private static string ResolveInputPath(string input)
        {
            try { return String.IsNullOrWhiteSpace(input) ? null : Splitter.NormalizePath(input); }
            catch { return null; }
        }

        /// <summary>选项卡一：块状后缀和输出位置。</summary>
        private void BuildSplitPage(TabControl tabs)
        {
            var split = Page(tabs, "分片与输出", new[] { 38F, 40F, 28F, 44F, 38F, 42F, 56F });
            split.Controls.Add(Ui.Label("当前模式"), 0, 0);
            split.Controls.Add(Ui.Label((mode == SplitMode.PageRange ? "块状分片" : "每页分片") + "（在主页切换）", true), 1, 0);
            split.Controls.Add(Ui.Label("块状文件后缀"), 0, 1);
            Field(rangeSuffix, "块状文件后缀");
            split.Controls.Add(WithTokens(rangeSuffix, rangeTokens, "{起始页}", "{结束页}"), 1, 1);
            rangeExample.AccessibleName = "块状文件名示例";
            Span(split, rangeExample, 2);
            Span(split, Ui.Label("{起始页} 和 {结束页} 会换成主页填写的页码，其余文字原样保留，也可以只写固定文字。\n程序自动在前面加上原文件名和下划线，末尾补上 .pdf。", true), 3);
            split.Controls.Add(Ui.Label("输出位置"), 0, 4);
            AddRadio(automaticOutput, "默认位置", "默认输出位置");
            AddRadio(existingOutput, "指定文件夹", "指定输出文件夹");
            split.Controls.Add(Ui.Flow(automaticOutput, existingOutput), 1, 4);
            split.Controls.Add(Ui.Label("输出文件夹"), 0, 5);
            var directory = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            Ui.Columns(directory, -100F, 124F);
            Field(outputDirectory, "输出文件夹路径");
            directory.Controls.Add(outputDirectory, 0, 0);
            browseOutput.Anchor = AnchorStyles.Left;
            browseOutput.Click += delegate { BrowseOutputDirectory(); };
            directory.Controls.Add(browseOutput, 1, 0);
            split.Controls.Add(directory, 1, 5);
            Span(split, Ui.Label("默认位置：每页分片保存到同级的命名文件夹；块状分片直接保存到源 PDF 旁。\n指定文件夹可包含已有文件；同名处理可在下一页设置。", true), 6);
        }

        /// <summary>选项卡二：同名文件的处理方式和编号规则。</summary>
        private void BuildConflictPage(TabControl tabs)
        {
            var conflicts = Page(tabs, "同名文件", new[] { 40F, 40F, 40F, 76F, 40F, 30F });
            conflicts.Controls.Add(Ui.Label("每页分片"), 0, 0);
            conflicts.Controls.Add(Ui.Label("块状分片"), 0, 1);
            ConfigureCollision(pageCollision, "每页分片同名处理");
            ConfigureCollision(rangeCollision, "块状分片同名处理");
            conflicts.Controls.Add(pageCollision, 1, 0);
            conflicts.Controls.Add(rangeCollision, 1, 1);
            conflicts.Controls.Add(Ui.Label("编号方式"), 0, 2);
            numbering.DropDownStyle = ComboBoxStyle.DropDownList;
            Ui.Stretch(numbering, 3, 8);
            numbering.AccessibleName = "自动编号方式";
            // 两个选项的顺序必须与 NumberingStyle 的枚举顺序一致。
            numbering.Items.AddRange(new object[] { "逐个递增：使用下一个可用编号", "整批编号：从已有最大编号 + 1 开始" });
            numbering.SelectedIndex = 0;
            conflicts.Controls.Add(numbering, 1, 2);
            Span(conflicts, Ui.Label("逐个递增：重名时继续末尾编号，跳过被占用的编号。支持 (数字) 和（数字）。\n整批编号：若本次任一输出重名，取所有相关文件名的最大编号 + 1。\n例如第 1 页已有 (2)、第 2 页已有 (5)，本批各页均从 (6) 开始。", true), 3);
            overwrite.Text = "自动覆盖同名文件（适用于两种分片模式）";
            overwrite.AccessibleName = "自动覆盖同名文件";
            overwrite.AutoSize = true;
            overwrite.Margin = new Padding(3, 8, 0, 0);
            Span(conflicts, overwrite, 4);
            // 平时不占版面：只有真正打开覆盖时才出现这一句。
            overwriteWarning.ForeColor = Theme.Danger;
            overwriteWarning.AccessibleName = "覆盖警告";
            Span(conflicts, overwriteWarning, 5);
        }

        /// <summary>选项卡三：qpdf 位置和每页分片的命名规则。</summary>
        private void BuildNamingPage(TabControl tabs)
        {
            var names = Page(tabs, "命名与 qpdf", new[] { 40F, 26F, 32F, 40F, 26F, 40F, 26F, 40F, 44F });
            names.Controls.Add(Ui.Label("qpdf 程序"), 0, 0);
            var engine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
            Ui.Columns(engine, -100F, 84F, 104F);
            Field(qpdf, "qpdf 程序路径");
            engine.Controls.Add(qpdf, 0, 0);
            var browse = Ui.Button("浏览…", 80);
            browse.Anchor = AnchorStyles.Left;
            browse.Click += delegate {
                using (var dialog = new OpenFileDialog { Title = "选择 qpdf.exe", Filter = "qpdf 程序 (qpdf.exe)|qpdf.exe", CheckFileExists = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) qpdf.Text = dialog.FileName;
            };
            engine.Controls.Add(browse, 1, 0);
            var auto = Ui.Button("自动查找", 100);
            auto.Anchor = AnchorStyles.Left;
            auto.Click += delegate {
                qpdf.Text = Splitter.FindQpdf();
                error.Text = qpdf.Text.Length == 0 ? "未找到 qpdf，请点击“浏览…”选择。" : "";
            };
            engine.Controls.Add(auto, 2, 0);
            names.Controls.Add(engine, 1, 0);
            Span(names, Ui.Label("留空时自动查找本机安装的 qpdf。", true), 1);
            Span(names, Ui.Label("每页分片的命名（{原文件名}、{页码} 会换成实际内容）"), 2);
            names.Controls.Add(Ui.Label("文件夹名称"), 0, 3);
            Field(folder, "文件夹命名规则");
            names.Controls.Add(WithTokens(folder, null, "{原文件名}"), 1, 3);
            folderExample.AccessibleName = "文件夹名称示例";
            Span(names, folderExample, 4);
            names.Controls.Add(Ui.Label("文件名"), 0, 5);
            Field(file, "切片文件命名规则");
            names.Controls.Add(WithTokens(file, null, "{原文件名}", "{页码}"), 1, 5);
            fileExample.AccessibleName = "文件名示例";
            Span(names, fileExample, 6);
            names.Controls.Add(Ui.Label("页码补零位数"), 0, 7);
            digits.Minimum = 1;
            digits.Maximum = 8;
            digits.Width = 70;
            digits.AccessibleName = "页码位数";
            digitsExample.AccessibleName = "页码写法示例";
            tips.SetToolTip(digits, "页码不足这个位数时前面补 0，例如 3 位写成 001。");
            var padding = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            Ui.Columns(padding, digits.Width + 14F, -100F);
            digits.Anchor = AnchorStyles.Left;
            digits.Margin = new Padding(3, 0, 11, 0);
            padding.Controls.Add(digits, 0, 0);
            padding.Controls.Add(digitsExample, 1, 0);
            names.Controls.Add(padding, 1, 7);
            Span(names, Ui.Label("文件名末尾自动补上 .pdf；“文件夹名称”用作每页分片的默认输出文件夹。\n补零位数大于 1 时页码前面补 0，按文件名排序时不会出现 1、10、2 的顺序。", true), 8);
        }

        /// <summary>选项卡下方常驻的预览框和错误提示。</summary>
        private void BuildPreviewArea(TableLayoutPanel root)
        {
            root.Controls.Add(Ui.Label("输出示例（“原文件名”代表实际 PDF 的名称，自动编号以保存结果为准）"), 0, 1);
            preview.Dock = DockStyle.Fill;
            preview.Multiline = true;
            preview.ReadOnly = true;
            preview.ScrollBars = ScrollBars.Both;
            preview.WordWrap = false;
            preview.BackColor = Theme.Surface;
            preview.AccessibleName = "命名预览";
            root.Controls.Add(preview, 0, 2);
            error.Dock = DockStyle.Fill;
            error.ForeColor = Theme.Danger;
            error.TextAlign = ContentAlignment.MiddleLeft;
            error.AccessibleName = "设置提示";
            root.Controls.Add(error, 0, 3);
        }

        /// <summary>底部按钮：左侧"恢复默认"，右侧"保存 / 取消"。</summary>
        private void BuildActions(TableLayoutPanel root)
        {
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var reset = Ui.Button("恢复默认", 116);
            // 只重置这个对话框管的偏好；主页的模式和范围由 ReadFields 原样带回。
            reset.Click += delegate { Fill(new AppSettings()); };
            actions.Controls.Add(reset, 0, 0);
            var save = Ui.Button("保存", 88);
            Ui.Primary(save);
            save.Click += delegate { SaveSettings(); };
            var cancel = Ui.Button("取消", 88);
            cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(Ui.Flow(save, cancel), 1, 0);
            root.Controls.Add(actions, 0, 4);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private void WireEvents()
        {
            foreach (TextBox field in new[] { folder, file, rangeSuffix, outputDirectory })
                field.TextChanged += delegate { UpdatePreview(); };
            digits.ValueChanged += delegate { UpdatePreview(); };
            automaticOutput.CheckedChanged += delegate { UpdateControls(); };
            existingOutput.CheckedChanged += delegate { UpdateControls(); };
            overwrite.CheckedChanged += delegate {
                // 覆盖不可撤销，勾选时要求二次确认；回填过程中不打扰用户。
                if (!filling && overwrite.Checked && MessageBox.Show(this,
                    "启用后，输出文件夹里同名的文件会被直接替换，旧内容不进回收站，无法找回。\r\n该设置对每页分片和块状分片都生效。确认启用自动覆盖？",
                    "确认启用覆盖", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    overwrite.Checked = false;
                UpdateControls();
            };
        }

        /// <summary>新建一个选项卡，内含一张"标签 / 控件"两列表格。</summary>
        private static TableLayoutPanel Page(TabControl tabs, string title, float[] heights)
        {
            var page = new TabPage(title) { BackColor = Theme.Window, AutoScroll = true, Padding = new Padding(12) };
            tabs.TabPages.Add(page);
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = heights.Length, Margin = Padding.Empty };
            Ui.Columns(table, 124F, -100F);
            foreach (float height in heights) table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            page.Controls.Add(table);
            return table;
        }

        /// <summary>把一个控件横跨两列放置，用于整行的说明文字。</summary>
        private static void Span(TableLayoutPanel table, Control control, int row)
        {
            table.Controls.Add(control, 0, row);
            table.SetColumnSpan(control, 2);
        }

        private static void Field(TextBox field, string name)
        {
            Ui.Stretch(field, 3, 8);
            field.AccessibleName = name;
        }

        /// <summary>
        /// 把一个命名输入框和它的“插入记号”按钮排成一行。
        /// 记号一律用中文写法显示，ReadFields 再换回 {name} 这类存储写法。
        /// </summary>
        private TableLayoutPanel WithTokens(TextBox field, List<Button> collect, params string[] tokens)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = tokens.Length + 1, Margin = Padding.Empty };
            var widths = new float[tokens.Length + 1];
            widths[0] = -100F;
            row.Controls.Add(field, 0, 0);
            for (int i = 0; i < tokens.Length; i++)
            {
                Button insert = TokenButton(field, tokens[i]);
                widths[i + 1] = insert.Width + 8;
                row.Controls.Add(insert, i + 1, 0);
                if (collect != null) collect.Add(insert);
            }
            Ui.Columns(row, widths);
            return row;
        }

        /// <summary>“＋原文件名”一类的小按钮：在光标处补回一个记号。</summary>
        private Button TokenButton(TextBox field, string token)
        {
            string caption = token.Trim('{', '}');
            var insert = Ui.Button("＋" + caption, 26 + caption.Length * 18);
            insert.Anchor = AnchorStyles.Left;
            insert.AccessibleName = "插入" + caption;
            insert.Click += delegate { InsertToken(field, token); };
            tips.SetToolTip(insert, "在光标处插入 " + token);
            return insert;
        }

        /// <summary>在光标处插入记号，随后把焦点交还输入框，方便接着输入。</summary>
        private static void InsertToken(TextBox field, string token)
        {
            int caret = field.SelectionStart;
            field.Text = field.Text.Remove(caret, field.SelectionLength).Insert(caret, token);
            field.SelectionStart = caret + token.Length;
            field.Focus();
        }

        private static void AddRadio(RadioButton radio, string text, string name)
        {
            radio.Text = text;
            radio.AccessibleName = name;
            radio.AutoSize = true;
            radio.Margin = new Padding(3, 7, 20, 0);
        }

        private static void ConfigureCollision(ComboBox combo, string name)
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            Ui.Stretch(combo, 3, 8);
            combo.AccessibleName = name;
            // 选项顺序必须与 CollisionPolicy 的枚举顺序一致。
            combo.Items.AddRange(new object[] { "自动终止，保留已有文件", "添加 (1)、(2)… 后缀区分" });
            combo.SelectedIndex = 0;
        }

        private void BrowseOutputDirectory()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择保存分片的文件夹（可包含已有文件）", ShowNewFolderButton = true })
            {
                if (Directory.Exists(outputDirectory.Text)) dialog.SelectedPath = outputDirectory.Text;
                else if (inputPath != null) dialog.SelectedPath = Path.GetDirectoryName(inputPath);
                if (dialog.ShowDialog(this) == DialogResult.OK) outputDirectory.Text = dialog.SelectedPath;
            }
        }

        /// <summary>
        /// 把当前控件状态收集成一份 AppSettings。这是界面到设置的唯一转换点，
        /// 预览和保存都走它，两者永远看到同一份内容。
        /// </summary>
        private AppSettings ReadFields()
        {
            if (existingOutput.Checked && String.IsNullOrWhiteSpace(outputDirectory.Text.Trim().Trim('"')))
                throw new ArgumentException("请选择或填写输出文件夹。");
            return new AppSettings {
                QpdfPath = qpdf.Text.Trim().Trim('"'), FolderPattern = NamingText.ToStorage(folder.Text), FilePattern = NamingText.ToStorage(file.Text), PageDigits = (int)digits.Value,
                // 分片模式和范围归主页管，这里原样带回。
                Mode = mode, StartPage = startPage, EndPage = endPage,
                RangeSuffix = NamingText.ToStorage(rangeSuffix.Text), OutputDirectory = existingOutput.Checked ? Splitter.NormalizePath(outputDirectory.Text) : "",
                PageCollision = (CollisionPolicy)pageCollision.SelectedIndex, RangeCollision = (CollisionPolicy)rangeCollision.SelectedIndex,
                Numbering = (NumberingStyle)numbering.SelectedIndex, OverwriteExisting = overwrite.Checked
            };
        }

        /// <summary>用一份设置回填所有控件。</summary>
        private void Fill(AppSettings settings)
        {
            filling = true;
            try
            {
                qpdf.Text = settings.QpdfPath;
                folder.Text = NamingText.ToDisplay(settings.FolderPattern);
                file.Text = NamingText.ToDisplay(settings.FilePattern);
                digits.Value = settings.PageDigits;
                rangeSuffix.Text = NamingText.ToDisplay(settings.RangeSuffix);
                outputDirectory.Text = settings.OutputDirectory;
                // 输出目录留空即代表"默认位置"。
                automaticOutput.Checked = String.IsNullOrWhiteSpace(settings.OutputDirectory);
                existingOutput.Checked = !automaticOutput.Checked;
                pageCollision.SelectedIndex = (int)settings.PageCollision;
                rangeCollision.SelectedIndex = (int)settings.RangeCollision;
                numbering.SelectedIndex = (int)settings.Numbering;
                overwrite.Checked = settings.OverwriteExisting;
            }
            finally { filling = false; }
            UpdateControls();
        }

        /// <summary>根据当前选择灰掉用不上的控件，然后刷新预览。</summary>
        private void UpdateControls()
        {
            rangeSuffix.Enabled = mode == SplitMode.PageRange;
            foreach (Button insert in rangeTokens) insert.Enabled = rangeSuffix.Enabled;
            outputDirectory.Enabled = browseOutput.Enabled = existingOutput.Checked;
            // 启用覆盖后，同名处理和编号方式都不再起作用。
            pageCollision.Enabled = rangeCollision.Enabled = numbering.Enabled = !overwrite.Checked;
            overwriteWarning.Text = overwrite.Checked ? OverwriteWarning : "";
            UpdatePreview();
        }

        /// <summary>试算一次输出位置和文件名；非法设置在这里就会显示原因。</summary>
        private void UpdatePreview()
        {
            if (filling) return;
            try
            {
                var settings = ReadFields();
                settings.Validate();
                preview.Text = "保存位置：" + Destination(settings) + "\r\n生成文件：" + (settings.Mode == SplitMode.PageRange
                    ? settings.RangeName(Placeholder) + "（共 " + (settings.EndPage - settings.StartPage + 1) + " 页，1 个 PDF）"
                    : settings.PageNamesPreview(Placeholder));
                ShowExamples(settings);
                error.Text = "";
            }
            catch (Exception ex) { preview.Clear(); ShowExamples(null); error.Text = NamingText.ToDisplay(ex.Message); }
        }

        /// <summary>
        /// 用一句话说明文件会落在哪里。指定文件夹是用户在这个对话框里选的，直接显示路径；
        /// 默认位置取决于源 PDF，贴一条绝对路径反而看不出来历，因此改成文字描述。
        /// </summary>
        private static string Destination(AppSettings settings)
        {
            if (settings.OutputDirectory.Length > 0) return settings.OutputDirectory;
            if (settings.Mode == SplitMode.PageRange) return "源 PDF 所在的文件夹";
            return "源 PDF 旁新建的“" + settings.FolderName(Placeholder) + "”文件夹";
        }

        /// <summary>
        /// 刷新输入框下方的示例。设置不合法时留空，免得示例和错误提示互相矛盾。
        /// </summary>
        private void ShowExamples(AppSettings settings)
        {
            if (settings == null)
            {
                rangeExample.Text = folderExample.Text = fileExample.Text = digitsExample.Text = "";
                return;
            }
            rangeExample.Text = "例如：" + settings.RangeName(Placeholder);
            folderExample.Text = "例如：" + settings.FolderName(Placeholder) + Path.DirectorySeparatorChar;
            fileExample.Text = "例如：" + settings.PageNamesPreview(Placeholder);
            digitsExample.Text = settings.PageDigits == 1
                ? "页码写作 1、2、10…（不补零）"
                : "页码写作 " + PageNumber(1, settings.PageDigits) + "、" + PageNumber(2, settings.PageDigits) + "、" + PageNumber(10, settings.PageDigits) + "…";
        }

        private static string PageNumber(int page, int width)
        {
            return page.ToString("D" + width, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 保存前做完整校验：三种命名都真正生成一遍，输出目录和 qpdf 路径都要存在。
        /// 任何一项不通过就留在对话框里显示原因，不关窗、不写文件。
        /// </summary>
        private void SaveSettings()
        {
            try
            {
                AppSettings settings = ReadFields();
                settings.Validate();
                // 逐个试生成，确保模板在这台机器上真能产出合法文件名。
                settings.FolderName(Placeholder);
                settings.PageName(Placeholder, 1);
                settings.RangeName(Placeholder);
                if (settings.OutputDirectory.Length > 0 && !Directory.Exists(settings.OutputDirectory))
                    throw new DirectoryNotFoundException("输出文件夹不存在，请重新选择。");
                if (settings.QpdfPath.Length > 0)
                {
                    settings.QpdfPath = Splitter.NormalizePath(settings.QpdfPath);
                    if (!File.Exists(settings.QpdfPath)) throw new FileNotFoundException("qpdf 路径不存在，请重新选择或留空自动查找。");
                }
                settings.Save(AppSettings.DefaultPath);
                Result = settings;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) { error.Text = "未保存：" + NamingText.ToDisplay(ex.Message); }
        }

        /// <summary>ToolTip 不在 Controls 里，需要随对话框一起释放。</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
