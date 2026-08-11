using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Diagnostics;

namespace WindowsDriverCore.Tests.Unit.Diagnostics;

/// <summary>
/// Where a pointer command aimed, written down.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because the transcript could not answer the question that
/// mattered.</b> On 2026-08-11 the compatibility suite's
/// <c>DeletePreviouslyCreatedAlarmEntry</c> — a <c>/moveto</c> then a
/// <c>/click</c> with button 2 — produced two 200s and no context menu, and the
/// following <c>find Name='Delete'</c> answered nothing 103 times. Both requests
/// reported success, so the transcript said the input was dispatched and nothing
/// about WHERE.
/// </para>
/// <para>
/// The element's size rides along with the point because the two failures look
/// identical without it: a centre computed from a sane rectangle that the system
/// then delivered somewhere else, and a centre computed from a rectangle that was
/// never there. An empty rectangle puts the point at the top-left of the screen,
/// which reads as a plausible coordinate until the size beside it says
/// <c>0x0</c>.
/// </para>
/// <para>
/// <b>Coordinates are not a payload.</b> They are where the driver aimed, which
/// is the driver's own behaviour — not something typed into an application. The
/// rule <see cref="IInteractionLog"/> holds is unchanged: no parameter here can
/// carry a string the caller supplied.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PointerTranscriptTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 8, 11, 9, 15, 4, 250, TimeSpan.Zero);

    [Test]
    public void AMoveToAnElement_RecordsThePointAndTheRectangleItCameFrom()
    {
        StringWriter written = new();

        using (TextRequestLogListener listener = new(written, new FixedClock(Noon)))
        using (DriverEventSource source = new())
        {
            ((IPointerLog)source).PointerTargeted("moveto", 1232, 549, 256, 64, 3.21);
        }

        written.ToString().ShouldBe(
            "2026-08-11T09:15:04.250Z   moveto -> (1232,549) of 256x64 3.2 ms" +
            Environment.NewLine);
    }

    /// <summary>
    /// THE CASE THE WHOLE EVENT EXISTS FOR. UIA reports an empty rectangle for an
    /// element it can see but cannot place, and the centre of nothing is the
    /// top-left of the screen — a coordinate that looks fine on its own.
    /// </summary>
    [Test]
    public void AMoveToAnUnplaceableElement_SaysSoRatherThanPrintingAPlausiblePoint()
    {
        StringWriter written = new();

        using (TextRequestLogListener listener = new(written, new FixedClock(Noon)))
        using (DriverEventSource source = new())
        {
            ((IPointerLog)source).PointerTargeted("moveto", 0, 0, 0, 0, 1.5);
        }

        written.ToString().ShouldBe(
            "2026-08-11T09:15:04.250Z   moveto -> (0,0) of NO RECTANGLE 1.5 ms" +
            Environment.NewLine);
    }

    /// <summary>
    /// A click has no element of its own — it acts wherever the pointer is — so
    /// the size is absent rather than zero. Absent and empty must not render the
    /// same, or the diagnosis above stops working.
    /// </summary>
    [Test]
    public void AClick_RecordsWhereThePointerActuallyWas_WithNoRectangle()
    {
        StringWriter written = new();

        using (TextRequestLogListener listener = new(written, new FixedClock(Noon)))
        using (DriverEventSource source = new())
        {
            ((IPointerLog)source).PointerTargeted("click button 2", 1232, 549, -1, -1, 0.44);
        }

        written.ToString().ShouldBe(
            "2026-08-11T09:15:04.250Z   click button 2 -> (1232,549) 0.4 ms" +
            Environment.NewLine);
    }

    /// <summary>A clock that does not move, so the stamp is asserted exactly.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
