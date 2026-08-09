using System.Globalization;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WindowsDriverCore.Automation.Uia;

/// <summary>
/// The element id this driver exposes: a UIA RuntimeId, dot-separated.
/// </summary>
/// <remarks>
/// Shared by the finder, which issues ids, and the resolver, which takes them
/// back. Those two must agree exactly; if the formatting and the comparison ever
/// drift apart, every element command breaks at once while find keeps working —
/// a failure that looks like a UIA problem rather than a formatting one. One
/// implementation is the only way to make that impossible rather than merely
/// unlikely.
/// </remarks>
internal static class UiaRuntimeId
{
    /// <summary>Formats a runtime id.</summary>
    /// <param name="runtimeId">The id as UIA returns it.</param>
    /// <returns>The dot-separated form, or empty for an empty id.</returns>
    /// <remarks>
    /// <para>
    /// Dots rather than commas. WinAppDriver's documentation and its live
    /// responses both use dots (<c>42.19466560.4.73</c>); the previous
    /// implementation used commas, which round-tripped within itself but did not
    /// match ids copied from inspect.exe or from a WinAppDriver session.
    /// </para>
    /// <para>
    /// Written by hand rather than with <c>string.Join</c> over a LINQ
    /// projection, which allocated a string per integer and then a second one
    /// for the join.
    /// </para>
    /// </remarks>
    internal static string Format(int[] runtimeId)
    {
        if (runtimeId.Length == 0)
        {
            return string.Empty;
        }

        // Eleven digits covers int.MinValue including its sign, plus one
        // separator per part. Stack-allocated: a runtime id is a handful of
        // ints, never an unbounded list.
        Span<char> buffer = stackalloc char[runtimeId.Length * 12];
        int written = 0;

        for (int index = 0; index < runtimeId.Length; index++)
        {
            if (index > 0)
            {
                buffer[written++] = '.';
            }

            runtimeId[index].TryFormat(
                buffer[written..], out int partLength, provider: CultureInfo.InvariantCulture);
            written += partLength;
        }

        return new string(buffer[..written]);
    }

    /// <summary>Reads an element's id, if it still has one.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The formatted id, or <see langword="null"/>.</returns>
    /// <remarks>
    /// An element can be enumerated and still have no resolvable identity when
    /// the tree is mutating underneath the query. Null rather than an exception,
    /// because the callers all want to skip it: returning an element a client
    /// cannot address produces a client-side <c>InvalidOperationException</c>
    /// that callers catching <c>NoSuchElementException</c> never catch.
    /// </remarks>
    internal static string? Read(IUIAutomationElement element)
    {
        try
        {
            int[]? runtimeId = element.GetRuntimeId();

            return runtimeId is { Length: > 0 } ? Format(runtimeId) : null;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
