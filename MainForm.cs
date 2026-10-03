using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PdfPageSplitter
{
    /// <summary>
    /// 主窗口。分成四块职责，分别由下面的 Build*/Update*/Split 方法负责：
    ///
    /// 1. 构建界面        —— 构造函数只按顺序调用各 Build* 方法，具体布局在各自方法里。
    /// 2. 读取总页数      —— SchedulePageCount / ReadPageCountAsync，防抖后在后台跑 qpdf。
    /// 3. 同步预览        —— UpdateOutputPreview 把设置和输入翻译成界面上的提示文字。
    /// 4. 执行分片        —— StartSplit / CancelSplit，切换忙碌状态并调用 <see cref="Splitter"/>。
    ///
    /// 窗口本身不含分片逻辑，也不直接调用 qpdf 进程；这些都在 <see cref="Splitter"/> 一侧。
    /// </summary>
    public sealed class MainForm : Form
    {
        // ---------- 布局常量 ----------

        /// <summary>输入区表格的列宽：标签列、输入列（占满剩余）、按钮列。</summary>
        private const float LabelColumnWidth = 96F;
        private const float ButtonColumnWidth = 118F;

        /// <summary>
        /// 输入区各行的高度（96 DPI 基准）。两个提示行初始为 0，出现警告时才撑开，
        /// 因此它们的下标必须与 WarningRow* 常量一致。
        /// </summary>
        private static readonly float[] InputRowHeights = { 40F, 40F, 38F, 40F, 38F, 40F, 0F, 40F, 38F, 0F };

        /// <summary>分片范围警告所在的行号。</summary>
        private const int WarningRowRange = 6;
        /// <summary>覆盖警告所在的行号。</summary>
        private const int WarningRowOutput = 9;

        /// <summary>无警告时输入区的总高度，等于 InputRowHeights 之和。</summary>
        private const float InputPanelHeight = 346F;
        private const float RangeWarningHeight = 28F;
        private const float OutputWarningHeight = 36F;

        // ---------- 行为常量 ----------

        /// <summary>连续输入时的防抖延迟，避免每敲一个字符就启动一次 qpdf。</summary>
        private const int PageCountDelayMs = 350;
        /// <summary>读取总页数的超时，防止异常文件让界面一直停在"读取中"。</summary>
        private const int PageCountTimeoutSeconds = 30;

        private const string NoSourceText = "—（尚未选择 PDF）";

        // ---------- 控件 ----------

        private readonly TextBox source = new TextBox();
        private readonly TextBox password = new TextBox();
        private readonly TextBox output = new TextBox();
        private readonly Label modeSummary = Ui.Label("");
        private readonly RadioButton everyPage = new RadioButton();
        private readonly RadioButton pageRange = new RadioButton();
        private readonly TextBox startPage = new TextBox();
        private readonly TextBox endPage = new TextBox();
        private readonly Label rangeHint = Ui.Label("", true);
        private readonly Label pageCount = Ui.Label(NoSourceText, true);
        private readonly System.Windows.Forms.Timer pageCountTimer = new System.Windows.Forms.Timer();
        private readonly Label outputHint = Ui.Label("", true);
        private readonly TextBox filenames = new TextBox();
        private readonly TextBox details = new TextBox();
        private readonly Label status = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Button start = Ui.Button("开始分片", 132);
        private readonly Button cancel = Ui.Button("取消", 88);
        private readonly Button open = Ui.Button("打开输出文件夹", 154);
        private readonly Button settingsButton = Ui.Button("设置", 88);
        private readonly Panel inputPanel = new Panel();
        private readonly TableLayoutPanel inputs = new TableLayoutPanel();
        private readonly RowStyle inputRow = new RowStyle(SizeType.Absolute, InputPanelHeight);

        // ---------- 状态 ----------

        private AppSettings settings;
        /// <summary>非 null 表示正在分片；同时用作"忙碌中"的判断依据。</summary>
        private CancellationTokenSource cancellation;
        private string lastOutput;
        private bool closeAfterCancel;
        /// <summary>界面正在回填模式单选按钮，期间忽略 CheckedChanged，避免递归保存。</summary>
        private bool updatingMode;
        /// <summary>界面正在回填范围输入框，期间忽略 TextChanged。</summary>
        private bool updatingRange;
        /// <summary>换了新 PDF，等读出总页数后把范围重置为全篇。</summary>
        private bool initializeRange = true;
        /// <summary>已知的源 PDF 总页数；null 表示尚未读出。</summary>
        private int? totalPages;
        /// <summary>分片结束后程序主动清空密码框，此时不要重新触发读页数。</summary>
        private bool clearingPassword;
        private CancellationTokenSource pageCountCancellation;
        /// <summary>尚未结束的读页数任务，关闭窗口时要等它们退出以回收 qpdf 进程。</summary>
        private readonly List<Task> pageCountReads = new List<Task>();

        public MainForm(string initialPath)
        {
            SuspendLayout();
            string settingsWarning = LoadSettings();
            ConfigureWindow();

            TableLayoutPanel root = BuildRoot();
            Controls.Add(root);
            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(Ui.Label("选择、粘贴或拖入 PDF。", true), 0, 1);
            root.Controls.Add(BuildInputPanel(), 0, 2);
            root.Controls.Add(BuildActions(), 0, 3);
            BuildStatusArea(root, settingsWarning);

            WireEvents();
            UpdateOutputPreview();
            // 拖放要在控件树建好之后统一挂接，子控件才都能接收文件。
            WireDrop(this);
            if (!String.IsNullOrWhiteSpace(initialPath)) SetSource(initialPath);
            else UpdateOutputPreview();
            // DPI 缩放必须在控件就位后设置，否则布局按错误的基准换算。
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(Theme.DesignDpi, Theme.DesignDpi);
            ResumeLayout(true);
        }

        /// <summary>读取设置；失败时退回默认值并返回要显示给用户的说明。</summary>
        private string LoadSettings()
        {
            try
            {
                settings = AppSettings.Load(AppSettings.DefaultPath);
                return "";
            }
            catch (Exception ex)
            {
                settings = new AppSettings();
                return "设置读取失败，当前使用默认值。可在“设置”中重新保存。\r\n" + ex.Message;
            }
        }

        private void ConfigureWindow()
        {
            Text = "PDF 分片工具";
            Theme.Apply(this);
            ClientSize = new Size(820, 732);
            MinimumSize = new Size(730, 762);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = SystemIcons.Application;
            KeyPreview = true;
        }

        /// <summary>纵向排列的整体骨架：标题、说明、输入区、按钮、状态、进度、详情。</summary>
        private TableLayoutPanel BuildRoot()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 20), ColumnCount = 1, RowCount = 7 };
            foreach (float h in new[] { 48F, 30F }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            // 输入区的高度会随警告出现而变化，因此保留 RowStyle 引用供 UpdateWarningLayout 调整。
            root.RowStyles.Add(inputRow);
            foreach (float h in new[] { 50F, 32F, 18F }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return root;
        }

        /// <summary>标题行：左侧大标题，右侧"设置"按钮。</summary>
        private TableLayoutPanel BuildHeader()
        {
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            Ui.Columns(header, -100F, 88F);
            header.Controls.Add(new Label { Text = "PDF 分片", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, Theme.TitleFontSize, FontStyle.Bold) }, 0, 0);
            settingsButton.Margin = Padding.Empty;
            settingsButton.Click += delegate { OpenSettings(); };
            header.Controls.Add(settingsButton, 1, 0);
            return header;
        }

        /// <summary>
        /// 白色输入卡片。内部是一张三列表格：标签 / 输入 / 按钮，
        /// 各行的含义见 InputRowHeights 上方的说明。
        /// </summary>
        private Panel BuildInputPanel()
        {
            inputPanel.Dock = DockStyle.Fill;
            inputPanel.BackColor = Theme.Surface;
            inputPanel.Padding = new Padding(16);
            inputs.Dock = DockStyle.Fill;
            inputs.ColumnCount = 3;
            inputs.RowCount = InputRowHeights.Length;
            Ui.Columns(inputs, LabelColumnWidth, -100F, ButtonColumnWidth);
            foreach (float h in InputRowHeights) inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            inputPanel.Controls.Add(inputs);

            BuildSourceRows();
            BuildPasswordRows();
            BuildModeRow();
            BuildRangeRow();
            BuildPreviewRows();
            return inputPanel;
        }

        /// <summary>第 0–2 行：文件路径、快捷键提示、总页数。</summary>
        private void BuildSourceRows()
        {
            inputs.Controls.Add(Ui.Label("PDF 文件"), 0, 0);
            source.AccessibleName = "PDF 文件路径";
            Ui.Stretch(source, 3, 10);
            inputs.Controls.Add(source, 1, 0);

            var browse = Ui.Button("选择 PDF…", 112);
            browse.Anchor = AnchorStyles.Left;
            browse.Click += delegate { BrowsePdf(); };
            inputs.Controls.Add(browse, 2, 0);
            var paste = Ui.Button("粘贴文件", 112);
            paste.Anchor = AnchorStyles.Left;
            paste.Click += delegate { PasteFile(); };
            inputs.Controls.Add(paste, 2, 1);
            inputs.Controls.Add(Ui.Label("Ctrl+O 选择文件    Ctrl+V 粘贴文件", true), 1, 1);

            inputs.Controls.Add(Ui.Label("PDF 总页数"), 0, 2);
            pageCount.AccessibleName = "PDF 总页数";
            inputs.Controls.Add(pageCount, 1, 2);
            inputs.SetColumnSpan(pageCount, 2);
        }

        /// <summary>第 3 行：打开密码。</summary>
        private void BuildPasswordRows()
        {
            inputs.Controls.Add(Ui.Label("打开密码"), 0, 3);
            password.UseSystemPasswordChar = true;
            password.AccessibleName = "PDF 打开密码（无密码留空）";
            Ui.Stretch(password, 3, 10);
            inputs.Controls.Add(password, 1, 3);
            inputs.Controls.Add(Ui.Label("无密码留空", true), 2, 3);
        }

        /// <summary>第 4 行：两种分片模式和当前模式的说明。</summary>
        private void BuildModeRow()
        {
            inputs.Controls.Add(Ui.Label("分片模式"), 0, 4);
            var modes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
            Ui.Columns(modes, 100F, 100F, -100F);
            ConfigureModeButton(everyPage, "每页分片", SplitMode.EveryPage);
            ConfigureModeButton(pageRange, "块状分片", SplitMode.PageRange);
            modes.Controls.Add(everyPage, 0, 0);
            modes.Controls.Add(pageRange, 1, 0);
            modeSummary.AccessibleName = "当前分片模式";
            modeSummary.Margin = new Padding(10, 0, 0, 0);
            modes.Controls.Add(modeSummary, 2, 0);
            inputs.Controls.Add(modes, 1, 4);
            inputs.SetColumnSpan(modes, 2);
        }

        /// <summary>第 5 行：起始页和结束页；第 6 行是它们的警告位。</summary>
        private void BuildRangeRow()
        {
            inputs.Controls.Add(Ui.Label("分片范围"), 0, 5);
            var range = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty };
            range.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            range.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            range.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            range.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            range.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            range.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (TextBox field in new[] { startPage, endPage })
            {
                field.Width = 90;
                field.Anchor = AnchorStyles.Left;
                field.Margin = new Padding(3, 0, 16, 0);
                field.AccessibleName = field == startPage ? "起始页" : "结束页";
                field.AccessibleDescription = "包含起始页和结束页。输入完成后自动限制在 1 至 PDF 总页数之间，并按从小到大排列。";
                // 离开输入框或按回车即视为输入完成，此时才纠正数值，免得打字打到一半被改。
                field.Leave += delegate { NormalizePageRange(); };
                field.KeyDown += delegate(object sender, KeyEventArgs e) {
                    if (e.KeyCode != Keys.Enter) return;
                    NormalizePageRange();
                    e.SuppressKeyPress = true;
                };
                int column = field == startPage ? 0 : 2;
                var label = Ui.Label(field.AccessibleName);
                label.Margin = new Padding(3, 0, 5, 0);
                range.Controls.Add(label, column, 0);
                range.Controls.Add(field, column + 1, 0);
            }
            inputs.Controls.Add(range, 1, 5);
            inputs.SetColumnSpan(range, 2);
            rangeHint.AccessibleName = "分片范围提示";
            inputs.Controls.Add(rangeHint, 0, WarningRowRange);
            inputs.SetColumnSpan(rangeHint, 3);
        }

        /// <summary>第 7–9 行：输出文件夹与文件名预览，以及覆盖警告位。</summary>
        private void BuildPreviewRows()
        {
            inputs.Controls.Add(Ui.Label("输出文件夹"), 0, 7);
            ConfigurePreview(output, "输出文件夹路径");
            output.AccessibleDescription = "路径相对于源 PDF 所在目录，可在“设置”中修改。";
            inputs.Controls.Add(output, 1, 7);
            inputs.SetColumnSpan(output, 2);
            inputs.Controls.Add(Ui.Label("切片文件名"), 0, 8);
            ConfigurePreview(filenames, "切片文件名预览");
            inputs.Controls.Add(filenames, 1, 8);
            inputs.SetColumnSpan(filenames, 2);
            outputHint.AccessibleName = "输出方式提示";
            inputs.Controls.Add(outputHint, 0, WarningRowOutput);
            inputs.SetColumnSpan(outputHint, 3);
        }

        /// <summary>开始 / 取消 / 打开输出文件夹三个按钮。</summary>
        private FlowLayoutPanel BuildActions()
        {
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0), WrapContents = false, Margin = Padding.Empty };
            Ui.Primary(start);
            start.Click += async delegate { await StartSplit(); };
            cancel.Enabled = false;
            cancel.Click += delegate { CancelSplit(); };
            open.Enabled = false;
            open.Click += delegate {
                try { Process.Start(new ProcessStartInfo(lastOutput) { UseShellExecute = true }); }
                catch (Exception ex) { details.Text = "无法打开输出文件夹：" + ex.Message; }
            };
            actions.Controls.AddRange(new Control[] { start, cancel, open });
            return actions;
        }

        /// <summary>状态行、进度条和详情框。</summary>
        private void BuildStatusArea(TableLayoutPanel root, string settingsWarning)
        {
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Text = "准备就绪";
            root.Controls.Add(status, 0, 4);
            progress.Dock = DockStyle.Fill;
            root.Controls.Add(progress, 0, 5);
            details.Dock = DockStyle.Fill;
            details.ReadOnly = true;
            details.Multiline = true;
            details.ScrollBars = ScrollBars.Both;
            details.WordWrap = false;
            details.BackColor = Theme.Surface;
            details.BorderStyle = BorderStyle.FixedSingle;
            details.Margin = new Padding(0, 10, 0, 0);
            details.Text = settingsWarning;
            root.Controls.Add(details, 0, 6);
        }

        /// <summary>把输入变化接到各自的响应上，集中一处便于查阅触发关系。</summary>
        private void WireEvents()
        {
            pageCountTimer.Interval = PageCountDelayMs;
            pageCountTimer.Tick += async delegate {
                pageCountTimer.Stop();
                Task read = ReadPageCountAsync();
                pageCountReads.Add(read);
                try { await read; }
                finally { pageCountReads.Remove(read); }
            };
            source.TextChanged += delegate { initializeRange = true; SchedulePageCount(); };
            password.TextChanged += delegate { if (!clearingPassword) SchedulePageCount(); };
            startPage.TextChanged += delegate { if (!updatingRange) UpdatePageRange(); };
            endPage.TextChanged += delegate { if (!updatingRange) UpdatePageRange(); };
            FormClosing += async delegate(object sender, FormClosingEventArgs e) { await OnClosingAsync(e); };
        }

        /// <summary>
        /// 关闭窗口前先收尾：分片中则转为取消并等待，读页数中则等 qpdf 进程退出，
        /// 避免留下仍在写临时文件的子进程。
        /// </summary>
        private async Task OnClosingAsync(FormClosingEventArgs e)
        {
            if (cancellation != null)
            {
                e.Cancel = true;
                closeAfterCancel = true;
                CancelSplit();
                return;
            }
            CancelPageCountRead();
            if (pageCountReads.Count == 0) return;
            e.Cancel = true;
            // Keep the message loop alive until all owned qpdf processes exit.
            await Task.WhenAll(pageCountReads.ToArray());
            pageCountReads.RemoveAll(task => task.IsCompleted);
            if (!IsDisposed) Close();
        }

        /// <summary>只读预览框：看起来像文字，但可以选中复制。</summary>
        private static void ConfigurePreview(TextBox control, string name)
        {
            control.ReadOnly = true;
            control.BorderStyle = BorderStyle.None;
            control.BackColor = Theme.Surface;
            Ui.Stretch(control);
            control.AccessibleName = name;
        }

        /// <summary>把单选按钮做成分段控件的样子，选中即保存分片模式。</summary>
        private void ConfigureModeButton(RadioButton button, string text, SplitMode mode)
        {
            button.Text = text;
            button.AccessibleName = text;
            button.Appearance = Appearance.Button;
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(3, 2, 3, 2);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Theme.Border;
            button.CheckedChanged += delegate {
                if (updatingMode || !button.Checked) return;
                settings.Mode = mode;
                UpdateOutputPreview();
                SaveSettings("分片模式已保存：" + text, "分片模式已切换，本次保存失败", "当前模式可用于本次分片。");
            };
        }

        /// <summary>
        /// 保存设置并反馈结果。保存失败不阻断操作：设置在内存中已生效，
        /// 本次分片仍可继续，只是下次启动不会记住。
        /// </summary>
        private void SaveSettings(string success, string failure, string reassurance)
        {
            try
            {
                settings.Save(AppSettings.DefaultPath);
                status.Text = success;
            }
            catch (Exception ex)
            {
                status.Text = failure;
                details.Text = reassurance + "保存设置失败：" + ex.Message;
            }
        }

        // ---------- 分片范围 ----------

        /// <summary>刚换了 PDF 且范围还空着——此时不算输入错误，只是在等总页数。</summary>
        private bool IsRangePending
        {
            get { return initializeRange && startPage.Text.Length == 0 && endPage.Text.Length == 0; }
        }

        private string ValidatePageRange(out int first, out int last)
        {
            return PageRange.Validate(startPage.Text, endPage.Text, totalPages, out first, out last);
        }

        /// <summary>把输入框里的内容纠正成规范的页码，然后同步预览。</summary>
        private void NormalizePageRange()
        {
            int first, last;
            bool validFirst = PageRange.TryRead(startPage.Text, totalPages, out first);
            bool validLast = PageRange.TryRead(endPage.Text, totalPages, out last);
            if (validFirst && validLast && first > last) PageRange.Swap(ref first, ref last);
            WhileUpdatingRange(delegate {
                if (validFirst) startPage.Text = first.ToString(CultureInfo.InvariantCulture);
                if (validLast) endPage.Text = last.ToString(CultureInfo.InvariantCulture);
            });
            UpdatePageRange();
        }

        /// <summary>程序回填范围输入框期间屏蔽 TextChanged，避免自触发。</summary>
        private void WhileUpdatingRange(Action action)
        {
            updatingRange = true;
            try { action(); }
            finally { updatingRange = false; }
        }

        /// <summary>范围变化后更新预览，并把新范围记进设置。</summary>
        private void UpdatePageRange()
        {
            int first, last;
            bool changed = ValidatePageRange(out first, out last).Length == 0 &&
                (settings.StartPage != first || settings.EndPage != last);
            if (changed)
            {
                settings.StartPage = first;
                settings.EndPage = last;
            }
            UpdateOutputPreview();
            if (!changed) return;
            SaveSettings("分片范围已保存", "分片范围已更新，本次保存失败", "当前范围可用于本次分片。");
        }

        // ---------- 总页数 ----------

        private void CancelPageCountRead()
        {
            pageCountTimer.Stop();
            if (pageCountCancellation == null) return;
            var pending = pageCountCancellation;
            pageCountCancellation = null;
            pending.Cancel();
        }

        /// <summary>
        /// 源文件或密码变化后重新读页数。先取消上一次请求并清空已知页数，
        /// 再启动防抖计时器，连续输入时只会真正读取一次。
        /// </summary>
        private void SchedulePageCount()
        {
            CancelPageCountRead();
            totalPages = null;
            if (initializeRange) WhileUpdatingRange(delegate { startPage.Clear(); endPage.Clear(); });
            pageCount.ForeColor = Theme.MutedText;
            pageCount.Text = String.IsNullOrWhiteSpace(source.Text) ? NoSourceText : "读取中…";
            if (!String.IsNullOrWhiteSpace(source.Text)) pageCountTimer.Start();
            UpdateOutputPreview();
        }

        /// <summary>读到页数后显示出来；若是新载入的 PDF 就把范围设为全篇。</summary>
        private void SetPageCount(int total)
        {
            totalPages = total;
            pageCount.Text = total + " 页";
            pageCount.ForeColor = Theme.Accent;
            if (initializeRange)
            {
                WhileUpdatingRange(delegate {
                    startPage.Text = "1";
                    endPage.Text = total.ToString(CultureInfo.InvariantCulture);
                });
                initializeRange = false;
            }
            NormalizePageRange();
        }

        /// <summary>
        /// 在后台读取总页数。只有最后一次请求的结果会被采用：
        /// 用户可能已经改了文件或密码，旧结果必须丢弃。
        /// </summary>
        private async Task ReadPageCountAsync()
        {
            var request = new CancellationTokenSource();
            pageCountCancellation = request;
            string input = source.Text, secret = password.Text, qpdf = settings.QpdfPath;
            request.CancelAfter(TimeSpan.FromSeconds(PageCountTimeoutSeconds));
            try
            {
                int total = await Task.Run(() => Splitter.GetPageCount(input, qpdf, secret, request.Token));
                if (IsStaleRead(request)) return;
                SetPageCount(total);
            }
            catch (OperationCanceledException)
            {
                if (!IsStaleRead(request)) pageCount.Text = "读取超时，请重新载入 PDF。";
            }
            catch (Exception ex)
            {
                if (IsStaleRead(request)) return;
                // 只显示首行：qpdf 的错误往往很长，详细内容对主页没有帮助。
                pageCount.Text = ex.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                pageCount.ForeColor = Theme.Danger;
            }
            finally
            {
                secret = null;
                if (pageCountCancellation == request) pageCountCancellation = null;
                request.Dispose();
            }
        }

        /// <summary>窗口已销毁，或这次读取已被更新的请求取代。</summary>
        private bool IsStaleRead(CancellationTokenSource request)
        {
            return IsDisposed || Disposing || pageCountCancellation != request;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                CancelPageCountRead();
                pageCountTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------- 设置与载入 ----------

        private void OpenSettings()
        {
            using (var dialog = new SettingsForm(settings.Copy(), source.Text))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string previousQpdf = settings.QpdfPath;
                settings = dialog.Result;
                UpdateOutputPreview();
                // 换了 qpdf 就得重新读一次页数，之前的结果可能来自另一个版本。
                if (!String.Equals(previousQpdf, settings.QpdfPath, StringComparison.OrdinalIgnoreCase)) SchedulePageCount();
                status.Text = "设置已保存";
            }
        }

        /// <summary>递归地让整个窗口都能接收拖放，用户往哪儿丢都行。</summary>
        private void WireDrop(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += delegate(object sender, DragEventArgs e) {
                e.Effect = cancellation == null && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText)) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            control.DragDrop += delegate(object sender, DragEventArgs e) { if (cancellation == null) LoadData(e.Data); };
            foreach (Control child in control.Controls) WireDrop(child);
        }

        private void SetSource(string path)
        {
            try
            {
                string normalized = Splitter.NormalizePath(path);
                if (!File.Exists(normalized) || !String.Equals(Path.GetExtension(normalized), ".pdf", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("请载入一个有效的 PDF 文件。");
                initializeRange = true;
                source.Text = normalized;
                // 新文件的密码多半不同，留着旧密码只会让页数读取失败。
                password.Clear();
                SchedulePageCount();
                status.Text = "已载入：" + Path.GetFileName(normalized);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法载入文件", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        // ---------- 预览 ----------

        /// <summary>
        /// 把当前设置和输入翻译成界面上的各处文字：模式按钮、范围提示、
        /// 输出文件夹和文件名预览，并决定"开始分片"是否可用。
        /// 任何影响这些显示的改动都应经过这里，保证各处口径一致。
        /// </summary>
        private void UpdateOutputPreview()
        {
            bool range = settings.Mode == SplitMode.PageRange;
            PaintModeButtons(range);

            int first, last;
            string rangeError = ValidatePageRange(out first, out last);
            bool pendingRange = IsRangePending;
            rangeHint.Text = pendingRange ? "" : rangeError;
            rangeHint.ForeColor = Theme.Danger;
            start.Enabled = cancellation == null && (pendingRange || rangeError.Length == 0);
            modeSummary.Text = pendingRange ? (range ? "范围合为 1 个 PDF" : "范围内每页 1 个 PDF") :
                rangeError.Length > 0 ? "请填写分片范围" : range
                ? "第 " + first + "–" + last + " 页（1 个 PDF）" : "范围内每页 1 个 PDF";
            outputHint.Text = settings.OverwriteExisting ? "将覆盖同名文件，原内容无法通过回收站恢复。" : "";
            outputHint.ForeColor = Theme.Danger;
            UpdateWarningLayout();
            UpdatePathPreview(range, pendingRange, rangeError);
        }

        /// <summary>回填模式单选按钮的选中状态和配色。</summary>
        private void PaintModeButtons(bool range)
        {
            updatingMode = true;
            try
            {
                everyPage.Checked = !range;
                pageRange.Checked = range;
                foreach (RadioButton button in new[] { everyPage, pageRange })
                {
                    button.BackColor = button.Checked ? Theme.Accent : Theme.Surface;
                    button.ForeColor = button.Checked ? Theme.Surface : ForeColor;
                    button.FlatAppearance.CheckedBackColor = button.BackColor;
                }
            }
            finally { updatingMode = false; }
        }

        /// <summary>更新输出文件夹和切片文件名两行预览。</summary>
        private void UpdatePathPreview(bool range, bool pendingRange, string rangeError)
        {
            try
            {
                bool empty = String.IsNullOrWhiteSpace(source.Text);
                string input = empty ? null : Splitter.NormalizePath(source.Text);
                string stem = empty ? "原文件名" : Path.GetFileNameWithoutExtension(input);
                output.Text = empty && String.IsNullOrWhiteSpace(settings.OutputDirectory)
                    ? (range ? @".\" : @".\" + settings.FolderName(stem) + @"\")
                    : FormatOutputDirectory(input, Splitter.SuggestedDirectory(input, settings));
                filenames.Text = pendingRange ? "" : rangeError.Length > 0 ? "请填写有效的分片范围" :
                    range ? settings.RangeName(stem) : settings.PageNamesPreview(stem);
            }
            catch (Exception ex)
            {
                // 命名规则非法时不要抛给用户，直接把原因显示在预览位置。
                output.Text = "请检查文件路径或命名设置";
                filenames.Text = NamingText.ToDisplay(ex.Message);
            }
        }

        /// <summary>
        /// 两个警告行平时高度为 0，需要时才撑开，并同步加高整个输入区，
        /// 免得卡片里出现滚动条。
        /// </summary>
        private void UpdateWarningLayout()
        {
            // Derive warning heights from an existing row to preserve DPI scaling.
            float scale = inputs.RowStyles[0].Height / InputRowHeights[0];
            bool rangeWarning = rangeHint.Text.Length > 0;
            bool outputWarning = outputHint.Text.Length > 0;
            inputs.SuspendLayout();
            rangeHint.Visible = rangeWarning;
            outputHint.Visible = outputWarning;
            inputs.RowStyles[WarningRowRange].Height = rangeWarning ? RangeWarningHeight * scale : 0;
            inputs.RowStyles[WarningRowOutput].Height = outputWarning ? OutputWarningHeight * scale : 0;
            inputRow.Height = InputPanelHeight * scale + inputs.RowStyles[WarningRowRange].Height + inputs.RowStyles[WarningRowOutput].Height;
            inputs.ResumeLayout(true);
        }

        /// <summary>
        /// 把绝对输出路径写成相对源 PDF 的短路径，界面上更易读。
        /// 跨盘符或网络共享时无法表示相对路径，此时保留绝对路径。
        /// </summary>
        private static string FormatOutputDirectory(string input, string directory)
        {
            string destination = Splitter.NormalizePath(directory).TrimEnd('\\') + @"\";
            if (String.IsNullOrWhiteSpace(input)) return destination;

            string sourceDirectory = Path.GetDirectoryName(Splitter.NormalizePath(input));
            string sourceRoot = Path.GetPathRoot(sourceDirectory);
            // Windows relative paths cannot cross drive letters or network shares.
            if (!String.Equals(sourceRoot, Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
                return destination;

            string[] sourceParts = sourceDirectory.Substring(sourceRoot.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            string[] targetParts = destination.Substring(sourceRoot.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            int common = 0;
            while (common < sourceParts.Length && common < targetParts.Length &&
                String.Equals(sourceParts[common], targetParts[common], StringComparison.OrdinalIgnoreCase)) common++;

            // 先用 ..\ 退到共同祖先，再逐级进入目标目录。
            string relative = "";
            for (int i = common; i < sourceParts.Length; i++) relative += @"..\";
            if (relative.Length == 0) relative = @".\";
            for (int i = common; i < targetParts.Length; i++) relative += targetParts[i] + @"\";
            return relative;
        }

        // ---------- 选择文件 ----------

        private void BrowsePdf()
        {
            using (var dialog = new OpenFileDialog { Title = "选择要分片的 PDF", Filter = "PDF 文件 (*.pdf)|*.pdf", CheckFileExists = true, Multiselect = false })
                if (dialog.ShowDialog(this) == DialogResult.OK) SetSource(dialog.FileName);
        }

        /// <summary>接受两种来源：资源管理器复制的文件，或一段文件路径文本。</summary>
        private void LoadData(IDataObject data)
        {
            if (data == null) return;
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length != 1) { MessageBox.Show(this, "请每次载入一个 PDF 文件。", "选择文件"); return; }
                SetSource(files[0]);
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText)) SetSource(Convert.ToString(data.GetData(DataFormats.UnicodeText)));
            else MessageBox.Show(this, "先在资源管理器中复制一个 PDF 文件，或复制它的完整路径。", "粘贴文件");
        }

        private void PasteFile()
        {
            try { LoadData(Clipboard.GetDataObject()); }
            catch (Exception ex) { MessageBox.Show(this, "无法读取剪贴板，请重试。\r\n" + ex.Message, "粘贴文件"); }
        }

        /// <summary>
        /// Ctrl+O 选择文件，Ctrl+V 粘贴文件。
        /// 焦点在文本框里时把 Ctrl+V 让给文本框，只有复制的是文件才抢过来。
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (cancellation == null)
            {
                if (keyData == (Keys.Control | Keys.O)) { BrowsePdf(); return true; }
                if (keyData == (Keys.Control | Keys.V))
                {
                    try
                    {
                        if ((!password.Focused && !output.Focused && !startPage.Focused && !endPage.Focused && Clipboard.ContainsFileDropList()) ||
                            (!source.Focused && !password.Focused && !output.Focused && !details.Focused && !startPage.Focused && !endPage.Focused))
                        { PasteFile(); return true; }
                    }
                    catch (System.Runtime.InteropServices.ExternalException) { }
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------- 分片 ----------

        private void CancelSplit()
        {
            if (cancellation == null) return;
            cancellation.Cancel();
            cancel.Enabled = false;
            status.Text = "正在取消并清理临时文件…";
        }

        /// <summary>分片期间锁住输入，只留"取消"可用。</summary>
        private void BeginSplitUi()
        {
            inputPanel.Enabled = false;
            settingsButton.Enabled = false;
            start.Enabled = false;
            cancel.Enabled = true;
            open.Enabled = false;
            progress.Style = ProgressBarStyle.Marquee;
            details.Clear();
        }

        /// <summary>恢复可操作状态。open 的状态由分片结果决定，这里不动它。</summary>
        private void EndSplitUi()
        {
            progress.Style = ProgressBarStyle.Blocks;
            progress.Value = open.Enabled ? 100 : 0;
            inputPanel.Enabled = true;
            settingsButton.Enabled = true;
            start.Enabled = true;
            cancel.Enabled = false;
        }

        /// <summary>
        /// 覆盖模式下先请用户确认，并明确告知会写到哪个目录。
        /// </summary>
        /// <returns>false 表示用户取消或目标目录无法推算。</returns>
        private bool ConfirmOverwrite(string input, AppSettings runSettings)
        {
            if (!runSettings.OverwriteExisting) return true;
            string target;
            try { target = Splitter.SuggestedDirectory(Splitter.NormalizePath(input), runSettings); }
            catch (Exception ex) { details.Text = ex.Message; return false; }
            return MessageBox.Show(this, "本次分片将覆盖以下目录中的同名文件：\r\n" + target +
                "\r\n\r\n原内容将被永久替换，不会进入回收站。确认开始？",
                "确认覆盖同名文件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        /// <summary>分片成功后填写状态栏和详情框。</summary>
        private void ReportSuccess(string input, SplitResult result)
        {
            lastOutput = result.DirectoryPath;
            output.Text = FormatOutputDirectory(input, lastOutput);
            status.Text = "完成：已生成 " + result.FileCount + " 个 PDF" + (result.Warnings.Length > 0 ? "（有 qpdf 提示，见下方）" : "");
            details.Text = "输出文件夹：" + lastOutput + "\r\n输出数量：" + result.FileCount + " 个 PDF，共 " + result.PageCount +
                " 页\r\n" + String.Join("\r\n", result.FileNames) +
                (result.Warnings.Length > 0 ? "\r\n\r\nqpdf 提示：\r\n" + result.Warnings : "");
            open.Enabled = true;
        }

        /// <summary>
        /// 执行一次分片：校验范围 → 确认覆盖 → 锁界面 → 后台调用 <see cref="Splitter"/> → 汇报结果。
        /// 分片用的是设置的一份快照，过程中改设置不会影响本次运行。
        /// </summary>
        private async Task StartSplit()
        {
            if (cancellation != null) return;
            if (String.IsNullOrWhiteSpace(source.Text)) { BrowsePdf(); if (String.IsNullOrWhiteSpace(source.Text)) return; }
            NormalizePageRange();
            int first, last;
            if (!IsRangePending && ValidatePageRange(out first, out last).Length > 0)
            {
                UpdateOutputPreview();
                status.Text = "请检查分片范围";
                return;
            }
            string input = source.Text, secret = password.Text;
            AppSettings runSettings = settings.Copy();
            if (!ConfirmOverwrite(input, runSettings)) return;

            cancellation = new CancellationTokenSource();
            BeginSplitUi();
            var reporter = new Progress<string>(s => { if (cancellation != null && !cancellation.IsCancellationRequested) status.Text = s; });
            try
            {
                CancellationToken token = cancellation.Token;
                CancelPageCountRead();
                // A split may be requested before the asynchronous preview read finishes.
                if (!totalPages.HasValue)
                {
                    status.Text = "正在读取 PDF 页数…";
                    int total = await Task.Run(() => Splitter.GetPageCount(input, runSettings.QpdfPath, secret, token));
                    SetPageCount(total);
                }
                // 读页数可能刚刚纠正过范围，因此在此刻才取最终的起止页。
                runSettings.StartPage = settings.StartPage;
                runSettings.EndPage = settings.EndPage;
                SplitResult result = await Task.Run(() => Splitter.Split(input, runSettings.QpdfPath, secret, token, s => ((IProgress<string>)reporter).Report(s), runSettings));
                CancelPageCountRead();
                SetPageCount(result.TotalPageCount);
                ReportSuccess(input, result);
            }
            catch (OperationCanceledException) { status.Text = "已取消"; details.Text = "本次临时文件已清理。"; }
            catch (Exception ex) { status.Text = "分片未完成"; details.Text = ex.Message; }
            finally
            {
                // 用完即清除密码：内存里的副本和界面上的明文都不再保留。
                secret = null;
                clearingPassword = true;
                try { password.Clear(); }
                finally { clearingPassword = false; }
                EndSplitUi();
                cancellation.Dispose();
                cancellation = null;
                if (closeAfterCancel) Close();
            }
        }
    }
}
