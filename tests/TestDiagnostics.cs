using System;
using System.Configuration;
using System.IO;

internal static class TestDiagnostics
{
    public static string CreateWorkDirectory(string prefix)
    {
        string parent = ConfigurationManager.AppSettings["TestWorkDirectory"];
        if (String.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("Build the test with tests/build-tests.ps1 to configure its scratch directory.");
        string directory = Path.Combine(parent, prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static int Run(Func<int> test)
    {
        try { return test(); }
        catch (Exception error) { Report(error); return 1; }
    }

    public static void Report(Exception error)
    {
        // Framework stack-trace formatting can itself fail for assemblies in deep paths.
        // Print the useful exception information before attempting that formatting.
        Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
        try { Console.Error.WriteLine(error.StackTrace); }
        catch (Exception formatting) { Console.Error.WriteLine("Stack trace unavailable: " + formatting.GetType().Name); }
        if (error.InnerException != null) Report(error.InnerException);
    }
}
