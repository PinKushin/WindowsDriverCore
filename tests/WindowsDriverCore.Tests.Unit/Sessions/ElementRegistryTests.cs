using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Tests.Unit.Sessions;

/// <summary>
/// The record of which element ids this server has handed out.
/// </summary>
/// <remarks>
/// <para>
/// It exists to tell two failures apart that look identical at the UIA layer.
/// An element id that no longer resolves is either <b>stale</b> — we issued it
/// and the element has since gone — or <b>unknown</b>, an id we never issued.
/// WinAppDriver reports the first as status 10 and the second as status 7, and
/// the compatibility suite asserts on both.
/// </para>
/// <para>
/// Measured, and the reason <see cref="IElementRegistry.TryConsume"/> is
/// destructive rather than a plain lookup: through real WinAppDriver, the
/// <i>first</i> touch of a dead element answers 10 and every touch after it
/// answers 7. Recorded as <c>error.element.stale.text</c> (400/10) followed by
/// <c>error.element.stale.click</c>, <c>.enabled</c> and <c>.location</c>
/// (404/7) against the same id.
/// </para>
/// <para>
/// Note what is stored: <b>ids, not elements</b>. Holding COM element references
/// between calls is the design that produces WinAppDriver's #857 and #1079,
/// because the held view drifts from the live tree. A set of strings cannot
/// drift.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ElementRegistryTests
{
    private const string Session = "session-a";
    private const string Other = "session-b";
    private const string Element = "42.19466560.4.73";

    [Test]
    public void IssuedElement_IsConsumedOnce_ThenUnknown()
    {
        // The destructive behaviour, which is the whole point of this type. A
        // non-destructive lookup would report 10 forever and fail every
        // compatibility test that touches a stale element twice.
        ElementRegistry registry = new();
        registry.Record(Session, Element);

        registry.TryConsume(Session, Element).ShouldBeTrue("the first touch is stale");
        registry.TryConsume(Session, Element).ShouldBeFalse("every touch after it is unknown");
    }

    [Test]
    public void ElementThatWasNeverIssued_IsUnknown()
    {
        ElementRegistry registry = new();

        registry.TryConsume(Session, "99999.99999.99999").ShouldBeFalse();
    }

    [Test]
    public void RecordingTheSameElementTwice_StillConsumesOnce()
    {
        // A client may find the same element repeatedly, and each find records
        // the id again. That must not buy extra stale reports.
        ElementRegistry registry = new();
        registry.Record(Session, Element);
        registry.Record(Session, Element);

        registry.TryConsume(Session, Element).ShouldBeTrue();
        registry.TryConsume(Session, Element).ShouldBeFalse();
    }

    [Test]
    public void OneSessionsElement_IsNotVisibleToAnother()
    {
        // Runtime ids are process-scoped and two sessions can drive the same
        // application, so ids can genuinely collide across sessions. Reporting
        // another session's id as stale would say "this used to exist" about
        // something this client never saw.
        ElementRegistry registry = new();
        registry.Record(Session, Element);

        registry.TryConsume(Other, Element).ShouldBeFalse();

        // The bystander: consuming under the wrong session must not have
        // consumed it under the right one either.
        registry.TryConsume(Session, Element).ShouldBeTrue();
    }

    [Test]
    public void ForgettingASession_DropsItsElements_AndLeavesOthersAlone()
    {
        // DELETE /session must not leak a set per session for the server's
        // lifetime. The second assertion is the control: a Forget that cleared
        // everything would pass the first on its own.
        ElementRegistry registry = new();
        registry.Record(Session, Element);
        registry.Record(Other, Element);

        registry.Forget(Session);

        registry.TryConsume(Session, Element).ShouldBeFalse();
        registry.TryConsume(Other, Element).ShouldBeTrue();
    }

    [Test]
    public void ForgettingASessionThatWasNeverSeen_IsNotAnError()
    {
        ElementRegistry registry = new();

        Should.NotThrow(() => registry.Forget("never-existed"));
    }

    [Test]
    public void ElementIds_AreMatchedExactly()
    {
        // Ids are numeric and dot-separated, so case folding cannot help and
        // trimming would let "42.1.2 " report as stale. Ordinal, like every other
        // identifier comparison in this driver.
        ElementRegistry registry = new();
        registry.Record(Session, Element);

        registry.TryConsume(Session, Element + " ").ShouldBeFalse();
        registry.TryConsume(Session, Element).ShouldBeTrue();
    }
}
