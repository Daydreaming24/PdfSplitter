using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PdfPageSplitter;

public static class HomePageTest
{
    private static string stage = "startup";

    private static void Stage(string value)
    {
        stage = value;
        Console.WriteLine("RUN: " + value);
    }

    private static IEnumerable<Control> Controls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Controls(child)) yield return descendant;
        }
    }

    private static Control Find(Control form, string name)
    {
        return Controls(form).Single(c => c.AccessibleName == name);
    }

    private static void Wait(Func<bool> ready, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Timed out waiting for the UI at line " + line + ".");
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static object Call(MainForm form, string name)
    {
        return typeof(MainForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
    }

    private static AppSettings Settings(MainForm form)
    {
        return (AppSettings)typeof(MainForm).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
    }

    private static bool Exited(int pid)
    {
        try { using (var process = Process.GetProcessById(pid)) return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // DoEvents can enter a nested modal loop, so Wait's UI-thread timeout alone
        // cannot bound the test. Keep an independent deadline with the current stage.
        using (var watchdog = new System.Threading.Timer(delegate {
            Console.Error.WriteLine("HomePageTest timed out during: " + stage);
            Environment.Exit(1);
        }, null, TimeSpan.FromSeconds(90), System.Threading.Timeout.InfiniteTimeSpan))
        {
            try { return Run(args); }
            catch (Exception ex) { Console.Error.WriteLine("Stage: " + stage); TestDiagnostics.Report(ex); return 1; }
        }
    }

    private static int Run(string[] args)
    {
        // A slow fake qpdf makes stale-result and process-cleanup checks deterministic.
        if (args.Length > 0 && args[0] == "--password-file=-")
        {
            Console.ReadLine();
            if (Path.GetFileName(args[1]) == "slow.pdf")
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "slow.pid"), Process.GetCurrentProcess().Id.ToString());
                Thread.Sleep(3000);
                Console.WriteLine("9");
            }
            else Console.WriteLine("17");
            return 0;
        }
        // Surface UI callback errors as failures instead of opening an unattended dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string input = Path.GetFullPath(args[0]);
        string encrypted = Path.GetFullPath(args[1]);
        string root = AppDomain.CurrentDomain.BaseDirectory;
        if (File.Exists(AppSettings.DefaultPath) || File.Exists(Path.Combine(root, "MainForm.cs")))
            throw new Exception("Run this test from a fresh isolated directory containing a copy of PdfSplitter.exe.");
        new AppSettings().Save(AppSettings.DefaultPath);
        Stage("page count and range editing");
        using (var main = new MainForm(input))
        {
            main.ShowInTaskbar = false;
            main.StartPosition = FormStartPosition.Manual;
            main.Location = new System.Drawing.Point(-32000, -32000);
            main.Show();
            Control count = Find(main, "PDF 总页数");
            Control source = Find(main, "PDF 文件路径");
            Control password = Find(main, "PDF 打开密码（无密码留空）");
            Wait(() => count.Text == "12 页");
            Control first = Find(main, "起始页"), last = Find(main, "结束页");
            var start = Controls(main).OfType<Button>().Single(c => c.Text == "开始分片");
            if (first.Text != "1" || last.Text != "12") throw new Exception("Loading a PDF should select all pages.");
            var rangeHint = Find(main, "分片范围提示");
            var outputHint = Find(main, "输出方式提示");
            if (rangeHint.Visible || outputHint.Visible || rangeHint.Text.Length != 0 || outputHint.Text.Length != 0)
                throw new Exception("Routine hints should be hidden.");
            var inputs = (TableLayoutPanel)rangeHint.Parent;
            if (inputs.RowStyles[6].Height != 0 || inputs.RowStyles[9].Height != 0)
                throw new Exception("Hidden hints should not reserve space.");
            if (!first.Enabled || !last.Enabled) throw new Exception("Range inputs must be enabled in per-page mode.");
            ((RadioButton)Find(main, "块状分片")).Checked = true;
            if (!first.Enabled || !last.Enabled) throw new Exception("Home range inputs were not enabled.");
            first.Text = "2";
            last.Text = "5";
            if (!start.Enabled || !Find(main, "切片文件名预览").Text.EndsWith("_pages2-5.pdf"))
                throw new Exception("Home range preview did not update.");
            foreach (string invalid in new[] { "", "1.5", "+", "-", "1e2", "999999999999999999999999x", "abc" })
            {
                first.Text = invalid;
                if (start.Enabled || !rangeHint.Visible || !rangeHint.Text.Contains("整数") ||
                    Find(main, "切片文件名预览").Text.EndsWith("_pages2-5.pdf"))
                    throw new Exception("Invalid home range was accepted: " + invalid);
                var saved = AppSettings.Load(AppSettings.DefaultPath);
                if (saved.StartPage != 2 || saved.EndPage != 5) throw new Exception("Invalid range overwrote the saved range.");
            }
            var rejected = (Task)Call(main, "StartSplit");
            if (!rejected.IsCompleted || Directory.GetFiles(root, "*_pages*.pdf").Length != 0)
                throw new Exception("Invalid range started a split using stale page numbers.");
            ((RadioButton)Find(main, "每页分片")).Checked = true;
            if (start.Enabled || !first.Enabled || !last.Enabled) throw new Exception("Invalid range must block per-page mode.");
            rejected = (Task)Call(main, "StartSplit");
            if (!rejected.IsCompleted) throw new Exception("Invalid per-page range started a split.");
            ((RadioButton)Find(main, "块状分片")).Checked = true;
            if (first.Text != "abc" || start.Enabled) throw new Exception("Mode switching discarded an unfinished range.");
            first.Text = "5";
            if (rangeHint.Visible || inputs.RowStyles[6].Height != 0) throw new Exception("Resolved warning should collapse.");
            if (!start.Enabled || !Find(main, "切片文件名预览").Text.EndsWith("_pages5-5.pdf"))
                throw new Exception("Single-page range was rejected.");
            string[,] corrections = {
                { "0", "200", "1", "12" },
                { "-10", "-2", "1", "1" },
                { "200", "5", "5", "12" },
                { "9", "2", "2", "9" },
                { "2147483648", "999999999999999999999999999999999999", "12", "12" },
                { "-999999999999999999999999999999999999", " +0005 ", "1", "5" },
                { "12", "12", "12", "12" },
                { "2", "5", "2", "5" }
            };
            for (int row = 0; row < corrections.GetLength(0); row++)
            {
                first.Focus();
                first.Text = corrections[row, 0];
                last.Text = corrections[row, 1];
                // Finish editing via an actual focus change (Leave event).
                source.Focus();
                if (first.Text != corrections[row, 2] || last.Text != corrections[row, 3] || !start.Enabled || rangeHint.Visible)
                    throw new Exception("Range correction failed for " + corrections[row, 0] + " to " + corrections[row, 1]);
                var saved = AppSettings.Load(AppSettings.DefaultPath);
                if (saved.StartPage.ToString() != first.Text || saved.EndPage.ToString() != last.Text ||
                    !Find(main, "切片文件名预览").Text.EndsWith("_pages" + first.Text + "-" + last.Text + ".pdf"))
                    throw new Exception("Corrected range, preview and saved settings disagree.");
            }
            first.Focus();
            first.Text = "0";
            last.Text = "200";
            typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(first, new object[] { new KeyEventArgs(Keys.Enter) });
            if (first.Text != "1" || last.Text != "12") throw new Exception("Enter should correct the range.");
            first.Text = "-2";
            last.Text = "";
            source.Focus();
            if (first.Text != "1" || start.Enabled || !rangeHint.Visible)
                throw new Exception("A numeric endpoint should be corrected even when the other is empty.");
            first.Text = "2";
            last.Text = "5";
            if (AppSettings.Load(AppSettings.DefaultPath).Mode != SplitMode.PageRange || Find(main, "输出文件夹路径").Text != @".\")
                throw new Exception("Home mode switch did not save or update output.");
            using (var reopened = new MainForm(null))
            {
                Call(reopened, "NormalizePageRange");
                if (!((RadioButton)Find(reopened, "块状分片")).Checked || Find(reopened, "起始页").Text.Length != 0 ||
                    Find(reopened, "结束页").Text.Length != 0 || Find(reopened, "分片范围提示").Text.Length != 0 ||
                    Find(reopened, "切片文件名预览").Text.Length != 0)
                    throw new Exception("Startup should restore the mode and leave the range blank without a warning.");
                var saved = AppSettings.Load(AppSettings.DefaultPath);
                if (saved.StartPage != 2 || saved.EndPage != 5) throw new Exception("Blank startup range changed saved settings.");
            }
            ((RadioButton)Find(main, "每页分片")).Checked = true;
            if (Settings(main).Mode != SplitMode.EveryPage || !Find(main, "切片文件名预览").Text.Contains("_page2.pdf"))
                throw new Exception("Per-page mode did not update the preview.");
            source.Text = Path.Combine(root, "missing.pdf");
            if (count.Text == "12 页") throw new Exception("Stale count was not cleared immediately.");
            Wait(() => count.Text.Contains("找不到 PDF"));
            source.Text = "";
            if (!count.Text.Contains("尚未选择") || first.Text.Length != 0 || last.Text.Length != 0 || rangeHint.Text.Length != 0)
                throw new Exception("Empty source should clear the count and range without a warning.");
            Stage("password retries");
            source.Text = encrypted;
            Wait(() => count.Text.Contains("打开密码"));
            password.Text = "wrong";
            Wait(() => count.Text.Contains("打开密码"));
            password.Text = "中文 test & password";
            Wait(() => count.Text == "7 页");
            if (first.Text != "1" || last.Text != "7") throw new Exception("Unlocking a new PDF should select all its pages.");
            var settings = Settings(main);
            ((RadioButton)Find(main, "块状分片")).Checked = true;
            first.Text = "3";
            last.Text = "6";
            settings.OutputDirectory = Path.Combine(root, "split-output");
            Directory.CreateDirectory(settings.OutputDirectory);
            Call(main, "UpdateOutputPreview");
            Stage("encrypted range split");
            var split = (Task)Call(main, "StartSplit");
            Wait(() => split.IsCompleted);
            split.GetAwaiter().GetResult();
            if (count.Text != "7 页" || password.Text.Length != 0 || Directory.GetFiles(settings.OutputDirectory, "*.pdf").Length != 1)
                throw new Exception("Encrypted range split must retain source total after clearing password.");
            string rangeOutput = Path.Combine(settings.OutputDirectory, Path.GetFileNameWithoutExtension(encrypted) + "_pages3-6.pdf");
            if (!File.Exists(rangeOutput) || PdfPageSplitter.Splitter.GetPageCount(rangeOutput, "", "", CancellationToken.None) != 4)
                throw new Exception("Split did not use the range entered on the home page.");
            Stage("encrypted per-page split");
            ((RadioButton)Find(main, "每页分片")).Checked = true;
            password.Text = "中文 test & password";
            split = (Task)Call(main, "StartSplit");
            Wait(() => split.IsCompleted);
            split.GetAwaiter().GetResult();
            string stem = Path.GetFileNameWithoutExtension(encrypted);
            if (count.Text != "7 页" || password.Text.Length != 0 || Directory.GetFiles(settings.OutputDirectory, "*.pdf").Length != 5)
                throw new Exception("Per-page split must use the same selected range and retain source total.");
            for (int page = 3; page <= 6; page++)
            {
                string pageOutput = Path.Combine(settings.OutputDirectory, stem + "_page" + page + ".pdf");
                if (!File.Exists(pageOutput) || PdfPageSplitter.Splitter.GetPageCount(pageOutput, "", "", CancellationToken.None) != 1)
                    throw new Exception("Missing selected per-page output: " + page);
            }
            Stage("split before page count completes");
            settings.OutputDirectory = Path.Combine(root, "corrected-output");
            Directory.CreateDirectory(settings.OutputDirectory);
            // Change the password to invalidate the count, then split before the timer fires.
            password.Text = "中文 test & password";
            first.Text = "99";
            last.Text = "3";
            split = (Task)Call(main, "StartSplit");
            Wait(() => split.IsCompleted);
            split.GetAwaiter().GetResult();
            if (first.Text != "3" || last.Text != "7" || Directory.GetFiles(settings.OutputDirectory, "*.pdf").Length != 5 ||
                !File.Exists(Path.Combine(settings.OutputDirectory, stem + "_page7.pdf")))
                throw new Exception("Starting before the count is ready should clamp and swap before publication.");
            last.Text = "6";
            first.Text = "6";
            if (Find(main, "切片文件名预览").Text != stem + "_page6.pdf")
                throw new Exception("Single-page preview includes pages outside the selection.");
            Console.WriteLine("PASS: range initialization, clamping, swapping, integer overflow, Enter/Leave, warning layout, preview, persistence and both split modes");
            source.Text = Path.Combine(root, "invalid.pdf");
            File.WriteAllText(source.Text, "Invalid PDF");
            Wait(() => count.Text.Contains("qpdf 处理失败"));
            settings.QpdfPath = Path.Combine(root, "missing.exe");
            source.Text = input;
            Wait(() => count.Text.Contains("找不到 qpdf"));
            settings.QpdfPath = "";
            Call(main, "SchedulePageCount");
            Wait(() => count.Text == "12 页");
            Console.WriteLine("PASS: source total, persisted home switch, empty/missing input, password retry, encrypted range total, invalid PDF, qpdf recovery");

            Stage("cancel old page reads and close the form");
            string slow = Path.Combine(root, "slow.pdf");
            File.Copy(input, slow);
            settings.QpdfPath = Assembly.GetExecutingAssembly().Location;
            source.Text = slow;
            string pidPath = Path.Combine(root, "slow.pid");
            Wait(() => File.Exists(pidPath) && new FileInfo(pidPath).Length > 0);
            int oldPid = Int32.Parse(File.ReadAllText(pidPath));
            source.Text = input;
            Wait(() => count.Text == "17 页");
            if (first.Text != "1" || last.Text != "17") throw new Exception("Latest source should initialize its own full range.");
            Wait(() => Exited(oldPid));
            if (count.Text != "17 页") throw new Exception("Old result replaced the current count.");
            File.Delete(pidPath);
            source.Text = slow;
            Wait(() => File.Exists(pidPath) && new FileInfo(pidPath).Length > 0);
            int closingPid = Int32.Parse(File.ReadAllText(pidPath));
            main.Close();
            Wait(() => Exited(closingPid));
            Wait(() => main.IsDisposed);
            Console.WriteLine("PASS: changing source cancels old qpdf, latest count wins, closing form stops pending qpdf");
        }
        Stage("split immediately after load");
        string earlyOutput = Path.Combine(root, "early-output");
        Directory.CreateDirectory(earlyOutput);
        new AppSettings { OutputDirectory = earlyOutput }.Save(AppSettings.DefaultPath);
        using (var early = new MainForm(input))
        {
            var task = (Task)Call(early, "StartSplit");
            Wait(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
            if (Find(early, "起始页").Text != "1" || Find(early, "结束页").Text != "12" ||
                Directory.GetFiles(earlyOutput, "*.pdf").Length != 12)
                throw new Exception("Splitting immediately after load should initialize and split the full PDF.");
        }
        Console.WriteLine("PASS: immediate splitting initializes the full range before writing files");
        return 0;
    }
}
