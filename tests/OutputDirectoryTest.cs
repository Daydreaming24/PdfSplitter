using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PdfPageSplitter;

public static class OutputDirectoryTest
{
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void CheckClean(string root)
    {
        if (Directory.GetDirectories(root, ".pdf-splitter-*", SearchOption.AllDirectories).Length != 0)
            throw new Exception("Private staging directory was left behind.");
    }

    public static int Main(string[] args)
    {
        return TestDiagnostics.Run(() => Run(args));
    }

    private static int Run(string[] args)
    {
        string root = TestDiagnostics.CreateWorkDirectory("output");
        try
        {
            string input = Path.Combine(root, "source.pdf");
            File.Copy(args[0], input);
            string qpdf = Splitter.FindQpdf();
            string automatic = Splitter.SuggestedDirectory(input);
            Directory.CreateDirectory(automatic);
            int reports = 0;
            Expect<IOException>(() => Splitter.Split(input, qpdf, "", CancellationToken.None, s => reports++));
            if (reports != 0 || Directory.GetFileSystemEntries(automatic).Length != 0 || Directory.Exists(automatic + "_2"))
                throw new Exception("An existing empty folder should stop automatic mode before qpdf.");
            Console.WriteLine("PASS: even an empty automatic output directory stops the run immediately");

            string raceInput = Path.Combine(root, "race.pdf");
            File.Copy(input, raceInput);
            string raceFolder = Splitter.SuggestedDirectory(raceInput);
            Expect<IOException>(() => Splitter.Split(raceInput, qpdf, "", CancellationToken.None, s => {
                if (!s.StartsWith("正在整理")) return;
                Directory.CreateDirectory(raceFolder);
                File.WriteAllText(Path.Combine(raceFolder, "keep.txt"), "keep");
            }));
            if (Directory.GetFileSystemEntries(raceFolder).Length != 1 || File.ReadAllText(Path.Combine(raceFolder, "keep.txt")) != "keep" || Directory.Exists(raceFolder + "_2"))
                throw new Exception("A late automatic-directory conflict was changed or bypassed.");
            CheckClean(root);
            Console.WriteLine("PASS: automatic mode preserves a directory created while qpdf was running");

            string existing = Path.Combine(root, "existing");
            Directory.CreateDirectory(existing);
            string lateConflict = Path.Combine(existing, "source_page2.pdf");
            bool saved = false;
            Expect<IOException>(() => Splitter.Split(input, qpdf, "", CancellationToken.None, s => {
                if (!s.StartsWith("正在保存")) return;
                saved = true;
                File.WriteAllText(lateConflict, "existing content");
            }, new AppSettings { EndPage = 2 }, existing));
            if (!saved || Directory.GetFileSystemEntries(existing).Length != 1 || File.ReadAllText(lateConflict) != "existing content")
                throw new Exception("A late page-name conflict changed the existing directory.");
            CheckClean(root);
            Console.WriteLine("PASS: existing-folder conflicts introduced after splitting preserve all original contents");

            string cancelled = Path.Combine(root, "cancelled");
            Directory.CreateDirectory(cancelled);
            File.WriteAllText(Path.Combine(cancelled, "keep.txt"), "keep");
            using (var token = new CancellationTokenSource())
            {
                Expect<OperationCanceledException>(() => Splitter.Split(input, qpdf, "", token.Token, s => {
                    if (s.StartsWith("正在拆分") && Directory.GetDirectories(cancelled, ".pdf-splitter-*").Length != 1)
                        throw new Exception("Existing mode must stage inside the selected destination.");
                    if (s.StartsWith("正在保存")) token.Cancel();
                }, null, cancelled));
            }
            if (Directory.GetFileSystemEntries(cancelled).Length != 1 || File.ReadAllText(Path.Combine(cancelled, "keep.txt")) != "keep")
                throw new Exception("Cancellation changed existing contents.");
            CheckClean(root);
            Console.WriteLine("PASS: cancellation before publication cleans destination-local staging and preserves existing contents");

            // Lock the second staged file to force an OS failure after the first move.
            // Exercise the publication routine directly so the lock can be released before staging cleanup.
            string ready = Path.Combine(root, "ready");
            string rollback = Path.Combine(root, "rollback");
            Directory.CreateDirectory(ready);
            Directory.CreateDirectory(rollback);
            File.WriteAllText(Path.Combine(ready, "first.pdf"), "first");
            File.WriteAllText(Path.Combine(ready, "second.pdf"), "second");
            File.WriteAllText(Path.Combine(rollback, "keep.txt"), "keep");
            MethodInfo publish = typeof(Splitter).GetMethod("PublishToExistingDirectory", BindingFlags.NonPublic | BindingFlags.Static);
            using (var locked = new FileStream(Path.Combine(ready, "second.pdf"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Expect<IOException>(() => {
                    try { publish.Invoke(null, new object[] { ready, rollback, new[] { "first.pdf", "second.pdf" }, CancellationToken.None }); }
                    catch (TargetInvocationException ex) { throw ex.InnerException; }
                });
            }
            if (Directory.GetFileSystemEntries(rollback).Length != 1 || File.ReadAllText(Path.Combine(rollback, "keep.txt")) != "keep" ||
                File.ReadAllText(Path.Combine(ready, "first.pdf")) != "first" || File.ReadAllText(Path.Combine(ready, "second.pdf")) != "second")
                throw new Exception("Partial publication was not rolled back.");
            if (!File.ReadAllBytes(input).SequenceEqual(File.ReadAllBytes(args[0]))) throw new Exception("Input changed.");
            Console.WriteLine("PASS: a failure on the second move rolls back earlier output and preserves existing files");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
