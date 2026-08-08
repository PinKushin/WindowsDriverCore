namespace WindowsDriverCore.Host;

/// <summary>
/// Entry point. Parses WinAppDriver-compatible command line arguments, wires the
/// composition root, and starts Kestrel.
/// </summary>
public static class Program
{
    /// <summary>Runs the driver.</summary>
    /// <param name="args">
    /// WinAppDriver-compatible forms: none, <c>[port]</c>, <c>[ip] [port]</c>, or
    /// <c>[ip] [port]/base/path</c>. <c>*</c> as the address binds all interfaces.
    /// </param>
    public static void Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        throw new NotImplementedException("Milestone 1 scaffold: no routes are mapped yet.");
    }
}
