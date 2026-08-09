using BenchmarkDotNet.Running;

namespace WindowsDriverCore.Benchmarks;

/// <summary>
/// Benchmark entry point, with a smoke path.
/// </summary>
/// <remarks>
/// <para>
/// <b>This launches Calculator and drives it.</b> Run it on a machine nobody is
/// using: BenchmarkDotNet takes many iterations per case, and a busy machine does
/// not just make the numbers slow, it makes them wrong.
/// </para>
/// <para>
/// <c>dotnet run -c Release --project bench/WindowsDriverCore.Benchmarks -- --filter *</c>
/// </para>
/// <para>
/// <c>--smoke</c> runs the setup and one call of each case directly, printing any
/// exception. BenchmarkDotNet reports a failed case as <c>NA</c> and discards the
/// child process output on cleanup, so a setup that throws looks identical to a
/// benchmark that could not be measured. Run the smoke path first when a case
/// reports NA.
/// </para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--smoke")
        {
            return Smoke();
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

        return 0;
    }

    private static int Smoke()
    {
        FindBenchmarks benchmarks = new();

        try
        {
            Console.WriteLine("setup...");
            benchmarks.Setup();

            Console.WriteLine($"ours   find  -> {benchmarks.FindThroughThisDriver()} matches");
            Console.WriteLine($"FlaUI  find  -> {benchmarks.FindThroughFlaUi()}");
            Console.WriteLine($"ours   read  -> '{benchmarks.ReadThroughThisDriver()}'");
            Console.WriteLine($"FlaUI  read  -> '{benchmarks.ReadThroughFlaUi()}'");
            Console.WriteLine("smoke OK");

            return 0;
        }
        catch (Exception failure)
        {
            Console.WriteLine($"SMOKE FAILED: {failure.GetType().Name}: {failure.Message}");
            Console.WriteLine(failure.StackTrace);

            return 1;
        }
        finally
        {
            benchmarks.Cleanup();
        }
    }
}
