using System.Diagnostics;

namespace SeekableDeflate.Tests;

/// <summary>Per-step console reporting with timings; every check prints PASS/FAIL and the run exits non-zero on any FAIL.</summary>
public static class TestReport
{
    public static int FailureCount { get; private set; }

    public static void Step(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    public static void Check(bool condition, string what)
    {
        if (!condition) FailureCount++;
        Console.WriteLine($"   {(condition ? "PASS" : "FAIL")}  {what}");
    }

    public static T Timed<T>(string what, Func<T> action)
    {
        var watch = Stopwatch.StartNew();
        T result = action();
        Console.WriteLine($"   {what}: {watch.Elapsed.TotalMilliseconds:0.#} ms");
        return result;
    }

    public static void Timed(string what, Action action) => Timed(what, () => { action(); return 0; });

    public static string OutputDirectory
    {
        get
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestOutput");
            Directory.CreateDirectory(directory);
            return Path.GetFullPath(directory);
        }
    }
}
