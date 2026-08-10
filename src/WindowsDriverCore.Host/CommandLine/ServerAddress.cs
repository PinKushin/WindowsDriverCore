using System.Globalization;

namespace WindowsDriverCore.Host.CommandLine;

/// <summary>
/// The address this server listens on, parsed from WinAppDriver-compatible
/// command line arguments.
/// </summary>
/// <param name="Host">
/// Host to bind. <c>*</c> binds every interface, as WinAppDriver documents.
/// </param>
/// <param name="Port">TCP port to bind.</param>
/// <param name="BasePath">
/// Optional base path such as <c>/wd/hub</c>, applied with
/// <c>UsePathBase</c>. Null when the server serves from the root.
/// </param>
public sealed record ServerAddress(string Host, int Port, string? BasePath)
{
    /// <summary>Host bound when no address argument is supplied.</summary>
    public const string DefaultHost = "127.0.0.1";

    /// <summary>Port bound when no port argument is supplied.</summary>
    /// <remarks>
    /// WinAppDriver's port, deliberately. An existing suite has to point at this
    /// driver unchanged, and every Appium and Selenium sample in the world says
    /// 4723 — changing it to avoid collisions would trade the product for a
    /// convenience.
    /// </remarks>
    public const int DefaultPort = 4723;

    /// <summary>Environment variable overriding <see cref="DefaultPort"/>.</summary>
    /// <remarks>
    /// <para>
    /// 4723 is shared with WinAppDriver <b>and</b> with Appium, so on a working
    /// machine it is frequently already taken — which means this driver cannot
    /// run beside the thing it is measured against. That comparison is how the
    /// project knows where it stands, so being unable to run both at once is a
    /// real cost rather than a nuisance.
    /// </para>
    /// <para>
    /// An environment variable rather than a new switch, because the point is to
    /// move the port <i>without</i> touching the arguments a suite already
    /// passes.
    /// </para>
    /// </remarks>
    public const string PortEnvironmentVariable = "WINDOWSDRIVERCORE_PORT";

    /// <summary>Environment variable overriding <see cref="DefaultHost"/>.</summary>
    public const string HostEnvironmentVariable = "WINDOWSDRIVERCORE_HOST";

    private const int MinPort = 1;
    private const int MaxPort = 65535;

    /// <summary>
    /// Parses WinAppDriver's documented argument forms: none, <c>[port]</c>,
    /// <c>[host] [port]</c>, or either form with a base path appended to the
    /// port argument (<c>4723/wd/hub</c>).
    /// </summary>
    /// <param name="args">The process arguments, excluding the executable name.</param>
    /// <returns>The parsed address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    /// <exception cref="FormatException">
    /// The arguments do not match any documented form, or the port is not a
    /// valid TCP port.
    /// </exception>
    public static ServerAddress Parse(string[] args) =>
        Parse(args, Environment.GetEnvironmentVariable);

    /// <summary>Parses the arguments against a supplied environment.</summary>
    /// <param name="args">The process arguments, excluding the executable name.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <returns>The parsed address.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="FormatException">
    /// The arguments do not match any documented form, the port is not a valid
    /// TCP port, or an environment override is not a number.
    /// </exception>
    /// <remarks>
    /// <b>Precedence is argument, then environment, then default.</b> An explicit
    /// argument must win, or a stray variable in a shell would silently move a
    /// server somebody asked to be somewhere specific — the quiet kind of wrong
    /// this project keeps finding.
    /// </remarks>
    public static ServerAddress Parse(string[] args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        string[] positional = LeadingPositionalArguments(args);
        string host = environment(HostEnvironmentVariable) is { Length: > 0 } configuredHost
            ? configuredHost
            : DefaultHost;

        return positional.Length switch
        {
            0 => new ServerAddress(host, PortFrom(environment), BasePath: null),
            1 => FromPortSpecification(host, positional[0]),
            2 => FromPortSpecification(positional[0], positional[1]),
            _ => throw new FormatException(
                $"Expected at most two arguments ([host] [port[/base/path]]), got {positional.Length}."),
        };
    }

