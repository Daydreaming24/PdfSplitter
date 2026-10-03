using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PdfPageSplitter;

public static class OutputPolicyTest
{
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    public static int Main(string[] args)
    {
        return TestDiagnostics.Run(() => Run(args));
    }

    private static int Run(string[] args)
    {
        string root = TestDiagnostics.CreateWorkDirectory("policy");
        try
        {
            string input = Path.Combine(root, "source.pdf");
            File.Copy(args[0], input);
            string ready = Path.Combine(root, "ready");
            // Keep the long-path regression independent of where the executable is built.
            while (ready.Length < 270) ready = Path.Combine(ready, new string('r', 40));
            string output = Path.Combine(root, "output");
            Directory.CreateDirectory(ready);
            Directory.CreateDirectory(output);
            foreach (string name in new[] { "first.pdf", "new.pdf", "locked.pdf" }) File.WriteAllText(Path.Combine(ready, name), "new " + name);
            File.WriteAllText(Path.Combine(output, "first.pdf"), "original first");
            File.WriteAllText(Path.Combine(output, "locked.pdf"), "original locked");
            File.WriteAllText(Path.Combine(output, "keep.txt"), "keep");
            MethodInfo publish = typeof(Splitter).Assembly.GetType("PdfPageSplitter.OutputPublisher").GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            using (var locked = new FileStream(Path.Combine(ready, "locked.pdf"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Expect<IOException>(() => {
                    try { publish.Invoke(null, new object[] { ready, output, new[] { "first.pdf", "new.pdf", "locked.pdf" }, input,
                        CollisionPolicy.Stop, true, CancellationToken.None, NumberingStyle.NextAvailable }); }
                    catch (TargetInvocationException ex) { throw ex.InnerException; }
                });
            }
            if (File.ReadAllText(Path.Combine(output, "first.pdf")) != "original first" ||
                File.ReadAllText(Path.Combine(output, "locked.pdf")) != "original locked" ||
                File.Exists(Path.Combine(output, "new.pdf")) || Directory.GetFiles(output).Length != 3)
                throw new Exception("Overwrite rollback did not restore original contents and remove new output.");
            Console.WriteLine("PASS: long staging paths and partial publication restore original contents");

            var settings = new AppSettings { Mode = SplitMode.PageRange, StartPage = 1, EndPage = 2, RangeCollision = CollisionPolicy.Rename, Numbering = NumberingStyle.BatchMaximum };
            File.WriteAllText(Path.Combine(output, settings.RangeName("source")), "original");
            var result = Splitter.Split(input, Splitter.FindQpdf(), "", CancellationToken.None, status => {
                if (status.StartsWith("正在保存")) File.WriteAllText(Path.Combine(output, "source_pages1-2 (8).pdf"), "late");
            }, settings, output);
            if (result.FileCount != 1 || result.PageCount != 2 || result.FileNames[0] != "source_pages1-2 (9).pdf")
                throw new Exception("Maximum numbering was not recomputed before publication.");
            Console.WriteLine("PASS: maximum numbering rescans late conflicts before publication");

            settings.Numbering = NumberingStyle.NextAvailable;
            result = Splitter.Split(input, Splitter.FindQpdf(), "", CancellationToken.None, status => {
                if (status.StartsWith("正在保存")) File.WriteAllText(Path.Combine(output, "source_pages1-2 (1).pdf"), "late");
            }, settings, output);
            if (result.FileNames[0] != "source_pages1-2 (2).pdf") throw new Exception("Next numbering did not handle late conflict.");
            Console.WriteLine("PASS: next-available numbering handles late conflicts");

            using (var cancellation = new CancellationTokenSource())
            {
                Expect<OperationCanceledException>(() => Splitter.Split(input, Splitter.FindQpdf(), "", cancellation.Token, status => {
                    if (status.StartsWith("正在整理")) cancellation.Cancel();
                }, settings, output));
            }
            if (Directory.GetDirectories(root, ".pdf-splitter-*", SearchOption.AllDirectories).Length != 0) throw new Exception("Staging left after cancellation.");
            if (!File.ReadAllBytes(input).SequenceEqual(File.ReadAllBytes(args[0]))) throw new Exception("Source modified.");
            Console.WriteLine("PASS: range cancellation removes staging and preserves originals");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
