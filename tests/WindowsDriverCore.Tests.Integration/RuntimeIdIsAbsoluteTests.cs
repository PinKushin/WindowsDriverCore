using System;
using Interop.UIAutomationClient;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// A nested find by runtime id searches the window, not the container.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other locator is a DESCRIPTION; a runtime id is an identity.</b>
/// "A button named Save" means something different inside a toolbar than it does
/// in a dialog, which is the entire point of a nested find. A runtime id names
/// exactly one element on the machine — scoping it can only return that element
/// or nothing, so the scope removes answers without adding meaning.
/// </para>
/// <para>
/// <b>Measured on the guest, 2026-08-30.</b>
/// <c>FindNestedElement_ByRuntimeId</c> finds <c>AddAlarmButton</c>, then asks
/// <c>AlarmButton</c> to find it again by id:
/// </para>
/// <code>
/// find AutomationId='AddAlarmButton' -> 1 match     42.4720086.4.1558
/// find RuntimeId='42.4720086.4.1558' -> 0 match(es)  &lt;- searched from ...1537
/// POST /element/42.4720086.4.1537/element -> 404
/// </code>
/// <para>
/// The two are cousins rather than ancestor and descendant, so the scoped search
/// was CORRECT to find nothing — and useless, since the caller already held the
/// element and was asking for it back.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class RuntimeIdIsAbsoluteTests
{
    private UiaElementFinder _finder = null!;
    private nint _window;

    [OneTimeSetUp]
    public void LaunchTheSubject()
    {
        string? path = Win32TestApp.Path;
        if (path is null)
        {
            Assert.Ignore("The Win32 test subject has not been built.");
        }

        CUIAutomationClass automation = new();
        UiaElementResolver resolver = new(automation);
        WindowLocator windows = new();
        _finder = new UiaElementFinder(automation, resolver);

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), windows)
            .Launch(new ApplicationTarget(path, null, null));

        if (launched.Application is null)
        {
            Assert.Ignore($"The subject would not launch: {launched.FailureMessage}");
        }

        _window = launched.Application.WindowHandle;
    }

    [OneTimeTearDown]
    public void CloseTheSubject()
    {
        if (_window != 0)
        {
            new WindowLocator().Close(_window);
        }
    }

    /// <summary>An element finds itself by id from a sibling's scope.</summary>
    /// <remarks>
    /// <para>
    /// The failing shape from the guest, reduced: take two elements that are NOT
    /// ancestor and descendant, and ask one to find the other by runtime id.
    /// </para>
    /// <para>
    /// <b>The two subjects are asserted to be unrelated first</b>, because a test
    /// that happened to pick a parent and its child would pass under the old
    /// behaviour too — the input has to be one where correct and broken differ.
    /// </para>
    /// </remarks>
    [Test]
    public void ASiblingFindsAnElementByItsRuntimeId()
    {
        FindResult buttons = _finder.FindAll(
            new SearchScope(_window), LocatorKind.ControlType, "Button");

        if (buttons.ElementIds.Count < 2)
        {
            Assert.Ignore($"The subject exposes {buttons.ElementIds.Count} buttons; this needs two.");
        }

        string first = buttons.ElementIds[0];
        string second = buttons.ElementIds[1];

        // THE PRECONDITION, asserted rather than assumed: searching INSIDE the
        // first for the second by any ordinary locator must find nothing, or
        // they are nested and this test proves nothing.
        _finder.FindAll(new SearchScope(_window, first), LocatorKind.RuntimeId, second)
            .ElementIds.ShouldNotBeEmpty(
                "a runtime id is absolute, so a sibling's scope must still find it");
    }

    /// <summary>An id that names nothing is still not found.</summary>
    /// <remarks>
    /// <b>The control.</b> Rooting at the window rather than the container widens
    /// the search, and a widened search that answers "found" for everything would
    /// pass the test above while destroying the locator. A runtime id belonging
    /// to no element must still come back empty.
    /// </remarks>
    [Test]
    public void ARuntimeIdThatNamesNothing_IsStillNotFound()
    {
        FindResult buttons = _finder.FindAll(
            new SearchScope(_window), LocatorKind.ControlType, "Button");

        if (buttons.ElementIds.Count == 0)
        {
            Assert.Ignore("The subject exposes no buttons.");
        }

        _finder.FindAll(
            new SearchScope(_window, buttons.ElementIds[0]),
            LocatorKind.RuntimeId,
            "42.999999.4.999999")
            .ElementIds.ShouldBeEmpty("widening the root must not mean matching anything");
    }

    /// <summary>Ordinary locators are still scoped to the container.</summary>
    /// <remarks>
    /// <b>The control that matters most.</b> The change is deliberately narrow —
    /// only <c>RuntimeId</c> re-roots. If it leaked to every locator, a nested
    /// find would silently become a window-wide one, which is the exact defect
    /// the nested routes exist to avoid and would be invisible in any test that
    /// only asserts "something was found".
    /// </remarks>
    [Test]
    public void AnOrdinaryLocator_StillSearchesOnlyTheContainer()
    {
        FindResult buttons = _finder.FindAll(
            new SearchScope(_window), LocatorKind.ControlType, "Button");

        if (buttons.ElementIds.Count < 2)
        {
            Assert.Ignore($"The subject exposes {buttons.ElementIds.Count} buttons; this needs two.");
        }

        // A button is not inside another button, so a scoped search for buttons
        // from one of them finds only itself — never the whole window's set.
        FindResult withinOne = _finder.FindAll(
            new SearchScope(_window, buttons.ElementIds[0]), LocatorKind.ControlType, "Button");

        withinOne.ElementIds.Count.ShouldBeLessThan(
            buttons.ElementIds.Count,
            "a scoped find by control type must not see the whole window");
    }
}