    /// <summary>The port from the environment, or the default.</summary>
    /// <remarks>
    /// An unparseable value throws rather than falling back. Falling back would
    /// start a server on 4723 for someone who explicitly asked for something
    /// else, and the only symptom would be a collision somewhere later.
    /// </remarks>
    private static int PortFrom(Func<string, string?> environment)
    {
        string? configured = environment(PortEnvironmentVariable);
        if (configured is not { Length: > 0 })
        {
            return DefaultPort;
        }

        if (!int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
            port < MinPort || port > MaxPort)
        {
            throw new FormatException(
                $"{PortEnvironmentVariable} is '{configured}', which is not a TCP port " +
                $"between {MinPort} and {MaxPort}.");
        }

        return port;
    }

    /// <summary>
    /// Takes the leading arguments up to the first switch.
    /// </summary>
    /// <remarks>
    /// WinAppDriver's arguments are positional, but the same array is also handed
    /// to the ASP.NET host builder, which owns <c>--key value</c> switches. This
    /// stops at the first switch rather than filtering switches out, because
    /// filtering would treat the value of a switch as positional — a
    /// <c>--contentRoot C:\x</c> pair would offer <c>C:\x</c> as a host name.
    /// </remarks>
    private static string[] LeadingPositionalArguments(string[] args)
    {
        int count = 0;
        while (count < args.Length && !IsSwitch(args[count]))
        {
            count++;
        }

        return args[..count];
    }

    /// <summary>
    /// Whether an argument belongs to the host builder rather than to us.
    /// </summary>
    /// <remarks>
    /// A leading <c>-</c> followed by a digit is a negative number, not a switch.
    /// Without that distinction <c>-1</c> reads as a switch, the port argument
    /// disappears, and the server silently binds the default port instead of
    /// rejecting an invalid one — the failure mode this whole project keeps
    /// finding in the code it replaces.
    /// </remarks>
    private static bool IsSwitch(string argument) =>
        argument.StartsWith("--", StringComparison.Ordinal)
        || (argument.StartsWith('-') && argument.Length > 1 && !char.IsDigit(argument[1]));

    /// <summary>
    /// The origin Kestrel binds. Excludes <see cref="BasePath"/>, which is applied
    /// as middleware rather than encoded in the listen URL.
    /// </summary>
    /// <returns>A URL of the form <c>http://host:port</c>.</returns>
    /// <remarks>
    /// Plain HTTP is the protocol, not an oversight. WinAppDriver serves the JSON
    /// Wire Protocol over HTTP on loopback, and every existing Appium and Selenium
    /// Grid configuration points at <c>http://127.0.0.1:4723</c>. Serving HTTPS
    /// would break every client this project exists to support.
    /// </remarks>
    public string ToListenUrl() => $"{Uri.UriSchemeHttp}://{Host}:{Port}";

    /// <summary>
    /// Splits WinAppDriver's combined port argument. The base path rides on the
    /// port rather than arriving as a separate argument — <c>4723/wd/hub</c> —
    /// which is the form every Appium and Selenium Grid configuration uses.
    /// </summary>
    private static ServerAddress FromPortSpecification(string host, string portSpecification)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new FormatException("Host argument cannot be empty.");
        }

        int separator = portSpecification.IndexOf('/', StringComparison.Ordinal);
        string portText = separator < 0 ? portSpecification : portSpecification[..separator];
        string? basePath = separator < 0 ? null : ParseBasePath(portSpecification[(separator + 1)..]);

        return new ServerAddress(host, ParsePort(portText), basePath);
    }

    private static int ParsePort(string portText)
    {
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
        {
            throw new FormatException($"'{portText}' is not a valid port number.");
        }

        if (port is < MinPort or > MaxPort)
        {
            throw new FormatException($"Port {port} is outside the valid range {MinPort}-{MaxPort}.");
        }

        return port;
    }

    private static string ParseBasePath(string path)
    {
        string trimmed = path.Trim('/');
        if (trimmed.Length == 0)
        {
            throw new FormatException("Base path cannot be empty when a '/' is present in the port argument.");
        }

        return $"/{trimmed}";
    }
}
