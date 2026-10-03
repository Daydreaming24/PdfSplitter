using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using PdfPageSplitter;

public static class OverwriteConfirmationTest
{
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Answer(string title, int answer, Action action)
    {
        bool seen = false, defaultNo = false;
        using (var timer = new Timer { Interval = 30 })
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            timer.Tick += delegate {
                IntPtr window = GetActiveWindow();
                var caption = new StringBuilder(256);
                GetWindowText(window, caption, caption.Capacity);
                if (caption.ToString() == title)
                {
                    seen = true;
                    defaultNo = (SendMessage(window, 0x0400, IntPtr.Zero, IntPtr.Zero).ToInt64() & 0xffff) == 7;
                    timer.Stop();
                    SendMessage(window, 0x0111, new IntPtr(answer), IntPtr.Zero);
                }
                else if (DateTime.UtcNow > deadline)
                {
                    timer.Stop();
                    SendMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                }
            };
            timer.Start();
            action();
        }
        if (!seen || !defaultNo) throw new Exception("Expected confirmation with No as the default: " + title);
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
        using (var settings = new SettingsForm(new AppSettings(), args[0]))
        {
            var toggle = (CheckBox)Descendants(settings).Single(c => c.AccessibleName == "自动覆盖同名文件");
            Answer("确认启用覆盖", 7, () => toggle.Checked = true);
            if (toggle.Checked) throw new Exception("Declining confirmation enabled overwrite.");
            Answer("确认启用覆盖", 6, () => toggle.Checked = true);
            if (!toggle.Checked || Descendants(settings).Single(c => c.AccessibleName == "自动编号方式").Enabled)
                throw new Exception("Confirmed overwrite was not enabled.");
            toggle.Checked = false;
            if (!Descendants(settings).Single(c => c.AccessibleName == "自动编号方式").Enabled)
                throw new Exception("Disabling overwrite did not restore the numbering controls.");
        }
        using (var main = new MainForm(Path.GetFullPath(args[0])))
        {
            typeof(MainForm).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(main,
                new AppSettings { Mode = SplitMode.PageRange, OverwriteExisting = true });
            Answer("确认覆盖同名文件", 7, () => {
                var task = (Task)typeof(MainForm).GetMethod("StartSplit", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(main, null);
                if (!task.IsCompleted) throw new Exception("Declining confirmation started splitting.");
                task.GetAwaiter().GetResult();
            });
            if (typeof(MainForm).GetField("cancellation", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(main) != null)
                throw new Exception("Declining confirmation created a running job.");
        }
        Console.WriteLine("PASS: enable-overwrite Yes/No branches, default No, and per-run decline prevents splitting");
        return 0;
    }
}
