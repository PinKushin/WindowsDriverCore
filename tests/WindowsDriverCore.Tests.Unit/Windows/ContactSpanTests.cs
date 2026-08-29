using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Unit.Windows;

/// <summary>
/// A contact box's edges, from a centre and a size.
/// </summary>
/// <remarks>
/// <para>
/// Small enough to look obviously correct, which is why it is tested. The
/// tempting one-liner — <c>centre - size / 2</c> to <c>centre + size / 2</c> —
/// silently delivers a 4 px contact for a 5 px request, and nothing downstream
/// would ever report it: the injection succeeds, the gesture works, and the size
/// is simply wrong by one.
/// </para>
/// <para>
/// That is the same failure shape as the defect this whole change came from —
/// <c>width</c> and <c>height</c> validated and then discarded — just an order of
/// magnitude smaller.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ContactSpanTests
{
    /// <summary>An even size splits evenly.</summary>
    [Test]
    public void AnEvenSize_IsCentredExactly()
    {
        (int low, int high) = SyntheticPointer.Span(centre: 100, size: 40);

        low.ShouldBe(80);
        high.ShouldBe(120);
        (high - low).ShouldBe(40, "the box is the size that was asked for");
    }

    /// <summary>An odd size keeps its full extent rather than losing a pixel.</summary>
    /// <remarks>
    /// Off-centre by half a pixel, which no digitiser could express anyway. The
    /// alternative loses the pixel, and a 5 px contact silently becoming 4 px is
    /// exactly the kind of quiet wrongness this project treats as a defect.
    /// </remarks>
    [Test]
    public void AnOddSize_KeepsItsFullExtent()
    {
        (int low, int high) = SyntheticPointer.Span(centre: 100, size: 5);

        (high - low).ShouldBe(5, "5 must not arrive as 4");
        low.ShouldBe(98);
        high.ShouldBe(103);
    }

    /// <summary>The default is the four-pixel square that shipped before.</summary>
    /// <remarks>
    /// <b>The regression control.</b> Every JSON Wire <c>/touch/*</c> gesture
    /// names no size and takes the default, so this is what the entire passing
    /// touch family injects. A change to the arithmetic that moved these edges
    /// would alter every one of them.
    /// </remarks>
    [Test]
    public void TheDefaultSize_IsTheSquareThatShippedBefore()
    {
        (int low, int high) =
            SyntheticPointer.Span(centre: 50, size: SyntheticContact.DefaultContactSize);

        low.ShouldBe(48);
        high.ShouldBe(52);
    }

    /// <summary>A degenerate size becomes the smallest real one.</summary>
    /// <remarks>
    /// Zero or negative is not a smaller contact, it is a rectangle some targets
    /// reject outright. The route refuses a STATED size below 1 with the suite's
    /// own message; this guards the paths that do not come through validation.
    /// </remarks>
    [TestCase(0)]
    [TestCase(-7)]
    public void ADegenerateSize_BecomesOnePixel(int size)
    {
        (int low, int high) = SyntheticPointer.Span(centre: 10, size: size);

        (high - low).ShouldBe(1);
    }
}
