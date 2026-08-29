using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// Every route this driver actually registers, read from the server.
/// </summary>
/// <remarks>
/// <para>
/// <b>The audit's instrument, made reliable.</b> Two passes of the protocol
/// audit used a grep over the routing source and both were wrong. Pass 4's
/// literal-path grep reported sixteen endpoints missing when three were, because
/// helpers like <c>MapRead(app, "text", …)</c> hide the path inside an argument.
/// Pass 10's inverse grep — walking our routes to check them against the spec —
/// silently MISSED <c>/back</c>, <c>/click</c> and <c>/buttondown</c>, which is
/// the dangerous direction: a route we should not serve would go unexamined.
/// </para>
/// <para>
/// <c>EndpointDataSource</c> is what the server itself dispatches on, so it
/// cannot disagree with what is served. A test rather than a script because it
/// then runs on every build, and because a route added tomorrow shows up here
/// without anybody remembering to re-run anything.
/// </para>
/// <para>
/// <b>This is not a change-detector.</b> It asserts PROPERTIES of the surface —
/// that every route is under a known prefix and that the families the protocol
/// requires are complete — rather than pinning a list that has to be edited every
/// time a route is added. A test that just froze the count would fail on every
/// addition and teach people to update it without reading it.
/// </para>
/// </remarks>
[TestFixture]
public sealed class TheServedSurfaceTests : IDisposable
{
    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private IReadOnlyList<string> _routes = null!;

