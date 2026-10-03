using System;
using System.IO;
using System.Threading;
using PdfPageSplitter;

public static class CancellationTest
{
    private static void CheckCancellation(string input, string root, string destination)
    {
        using (var token = new CancellationTokenSource())
        {
            bool scheduled = false;
            try
            {
                Splitter.Split(input, Splitter.FindQpdf(), "", token.Token, status => {
                    if (!status.StartsWith("正在拆分")) return;
                    scheduled = true;
                    ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(20); token.Cancel(); });
                }, new AppSettings { EndPage = Splitter.GetPageCount(input, "", "", CancellationToken.None) }, destination);
                throw new Exception("The operation completed before cancellation could be tested.");
            }
            catch (OperationCanceledException)
            {
                if (!scheduled) throw new Exception("Cancellation was not scheduled during splitting.");
                if (Directory.GetDirectories(root, ".pdf-splitter-*", SearchOption.AllDirectories).Length != 0)
                    throw new Exception("Cancellation left staging folders.");
                if (Directory.Exists(Splitter.SuggestedDirectory(input))) throw new Exception("Cancellation left automatic output.");
                if (!File.Exists(input)) throw new Exception("Input file is missing.");
                if (destination != null && (Directory.GetFileSystemEntries(destination).Length != 1 ||
                    File.ReadAllText(Path.Combine(destination, "keep.txt")) != "keep"))
                    throw new Exception("Cancellation changed existing contents.");
                Console.WriteLine("PASS: cancellation during qpdf execution; staging removed and input preserved (" +
                    (destination == null ? "automatic" : "existing directory") + ")");
            }
        }
    }

    public static int Main(string[] args)
    {
        return TestDiagnostics.Run(() => Run(args));
    }

    private static int Run(string[] args)
    {
        string root = TestDiagnostics.CreateWorkDirectory("cancel");
        try
        {
            string input = Path.Combine(root, "cancel.pdf");
            File.Copy(args[0], input);
            CheckCancellation(input, root, null);
            string existing = Path.Combine(root, "existing");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");
            CheckCancellation(input, root, existing);
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
