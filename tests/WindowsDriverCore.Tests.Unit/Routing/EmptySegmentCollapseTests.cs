using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Protocol.Routing;

namespace WindowsDriverCore.Tests.Unit.Routing;

/// <summary>
/// Dropping empty segments from a request path.
/// </summary>
/// <remarks>
/// <para>
/// The end-to-end tests in <c>EmptySessionSegmentTests</c> cover the two shapes
/// a client actually produces. These cover what the rewrite does to everything
/// else, because it runs on every request that contains a doubled separator and
/// the risk is not that it fails to fire — it is that it changes a path nobody
/// asked it to.
/// </para>
/// <para>
/// <b>Written after the implementation, so verified by mutation rather than by
/// being red first</b> — and the mutations corrected the code twice. Removing
/// the root special case changed no test, because there never was one to make:
/// joining zero segments gives <c>""</c> and the leading separator is prepended
/// unconditionally, so <c>//</c> already became <c>/</c>. That branch was dead,
/// and the comment defending it was wrong. Removing the doubled-separator guard
/// DID turn a test red, which is what a load-bearing line looks like.
/// </para>
/// </remarks>
[TestFixture]
public sealed class EmptySegmentCollapseTests
{
    private static async Task<string> Rewrite(string path)
    {
        string seen = "(next was never called)";

        EmptySegmentCollapse collapse = new(context =>
        {
            seen = context.Request.Path.Value ?? "(null)";
            return Task.CompletedTask;
        });

        DefaultHttpContext context = new();
        context.Request.Path = new PathString(path);

        await collapse.InvokeAsync(context);

        return seen;
    }

    [Test]
    public async Task AnEmptySegmentInTheMiddle_IsDropped() =>
        (await Rewrite("/session//title")).ShouldBe("/session/title");

    [Test]
    public async Task AnEmptyLeadingSegment_IsDropped() =>
        (await Rewrite("//status")).ShouldBe("/status");

    [Test]
    public async Task SeveralInARow_AreAllDropped() =>
        (await Rewrite("/session///abc////title")).ShouldBe("/session/abc/title");

    /// <summary>A path of nothing but separators is still a path.</summary>
    /// <remarks>
    /// <b>Kept as a boundary, not as a guard against a branch.</b> The code has
    /// no special case for this and does not need one — the leading separator is
    /// prepended unconditionally. What this pins is that the answer is a routable
    /// path rather than the empty string, which is the thing that would break if
    /// the join were ever rewritten.
    /// </remarks>
    [Test]
    public async Task APathOfOnlySeparators_BecomesTheRoot() =>
        (await Rewrite("//")).ShouldBe("/");

    /// <summary>An ordinary path is passed through untouched.</summary>
    /// <remarks>
    /// <b>The control.</b> Every request in a run goes through this, so a
    /// rewrite that mangled well-formed paths would break the server rather than
    /// one test. Asserted on a path with a session id, an element id and a
    /// command, which is the shape almost every request has.
    /// </remarks>
    [Test]
    public async Task AWellFormedPath_IsUnchanged() =>
        (await Rewrite("/session/abc-123/element/42.99.4.1/click"))
            .ShouldBe("/session/abc-123/element/42.99.4.1/click");

    /// <summary>A trailing separator alone is left exactly as it arrived.</summary>
    /// <remarks>
    /// <b>Deliberate, and the narrowest thing that fixes the defect.</b> The
    /// rewrite only runs on a path containing <c>//</c>, so a single trailing
    /// separator never reaches it. ASP.NET Core's matcher already ignores one,
    /// so nothing is gained by normalising it — and every path this does not
    /// touch is a path whose behaviour cannot have changed.
    /// </remarks>
    [Test]
    public async Task ASingleTrailingSeparator_IsNotTouched() =>
        (await Rewrite("/session/abc/")).ShouldBe("/session/abc/");

    /// <summary>Dot segments are not interpreted.</summary>
    /// <remarks>
    /// <b>The security-relevant control.</b> This collapses empty segments and
    /// nothing else: <c>.</c> and <c>..</c> arrive unchanged, so the rewrite
    /// cannot turn a path into one that escapes anywhere. Nothing here reaches
    /// the file system in any case — these paths select a route, not a file —
    /// but a rewrite that quietly resolved <c>..</c> would be a different and
    /// much more dangerous function than the one this claims to be.
    /// </remarks>
    [Test]
    public async Task DotSegments_AreLeftAlone() =>
        (await Rewrite("/session/..//../title")).ShouldBe("/session/../../title");
}
