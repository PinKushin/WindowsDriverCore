using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// No fixture in this project can put real input on the desktop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Twice in one day, one route apart.</b> <c>ActionsValidationTests</c>
/// injected real touch once <c>/actions</c> started performing — "youre clicking
/// my browser" — and was fixed by substituting the injector.
/// <c>DispatchedInputDrainsBeforeAReadTests</c> then did the same through
/// <c>/click</c>: "random clicks that click whatever my mouse happens to be
/// over". Both were found by the owner noticing their desktop misbehave, which
/// is not a test.
/// </para>
/// <para>
/// <b>Why the check is on the SOURCE and not on behaviour.</b> The failure is
/// that a fixture forgot to substitute something —
/// <c>WebApplicationFactory</c> boots the real container, so anything not
/// replaced is the real thing. There is no assertion a fixture can make about
/// itself here: the one that forgot is exactly the one that would not think to
/// ask. Reading the files is the only instrument that sees a fixture nobody
/// wrote a check for.
/// </para>
/// <para>
/// <b>The mouse routes are the sharp case.</b> <c>/click</c>, <c>/buttondown</c>
/// and <c>/buttonup</c> carry no coordinate — they act wherever the pointer
/// already is, so there is no such thing as a harmless test value.
/// </para>
/// </remarks>
[TestFixture]
public sealed class NoProtocolTestReachesTheDesktopTests
{
    /// <summary>Routes that put input on the desktop, and what must be faked to serve them.</summary>
    /// <remarks>
    /// SESSION-scoped forms only. <c>/element/{id}/click</c> reaches the desktop
    /// through <c>IElementInteractor</c>, which every fixture already
    /// substitutes, so matching a bare "/click" would flag it and teach the next
    /// reader to ignore this test.
    /// </remarks>
    private static readonly (string Route, string Collaborator)[] Dangerous =
    [
        ("{sessionId}/click", "IPointerInput"),
        ("{sessionId}/buttondown", "IPointerInput"),
        ("{sessionId}/buttonup", "IPointerInput"),
        ("{sessionId}/doubleclick", "IPointerInput"),
        ("{sessionId}/moveto", "IPointerInput"),
        ("{sessionId}/keys", "IKeyboardInput"),
        ("{sessionId}/touch/", "ISyntheticPointer"),
        ("{sessionId}/actions", "ISyntheticPointer"),
    ];

    [Test]
    public void EveryFixtureThatDrivesInput_SubstitutesTheThingThatWouldReachTheDesktop()
    {
        List<string> offences = [];

        foreach (string file in Directory.EnumerateFiles(SourceDirectory(), "*.cs"))
        {
            // This file names every route in its own table, so it would always
            // accuse itself.
            if (Path.GetFileName(file) == "NoProtocolTestReachesTheDesktopTests.cs")
            {
                continue;
            }

            string source = File.ReadAllText(file);

            // Only fixtures that boot the real pipeline can reach anything.
            if (!source.Contains("WebApplicationFactory", StringComparison.Ordinal))
            {
                continue;
            }

            foreach ((string route, string collaborator) in Dangerous)
            {
                bool posts = source.Contains(route, StringComparison.Ordinal);
                bool substitutes = source.Contains(
                    $"Substitute.For<{collaborator}>", StringComparison.Ordinal);

                if (posts && !substitutes)
                {
                    offences.Add(
                        $"{Path.GetFileName(file)} drives {route} without substituting {collaborator}");
                }
            }
        }

        offences.ShouldBeEmpty(
            "a protocol test is about the wire, and these would send real input to " +
            "whatever the person running the suite is looking at:" +
            Environment.NewLine + string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// This project's source, found from the test binary.
    /// </summary>
    /// <remarks>
    /// Walked up from the assembly rather than hard-coded, so the check survives
    /// a move. Inconclusive rather than green if it cannot find the sources —
    /// a guard that silently checks nothing is worse than no guard.
    /// </remarks>
    private static string SourceDirectory()
    {
        DirectoryInfo? here = new(AppContext.BaseDirectory);

        while (here is not null &&
               !File.Exists(Path.Combine(here.FullName, "WindowsDriverCore.Tests.Protocol.csproj")))
        {
            here = here.Parent;
        }

        if (here is null)
        {
            Assert.Inconclusive("The protocol test sources could not be located.");
        }

        return here.FullName;
    }
}
