using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Protocol.Routing;

namespace WindowsDriverCore.Tests.Unit.Routing;

/// <summary>
/// A server started with <c>4723/wd/hub</c> must serve only that prefix.
///
/// Measured against real WinAppDriver 1.2.2009.02003 started with
/// <c>127.0.0.1 4728/wd/hub</c>: <c>/wd/hub/status</c> answered 200, bare
/// <c>/status</c> answered 404. <c>UsePathBase</c> on its own produces neither
/// — it strips the prefix when present and passes everything else through, so
/// both paths answer. That is what this gate exists to prevent.
/// </summary>
[TestFixture]
public sealed class BasePathGateTests
{
    private static async Task<(int StatusCode, bool ReachedNext)> Send(string basePath, string path)
    {
        bool reachedNext = false;
        BasePathGate gate = new(
            next: _ =>
            {
                reachedNext = true;
                return Task.CompletedTask;
            },
            basePath: basePath);

        DefaultHttpContext context = new();
        context.Request.Path = new PathString(path);

        await gate.InvokeAsync(context);

        return (context.Response.StatusCode, reachedNext);
    }

    [Test]
    public async Task RequestUnderBasePath_ReachesTheRestOfThePipeline()
    {
        (int statusCode, bool reachedNext) = await Send("/wd/hub", "/wd/hub/status");

        reachedNext.ShouldBeTrue();
        statusCode.ShouldBe(StatusCodes.Status200OK, "the gate must not set a status of its own");
    }

    [Test]
    public async Task RequestOutsideBasePath_Is404AndNeverReachesThePipeline()
    {
        // Both halves matter. A gate that returned 404 but still called next
        // would run the command and discard the answer; one that blocked without
        // setting the status would return 200 with an empty body.
        (int statusCode, bool reachedNext) = await Send("/wd/hub", "/status");

        statusCode.ShouldBe(StatusCodes.Status404NotFound);
        reachedNext.ShouldBeFalse();
    }

    [Test]
    public async Task BasePathItself_IsUnderTheBasePath()
    {
        (_, bool reachedNext) = await Send("/wd/hub", "/wd/hub");

        reachedNext.ShouldBeTrue();
    }

    [Test]
    public async Task PrefixMatchMustEndOnASegmentBoundary()
    {
        // The condition that separates a correct implementation from a plain
        // StartsWith: "/wd/hubbub" begins with "/wd/hub" but is a different path.
        // Without this case, StartsWith and a boundary check agree on every other
        // input in this fixture.
        (int statusCode, bool reachedNext) = await Send("/wd/hub", "/wd/hubbub/status");

        statusCode.ShouldBe(StatusCodes.Status404NotFound);
        reachedNext.ShouldBeFalse();
    }

    [Test]
    public async Task MatchIsCaseInsensitive()
    {
        // URLs paths are matched case-insensitively by ASP.NET routing, so the
        // gate must not be stricter than the router it guards.
        (_, bool reachedNext) = await Send("/wd/hub", "/WD/HUB/status");

        reachedNext.ShouldBeTrue();
    }

    [Test]
    public async Task UnrelatedPath_IsRejected()
    {
        // A control: something sharing no prefix at all must also be rejected,
        // so a pass above cannot come from the gate admitting everything.
        (int statusCode, bool reachedNext) = await Send("/wd/hub", "/session/abc/element");

        statusCode.ShouldBe(StatusCodes.Status404NotFound);
        reachedNext.ShouldBeFalse();
    }
}
