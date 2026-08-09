using BenchmarkDotNet.Running;

namespace WindowsDriverCore.Benchmarks;

/// <summary>
/// Benchmark entry point.
/// </summary>
/// <remarks>
/// <para>
/// <b>This launches Calculator and drives it.</b> Run it on a machine nobody is
/// using: BenchmarkDotNet takes many iterations per case, and a busy machine
/// does not just make the numbers slow, it makes them wrong.
/// </para>
/// <para>
/// <c>dotnet run -c Release --project bench/WindowsDriverCore.Benchmarks</c>
/// </para>
/// </remarks>
internal static class Program
{
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
