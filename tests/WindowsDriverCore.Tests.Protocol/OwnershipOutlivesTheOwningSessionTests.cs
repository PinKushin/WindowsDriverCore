using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// The application this driver started is closed even if its own session is not
/// the last one out.
/// </summary>
/// <remarks>
/// <para>
/// <b>The measured defect, 2026-08-30.</b> Counting a full compatibility run's
/// transcript by application:
/// </para>
/// <code>
/// app                launches  distinct pids  terminated
/// Calculator               19              4           2
/// notepad.exe              15             15          14
/// explorer.exe              9              2           1
/// </code>
/// <para>
/// Notepad, an ordinary multi-instance Win32 application, is clean. The
/// PACKAGED, SINGLE-INSTANCE applications leak: Calculator was launched
/// nineteen times, produced four processes, and only two of them were ever
/// closed. It therefore survives across test classes carrying whatever state the
/// previous class left — which is the contamination behind
/// <c>TouchDoubleTap</c>, a test that passes 2 of 2 when its class runs alone.
/// </para>
/// <para>
/// <b>The mechanism.</b> Ownership is <c>launched.Application.Started</c>, true
/// only when the activation actually started the process; a single-instance
/// application that is already running returns the existing one. The delete
/// route then terminates only when the removed session owns the application AND
/// no other live session shares its process id. The comment there claimed "the
/// last session out closes it, so nothing leaks either" — and that holds only
/// when the last one out is the OWNER:
/// </para>
/// <code>
/// S1 launches Calculator          owns it
/// S2 opens on the same process    does not own it
/// S1 quits first                  another session shares the pid -> no terminate
/// S2 quits                        does not own it              -> no terminate
/// </code>
/// <para>
/// <b>The fix is to hand ownership on, not to terminate more eagerly.</b>
/// Closing an application this driver did not start is the defect the ownership
/// flag exists to prevent — WinAppDriver kills the user's other windows on
/// Windows 11, which this project deliberately does not do. Transferring keeps
/// that property and still guarantees the application we DID start is closed.
/// </para>
/// </remarks>
[TestFixture]
public sealed class OwnershipOutlivesTheOwningSessionTests : IDisposable
{
    private const int SharedProcess = 4242;
    private const nint SharedWindow = 0x1234;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private ISessionStore _store = null!;
    private IApplicationTerminator _terminator = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _terminator = Substitute.For<IApplicationTerminator>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddSingleton(_terminator)));

        _client = _factory.CreateClient();
        _store = _factory.Services.GetRequiredService<ISessionStore>();
    }

    [SetUp]
    public void ArrangeDefaults()
    {
        _store.Clear();
        _terminator.ClearReceivedCalls();
    }

    [OneTimeTearDown]
    public void StopServer() => Dispose();

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private void Seed(string id, bool owns) =>
        _store.Add(new DriverSession(
            id,
            new Dictionary<string, string> { ["app"] = "Calculator" },
            SharedProcess,
            SharedWindow,
            OwnsApplication: owns));

    private Task<HttpResponseMessage> Quit(string id) =>
        _client.DeleteAsync(new Uri($"/session/{id}", UriKind.Relative));

    /// <summary>The owner leaving first must not strand the application.</summary>
    /// <remarks>
    /// The exact order the compatibility suite produces. Asserted on the SECOND
    /// delete, because the first one is correct to do nothing — another session
    /// is still using the process.
    /// </remarks>
    [Test]
    public async Task WhenTheOwnerQuitsFirst_TheLastSessionOutStillClosesIt()
    {
        Seed("owner", owns: true);
        Seed("borrower", owns: false);

        await Quit("owner");
        _terminator.DidNotReceive().Terminate(Arg.Any<int>(), Arg.Any<nint>());

        await Quit("borrower");
        _terminator.Received(1).Terminate(SharedProcess, SharedWindow);
    }

    /// <summary>An application nobody started is never closed.</summary>
    /// <remarks>
    /// <b>THE CONTROL, and it is the whole reason this is a transfer rather than
    /// "terminate on the last delete".</b> Two sessions attached to somebody
    /// else's running application must both quit without closing it. A fix that
    /// simply terminated whenever the last session left would pass the test
    /// above and start killing applications the user opened — which is precisely
    /// the reference behaviour this project refuses to copy.
    /// </remarks>
    [Test]
    public async Task AnApplicationNobodyStarted_IsNeverClosed()
    {
        Seed("attached-one", owns: false);
        Seed("attached-two", owns: false);

        await Quit("attached-one");
        await Quit("attached-two");

        _terminator.DidNotReceive().Terminate(Arg.Any<int>(), Arg.Any<nint>());
    }

    /// <summary>A lone owner still closes its own application.</summary>
    /// <remarks>
    /// The unchanged case, kept so the transfer cannot be implemented by
    /// deferring every terminate to a session that may never arrive.
    /// </remarks>
    [Test]
    public async Task ALoneOwner_ClosesItImmediately()
    {
        Seed("only", owns: true);

        await Quit("only");

        _terminator.Received(1).Terminate(SharedProcess, SharedWindow);
    }

    /// <summary>Ownership passes to one session, not to every one.</summary>
    /// <remarks>
    /// <b>The control against over-transfer.</b> Handing ownership to every
    /// remaining session would close the application on the next delete while
    /// others are still using it — the four-test regression the
    /// process-sharing check was added to fix in the first place.
    /// </remarks>
    [Test]
    public async Task WithTwoBorrowers_TheApplicationSurvivesUntilTheLastLeaves()
    {
        Seed("owner", owns: true);
        Seed("borrower-one", owns: false);
        Seed("borrower-two", owns: false);

        await Quit("owner");
        await Quit("borrower-one");
        _terminator.DidNotReceive().Terminate(Arg.Any<int>(), Arg.Any<nint>());

        await Quit("borrower-two");
        _terminator.Received(1).Terminate(SharedProcess, SharedWindow);
    }
}
