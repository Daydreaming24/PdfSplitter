using System.Drawing;
using System.Windows.Forms;

namespace PdfPageSplitter
{
    /// <summary>
    /// 界面配色、字体和间距的唯一来源。
    /// 两个窗体都从这里取值，改动主题不必再逐处搜索 Color.FromArgb 字面量。
    /// </summary>
    internal static class Theme
    {
        public static readonly Color Text = Color.FromArgb(30, 41, 59);
        public static readonly Color MutedText = Color.FromArgb(100, 116, 139);
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color Danger = Color.FromArgb(185, 28, 28);
        public static readonly Color Window = Color.FromArgb(246, 248, 252);
        public static readonly Color Surface = Color.White;
        public static readonly Color Border = Color.FromArgb(203, 213, 225);

        public const string FontFamily = "Microsoft YaHei UI";
        public const float FontSize = 10F;
        public const float TitleFontSize = 18F;

        /// <summary>按 96 DPI 设计的基准尺寸；窗体通过 AutoScaleMode.Dpi 自动缩放。</summary>
        public const float DesignDpi = 96F;

        /// <summary>统一两个窗体的基础外观（字体、配色、DPI 缩放基准）。</summary>
        public static void Apply(Form form)
        {
            form.Font = new Font(FontFamily, FontSize);
            form.BackColor = Window;
            form.ForeColor = Text;
        }
    }

    /// <summary>
    /// 控件工厂。集中默认的边距和对齐方式，使两个窗体的同类控件保持一致。
    /// </summary>
    internal static class Ui
    {
        public static Button Button(string text, int width)
        {
            return new Button { Text = text, Width = width, Height = 36, Margin = new Padding(0, 0, 8, 0), UseVisualStyleBackColor = true };
        }

        public static Label Label(string text, bool muted = false)
        {
            return new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = muted ? Theme.MutedText : Theme.Text };
        }

        public static void Primary(Button button)
        {
            button.BackColor = Theme.Accent;
            button.ForeColor = Theme.Surface;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
        }

        /// <summary>
        /// 让单行控件在 TableLayoutPanel 的单元格内左右拉伸、上下居中。
        /// 单行 TextBox 和 ComboBox 的高度由字体决定，Dock.Fill 无法拉伸它们；
        /// 改用 Left|Right 锚定后由布局自行居中，无需写死随 DPI 失效的上边距。
        /// </summary>
        public static void Stretch(Control control, int left = 3, int right = 0)
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            control.Margin = new Padding(left, 0, right, 0);
        }

        /// <summary>把若干控件横向排成一行，用于单元格内的组合控件。</summary>
        public static FlowLayoutPanel Flow(params Control[] controls)
        {
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            flow.Controls.AddRange(controls);
            return flow;
        }

        /// <summary>依次添加列样式：正数为绝对宽度，负数表示按百分比占满剩余空间。</summary>
        public static void Columns(TableLayoutPanel table, params float[] widths)
        {
            foreach (float width in widths)
                table.ColumnStyles.Add(width < 0 ? new ColumnStyle(SizeType.Percent, -width) : new ColumnStyle(SizeType.Absolute, width));
        }
    }
}