    [OneTimeSetUp]
    public void ReadTheEndpointTable()
    {
        // SUBSTITUTED EVEN THOUGH THIS FIXTURE SENDS NO REQUESTS.
        //
        // NoProtocolTestReachesTheDesktopTests flagged this file for naming
        // /click, /keys and /touch in its TestCase attributes. It only reads the
        // endpoint table and dispatches nothing, so that is a false positive -
        // but the guard is deliberately conservative, it exists because a
        // protocol test has twice sent real input to somebody's desktop, and
        // weakening it to recognise this file would trade a strong guard for a
        // cosmetic one.
        //
        // Substituting costs five lines and is defence in depth regardless: this
        // fixture DOES boot the real container, so the real injectors are
        // constructed as singletons even though nothing calls them.
        IPointerInput mouse = Substitute.For<IPointerInput>();
        IKeyboardInput keyboard = Substitute.For<IKeyboardInput>();
        ISyntheticPointer synthetic = Substitute.For<ISyntheticPointer>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(mouse);
                services.AddSingleton(keyboard);
                services.AddSingleton(synthetic);
            }));

        // Forces the host to build; the endpoint table does not exist until it
        // has.
        _ = _factory.Services;

        _routes = [.. _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Where(text => text.Length > 0)
            .Distinct()
            .OrderBy(text => text, StringComparer.Ordinal)];
    }

    [OneTimeTearDown]
    public void StopServer() => Dispose();

    /// <inheritdoc />
    public void Dispose() => _factory?.Dispose();

    /// <summary>Every route sits under a prefix the protocol defines.</summary>
    /// <remarks>
    /// <para>
    /// <b>The check the inverse grep was for.</b> A route served at a path no
    /// client will ever call is dead weight; one served at a path a client WILL
    /// call but the spec does not define is worse, because it looks like an
    /// extension and behaves like a trap.
    /// </para>
    /// <para>
    /// The allowed set is small on purpose: <c>/status</c>, <c>/session</c>,
    /// <c>/sessions</c>, and the catch-all. Anything else is a new top-level
    /// concept and should be argued for rather than appear.
    /// </para>
    /// </remarks>
    [Test]
    public void EveryRoute_IsUnderAProtocolPrefix()
    {
        // "/{*path" rather than "{*path": MapFallback registers
        // "/{*path:nonfile}", with a leading slash and a route constraint. Both
        // were guessed wrong first time and the endpoint table said so, which is
        // the instrument doing its job.
        string[] allowed = ["/status", "/session", "/sessions", "/{*path"];

        List<string> strays = [.. _routes.Where(route =>
            !allowed.Any(prefix => route.StartsWith(prefix, StringComparison.Ordinal)))];

        strays.ShouldBeEmpty(
            "every route belongs to a path the protocol defines: " + string.Join(", ", strays));
    }

    /// <summary>The families the protocol requires are complete.</summary>
    /// <remarks>
    /// <para>
    /// <b>Named individually because a family with a hole is the failure this
    /// project keeps meeting.</b> <c>/touch</c> had down, move and up long before
    /// it had them setting <c>InputPending</c>; <c>/window</c> had
    /// <c>current/size</c> and not <c>{handle}/size</c>; <c>/alert</c> had
    /// nothing at all while every other command was served.
    /// </para>
    /// <para>
    /// Each entry here is a route a real client sends. The list grows when a
    /// command is added, which is the point — an addition that forgets half its
    /// family fails here.
    /// </para>
    /// </remarks>
    [TestCase("/session/{sessionId}/element", Description = "find one")]
    [TestCase("/session/{sessionId}/elements", Description = "find many")]
    [TestCase("/session/{sessionId}/keys", Description = "session keystrokes")]
    [TestCase("/session/{sessionId}/actions", Description = "W3C input")]
    [TestCase("/session/{sessionId}/source", Description = "the UIA tree")]
    [TestCase("/session/{sessionId}/screenshot", Description = "the screen")]
    [TestCase("/session/{sessionId}/title", Description = "the window caption")]
    [TestCase("/session/{sessionId}/timeouts", Description = "the implicit wait")]
    [TestCase("/session/{sessionId}/orientation", Description = "always LANDSCAPE")]
    [TestCase("/session/{sessionId}/location", Description = "geolocation")]
    [TestCase("/session/{sessionId}/execute", Description = "vendor commands")]
    [TestCase("/session/{sessionId}/log", Description = "the transcript")]
    [TestCase("/session/{sessionId}/alert_text", Description = "modal dialogs")]
    [TestCase("/session/{sessionId}/back", Description = "navigation")]
    [TestCase("/session/{sessionId}/forward", Description = "navigation")]
    [TestCase("/session/{sessionId}/click", Description = "mouse")]
    [TestCase("/session/{sessionId}/buttondown", Description = "mouse")]
    [TestCase("/session/{sessionId}/buttonup", Description = "mouse")]
    [TestCase("/session/{sessionId}/moveto", Description = "mouse")]
    [TestCase("/session/{sessionId}/touch/down", Description = "one contact phase")]
    [TestCase("/session/{sessionId}/touch/move", Description = "one contact phase")]
    [TestCase("/session/{sessionId}/touch/up", Description = "one contact phase")]
    [TestCase("/session/{sessionId}/touch/scroll", Description = "gesture")]
    [TestCase("/session/{sessionId}/touch/flick", Description = "gesture")]
    [TestCase("/session/{sessionId}/window_handle", Description = "JSON Wire")]
    [TestCase("/session/{sessionId}/window/handles", Description = "W3C")]
    [TestCase("/session/{sessionId}/appium/app/launch", Description = "Appium lifecycle")]
    [TestCase("/session/{sessionId}/appium/app/close", Description = "Appium lifecycle")]
    public void TheProtocolsFamilies_AreComplete(string route) =>
        _routes.ShouldContain(route);

    /// <summary>The catch-all is registered, and exactly once.</summary>
    /// <remarks>
    /// <b>It is what makes an unknown command an ANSWER rather than an empty
    /// 404.</b> WinAppDriver names the method and path in its unrecognised-command
    /// reply, and matching that is why a client sees what it asked for. Two of
    /// them would be a routing ambiguity that fails at startup rather than here,
    /// but asserting one keeps the count honest if that ever changes.
    /// </remarks>
    [Test]
    public void TheUnknownCommandFallback_IsRegisteredExactlyOnce() =>
        _routes.Count(route => route.Contains("{*path", StringComparison.Ordinal))
            .ShouldBe(1);
}
