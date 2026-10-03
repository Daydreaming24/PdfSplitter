using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Reflection;
using PdfPageSplitter;

public static class SettingsAndLayoutTest
{
    private static IEnumerable<Control> Controls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Controls(child)) yield return descendant;
        }
    }

    private static void Render(Form form, string path)
    {
        // Realize each selected tab's native child controls before DrawToBitmap.
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
        if (form is MainForm)
        {
            Control count = Controls(form).Single(c => c.AccessibleName == "PDF 总页数");
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (count.Text == "读取中…" && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(10);
            }
        }
        form.PerformLayout();
        Control content = form.Controls[0];
        using (var bitmap = new Bitmap(content.Width, content.Height))
        {
            content.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        form.Hide();
        Console.WriteLine("Rendered " + Path.GetFileName(path) + ": " + form.Width + " x " + form.Height);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        return TestDiagnostics.Run(() => Run(args));
    }

    private static int Run(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MainForm.cs")))
            throw new Exception("Run this test from an isolated directory containing a copy of PdfSplitter.exe.");
        string qa = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(qa);
        string settingsPath = Path.Combine(qa, "roundtrip.xml");
        var settings = new AppSettings { FolderPattern = "{name}_切片", FilePattern = "{name}_p{page}", PageDigits = 3 };
        settings.Save(settingsPath);
        var loaded = AppSettings.Load(settingsPath);
        if (loaded.FolderPattern != settings.FolderPattern || loaded.PageDigits != 3) throw new Exception("Settings roundtrip failed.");
        loaded.FilePattern = "第{page}页_{name}.pdf";
        loaded.Save(settingsPath);
        if (AppSettings.Load(settingsPath).FilePattern != loaded.FilePattern) throw new Exception("Settings replacement failed.");
        Console.WriteLine("PASS: atomic settings save, replacement, and reload");

        using (var empty = new MainForm(null))
        {
            Render(empty, Path.Combine(qa, "main-empty.png"));
            if (Controls(empty).Any(c => (c.AccessibleName == "起始页" || c.AccessibleName == "结束页" ||
                c.AccessibleName == "分片范围提示" || c.AccessibleName == "切片文件名预览") && c.Text.Length != 0))
                throw new Exception("Startup range and dependent preview should remain blank.");
        }
        using (var main = new MainForm(Path.GetFullPath(args[0])))
        {
            Render(main, Path.Combine(qa, "main.png"));
            if (Controls(main).Any(c => c.AccessibleName == "qpdf 程序路径" || c.Text.Contains("每一页，一个 PDF")))
                throw new Exception("Unwanted home-page content.");
            string inputName = Path.GetFileNameWithoutExtension(args[0]);
            var filePreview = Controls(main).Single(c => c.AccessibleName == "切片文件名预览");
            if (!filePreview.Text.Contains(inputName)) throw new Exception("Main preview does not use the input filename.");
            if (!Controls(main).Any(c => c is Button && c.Text == "设置")) throw new Exception("Settings button missing.");
            var output = (TextBox)Controls(main).Single(c => c.AccessibleName == "输出文件夹路径");
            if (!output.ReadOnly) throw new Exception("Output preview should remain read-only.");
            if (Controls(main).OfType<RadioButton>().Count() != 2) throw new Exception("Home mode switch missing.");
            int total = PdfPageSplitter.Splitter.GetPageCount(args[0], "", "", System.Threading.CancellationToken.None);
            if (Controls(main).Single(c => c.AccessibleName == "PDF 总页数").Text != total + " 页")
                throw new Exception("Home page count incorrect.");
            var rangeSettings = new AppSettings { Mode = SplitMode.PageRange, StartPage = 2, EndPage = 5, Numbering = NumberingStyle.BatchMaximum };
            typeof(MainForm).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(main, rangeSettings);
            Controls(main).Single(c => c.AccessibleName == "结束页").Text = "5";
            Controls(main).Single(c => c.AccessibleName == "起始页").Text = "2";
            typeof(MainForm).GetMethod("UpdateOutputPreview", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(main, null);
            if (!filePreview.Text.EndsWith("_pages2-5.pdf") || output.Text != @".\")
                throw new Exception("Range preview or default sibling output incorrect.");
            Render(main, Path.Combine(qa, "main-range.png"));
            main.Size = main.MinimumSize;
            Render(main, Path.Combine(qa, "main-range-minimum.png"));
            ((RadioButton)Controls(main).Single(c => c.AccessibleName == "每页分片")).Checked = true;
            Render(main, Path.Combine(qa, "main-pages-minimum.png"));
            var rangeHint = Controls(main).Single(c => c.AccessibleName == "分片范围提示");
            var outputHint = Controls(main).Single(c => c.AccessibleName == "输出方式提示");
            var inputs = (TableLayoutPanel)rangeHint.Parent;
            if (rangeHint.Text.Length != 0 || outputHint.Text.Length != 0 || inputs.RowStyles[6].Height != 0 || inputs.RowStyles[9].Height != 0)
                throw new Exception("Routine hints should be hidden and collapsed.");
            rangeSettings.OverwriteExisting = true;
            Controls(main).Single(c => c.AccessibleName == "起始页").Text = "abc";
            if (!rangeHint.Text.Contains("整数") || !outputHint.Text.Contains("覆盖") || inputs.RowStyles[6].Height == 0 || inputs.RowStyles[9].Height == 0)
                throw new Exception("Necessary input and overwrite warnings should remain visible.");
            Render(main, Path.Combine(qa, "main-warnings-minimum.png"));
            foreach (Control control in Controls(main).Where(c => c is TextBox || c is Button || c is RadioButton))
                if (!control.Parent.ClientRectangle.Contains(control.Bounds)) throw new Exception("Clipped main control: " + control.AccessibleName);
        }
        using (var dialog = new SettingsForm(new AppSettings(), Path.GetFullPath(args[0])))
        {
            Render(dialog, Path.Combine(qa, "settings.png"));
            if (Controls(dialog).Any(c => c is RadioButton && (c.AccessibleName == "每页分片" || c.AccessibleName == "块状分片")))
                throw new Exception("Mode switch should only appear on the home page.");
            if (Controls(dialog).Any(c => c.AccessibleName == "起始页" || c.AccessibleName == "结束页"))
                throw new Exception("Page range inputs should only appear on the home page.");
            // The warning belongs to the overwrite switch, so it stays away while the switch is off.
            if (Controls(dialog).Single(c => c.AccessibleName == "覆盖警告").Text.Length != 0)
                throw new Exception("Overwrite warning should stay hidden while overwriting is off.");
        }
        using (var dialog = new SettingsForm(new AppSettings { OverwriteExisting = true }, Path.GetFullPath(args[0])))
        {
            Control shownWarning = Controls(dialog).Single(c => c.AccessibleName == "覆盖警告");
            if (!shownWarning.Text.StartsWith("“自动覆盖同名文件”已打开") || !shownWarning.Text.Contains("无法找回") ||
                shownWarning.Text.Contains("\n"))
                throw new Exception("Overwrite warning should name its switch in one line.");
            ((TabControl)Controls(dialog).OfType<TabControl>().Single()).SelectedIndex = 1;
            Render(dialog, Path.Combine(qa, "settings-overwrite.png"));
        }
        using (var dialog = new SettingsForm(new AppSettings { Mode = SplitMode.PageRange, StartPage = 2, EndPage = 5 }, Path.GetFullPath(args[0])))
        {
            Render(dialog, Path.Combine(qa, "settings-range.png"));
            var tabs = Controls(dialog).OfType<TabControl>().Single();
            if (Controls(dialog).Any(c => c.AccessibleName == "起始页" || c.AccessibleName == "结束页"))
                throw new Exception("Page range inputs remain in range-mode settings.");
            var namePreview = Controls(dialog).Single(c => c.AccessibleName == "命名预览");
            if (!namePreview.Text.Contains("pages2-5.pdf"))
                throw new Exception("Settings preview did not use the home-page range.");
            // The dialog defines rules, so its example never names the PDF loaded on the home page.
            if (namePreview.Text.Contains(Path.GetFileNameWithoutExtension(args[0])) ||
                !namePreview.Text.Contains("保存位置：源 PDF") || !namePreview.Text.Contains("生成文件：原文件名_"))
                throw new Exception("Settings preview should stay a labelled placeholder example.");
            var selected = (RadioButton)Controls(dialog).Single(c => c.AccessibleName == "指定输出文件夹");
            var automatic = (RadioButton)Controls(dialog).Single(c => c.AccessibleName == "默认输出位置");
            var output = Controls(dialog).Single(c => c.AccessibleName == "输出文件夹路径");
            selected.Checked = true;
            output.Text = qa;
            automatic.Checked = true;
            if (output.Enabled) throw new Exception("Default output should disable custom path.");
            selected.Checked = true;
            if (output.Text != qa || !output.Enabled) throw new Exception("Custom directory was lost.");
            Render(dialog, Path.Combine(qa, "settings-range.png"));
            dialog.Size = dialog.MinimumSize;
            Render(dialog, Path.Combine(qa, "settings-range-minimum.png"));
            for (int tab = 0; tab < tabs.TabPages.Count; tab++)
            {
                tabs.SelectedIndex = tab;
                Render(dialog, Path.Combine(qa, "settings-tab-" + tab + "-minimum.png"));
                foreach (Control control in Controls(tabs.TabPages[tab]).Where(c => c is TextBox || c is ComboBox || c is Button || c is RadioButton || c is NumericUpDown))
                    if (!control.Parent.ClientRectangle.Contains(control.Bounds)) throw new Exception("Clipped control: " + control.AccessibleName);
            }
            var collision = (ComboBox)Controls(dialog).Single(c => c.AccessibleName == "每页分片同名处理");
            collision.SelectedIndex = 1;
            ((ComboBox)Controls(dialog).Single(c => c.AccessibleName == "自动编号方式")).SelectedIndex = 1;
            var read = (AppSettings)typeof(SettingsForm).GetMethod("ReadFields", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(dialog, null);
            read.Save(settingsPath);
            var roundtrip = AppSettings.Load(settingsPath);
            if (roundtrip.Mode != SplitMode.PageRange || roundtrip.StartPage != 2 || roundtrip.EndPage != 5 ||
                roundtrip.OutputDirectory != qa || roundtrip.PageCollision != CollisionPolicy.Rename || roundtrip.Numbering != NumberingStyle.BatchMaximum ||
                roundtrip.OverwriteExisting) throw new Exception("New settings roundtrip failed.");
            dialog.Show();
            ((Button)Controls(dialog).Single(c => c is Button && c.Text == "恢复默认")).PerformClick();
            dialog.Hide();
            var reset = (AppSettings)typeof(SettingsForm).GetMethod("ReadFields", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(dialog, null);
            if (reset.Mode != SplitMode.PageRange || reset.StartPage != 2 || reset.EndPage != 5) throw new Exception("Settings reset changed the home-page mode or range.");
        }
        using (var dialog = new SettingsForm(new AppSettings(), Path.GetFullPath(args[0])))
        {
            var tabs = Controls(dialog).OfType<TabControl>().Single();
            tabs.SelectedIndex = 2;
            MethodInfo read = typeof(SettingsForm).GetMethod("ReadFields", BindingFlags.NonPublic | BindingFlags.Instance);
            // The dialog shows Chinese placeholders; settings.xml keeps {name}/{page}/{start}/{end}.
            var folderField = (TextBox)Controls(dialog).Single(c => c.AccessibleName == "文件夹命名规则");
            var fileField = (TextBox)Controls(dialog).Single(c => c.AccessibleName == "切片文件命名规则");
            var suffixField = (TextBox)Controls(dialog).Single(c => c.AccessibleName == "块状文件后缀");
            if (folderField.Text != "{原文件名}_clips" || fileField.Text != "{原文件名}_page{页码}" ||
                suffixField.Text != "pages{起始页}-{结束页}") throw new Exception("Naming fields should display the Chinese placeholders.");
            var shown = (AppSettings)read.Invoke(dialog, null);
            if (shown.FolderPattern != "{name}_clips" || shown.FilePattern != "{name}_page{page}" ||
                shown.RangeSuffix != "pages{start}-{end}") throw new Exception("Displayed placeholders did not convert back.");
            // PerformClick only fires on a selectable control, so show the dialog off-screen first.
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-32000, -32000);
            dialog.Show();
            fileField.Text = "第页_{原文件名}";
            fileField.SelectionStart = 1;
            ((Button)Controls(dialog).Single(c => c.AccessibleName == "插入页码")).PerformClick();
            dialog.Hide();
            if (fileField.Text != "第{页码}页_{原文件名}" || fileField.SelectionStart != 5) throw new Exception("Token insertion failed.");
            if (((AppSettings)read.Invoke(dialog, null)).FilePattern != "第{page}页_{name}")
                throw new Exception("Inserted token did not convert back.");
            if (!Controls(dialog).Single(c => c.AccessibleName == "文件名示例").Text.Contains("第1页_") ||
                !Controls(dialog).Single(c => c.AccessibleName == "文件夹名称示例").Text.Contains("_clips") ||
                !Controls(dialog).Single(c => c.AccessibleName == "块状文件名示例").Text.Contains("_pages1-1.pdf"))
                throw new Exception("Naming examples did not follow the fields.");
            Controls(dialog).Single(c => c.AccessibleName == "文件夹命名规则").Text = "{name}_切片";
            Controls(dialog).Single(c => c.AccessibleName == "切片文件命名规则").Text = "第{page}页_{name}.pdf";
            ((NumericUpDown)Controls(dialog).Single(c => c.AccessibleName == "页码位数")).Value = 3;
            string preview = Controls(dialog).Single(c => c.AccessibleName == "命名预览").Text;
            if (!preview.Contains("第001页_")) throw new Exception("Settings preview did not refresh.");
            if (!Controls(dialog).Single(c => c.AccessibleName == "页码写法示例").Text.Contains("001、002"))
                throw new Exception("Page-digit example did not explain the padding.");
            Render(dialog, Path.Combine(qa, "settings-custom.png"));
            // The spinner, the text beside it and the naming fields above all sit on centred rows.
            var spinner = (NumericUpDown)Controls(dialog).Single(c => c.AccessibleName == "页码位数");
            var spinnerHint = Controls(dialog).Single(c => c.AccessibleName == "页码写法示例");
            var namedField = Controls(dialog).Single(c => c.AccessibleName == "切片文件命名规则");
            foreach (Control control in new[] { spinner, spinnerHint, namedField })
                if (Math.Abs((control.Top + control.Height / 2.0) - control.Parent.ClientSize.Height / 2.0) > 2)
                    throw new Exception("Row control is not vertically centred: " + control.AccessibleName);
            Controls(dialog).Single(c => c.AccessibleName == "文件夹命名规则").Text = "../{name}";
            if (Controls(dialog).Single(c => c.AccessibleName == "设置提示").Text.Length == 0) throw new Exception("Missing invalid-pattern feedback.");
            if (Controls(dialog).Single(c => c.AccessibleName == "文件名示例").Text.Length != 0)
                throw new Exception("Examples should clear while the settings are invalid.");
            Controls(dialog).Single(c => c.AccessibleName == "文件夹命名规则").Text = "{原文件名}";
            Controls(dialog).Single(c => c.AccessibleName == "切片文件命名规则").Text = "{原文件名}";
            if (!Controls(dialog).Single(c => c.AccessibleName == "设置提示").Text.Contains("{页码}"))
                throw new Exception("Validation messages should use the displayed placeholders.");
        }
        Console.WriteLine("PASS: main summaries, settings modes, path retention, both numbering policies, XML roundtrip, layout and validation");
        return 0;
    }
}
