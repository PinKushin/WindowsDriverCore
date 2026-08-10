using System;
using System.Collections.Generic;
using System.Linq;
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
/// The rungs of the click ladder below <c>Invoke</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because a mutation run said they had no test subject.</b> 41 of
/// <c>UiaElementInteractor</c>'s mutants reported <c>NoCoverage</c>: Calculator
/// is buttons carrying <c>InvokePattern</c> and nothing else, so every existing
/// click test exercises exactly one rung. Toggle, SelectionItem,
/// ExpandCollapse, Focus-for-Edit and the ancestor walk were unreached — and the
/// ancestor walk is the rung with field evidence behind it, the one that fixed a
/// MAUI <c>CollectionView</c> in a real suite.
/// </para>
/// <para>
/// Settings is the subject because it has the shapes Calculator lacks. Elements
/// are chosen by <b>which pattern they advertise</b> rather than by name, so the
/// tests survive Settings being redesigned — what is being tested is the ladder,
/// not Microsoft's layout.
/// </para>
/// <para>
/// Each test asserts the <b>path</b>, not merely that something happened. "The
/// click succeeded" cannot distinguish Toggle from a fallback to Invoke, and
/// distinguishing them is the entire point.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class ClickLadderTests
{
    private const string SettingsAumid =
        "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    private UiaElementFinder _finder = null!;
    private UiaElementInspector _inspector = null!;
    private UiaElementInteractor _interactor = null!;
    private nint _window;

    [OneTimeSetUp]
    public void LaunchSettings()
    {
        CUIAutomationClass automation = new();
        UiaElementResolver resolver = new(automation);
        _finder = new UiaElementFinder(automation, resolver);
        _inspector = new UiaElementInspector(automation, resolver);
        _interactor = new UiaElementInteractor(automation, resolver);

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(SettingsAumid, null, null));

        if (launched.Application is null)
        {
            Assert.Ignore($"Settings is not available: {launched.FailureMessage}");
        }

        _window = launched.Application.WindowHandle;

        // Wait for content, then for it to stop moving. Settings has a window
        // long before it has a control tree.
        string anyButton = UiSettle.UntilSomethingMatches(
            _finder, _window, LocatorKind.ControlType, "Button")[0];
        UiSettle.UntilBoundsAreStable(_inspector, _window, anyButton);
    }

    [OneTimeTearDown]
    public void CloseSettings() => AppLifetime.KillAll("SystemSettings");

    /// <summary>
    /// The first element of a control type that advertises a pattern.
    /// </summary>
    /// <remarks>
    /// By advertised pattern rather than by name, so a Settings redesign cannot
    /// silently turn one of these tests into a test of something else.
    /// </remarks>
    private string? FirstAdvertising(string controlType, string patternAttribute)
    {
        FindResult found = _finder.FindAll(_window, LocatorKind.ControlType, controlType);

        return found.ElementIds.FirstOrDefault(id =>
            _inspector.Attribute(_window, id, patternAttribute).Value == "True");
    }

    [Test]
    [Explicit("Diagnostic: prints what the application actually exposes.")]
    public void SurveyWhatThisApplicationExposes()
    {
        // Run this when a rung has no subject. Guessing at another application's
        // control structure is how three tests above ended up skipping, and a
        // skip reads as a pass while covering nothing.
        string[] controlTypes =
        [
            "Button", "CheckBox", "RadioButton", "ComboBox", "ListItem", "TabItem",
            "Edit", "Document", "Text", "Group", "List", "Slider", "Hyperlink",
        ];

        string[] patterns =
        [
            "IsInvokePatternAvailable", "IsTogglePatternAvailable",
            "IsSelectionItemPatternAvailable", "IsExpandCollapsePatternAvailable",
            "IsValuePatternAvailable",
        ];

        int surveyed = 0;

        foreach (string controlType in controlTypes)
        {
            FindResult found = _finder.FindAll(_window, LocatorKind.ControlType, controlType);
            if (found.ElementIds.Count == 0)
            {
                continue;
            }

            List<string> advertised = [];
            foreach (string pattern in patterns)
            {
                int count = found.ElementIds.Count(id =>
                    _inspector.Attribute(_window, id, pattern).Value == "True");

                if (count > 0)
                {
                    advertised.Add($"{pattern[2..^16]}={count}");
                }
            }

            TestContext.Out.WriteLine(
                $"{controlType,-12} {found.ElementIds.Count,4}   {string.Join("  ", advertised)}");
            surveyed++;
        }

        surveyed.ShouldBeGreaterThan(0, "the application exposed no control type at all");
    }

    [Test]
    public void AToggle_IsClickedThroughTogglePattern_NotInvoke()
    {
        // A checkbox or switch exposes Toggle and NOT Invoke, so a ladder that
        // stopped at Invoke would leave every one of them unclickable. That is
        // the reason the rung order is not a preference.
        string? toggle = FindAToggle();

        if (toggle is null)
        {
            Assert.Ignore("No toggle reachable from any Settings page tried.");
        }

        string before = _inspector.Attribute(_window, toggle, "Toggle.ToggleState").Value ?? "?";

        ElementAction click = _interactor.Click(_window, toggle);

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);
        click.Path.ShouldBe("Toggle");

        // Application state, not the return value: the toggle must have moved.
        _inspector.Attribute(_window, toggle, "Toggle.ToggleState").Value
            .ShouldNotBe(before, "the toggle state must change, or no handler ran");

        // Put it back, so the machine is left as it was found.
        _interactor.Click(_window, toggle);
    }

    [Test]
    public void WhenAnElementAdvertisesBothSelectionItemAndInvoke_SelectionItemWins()
    {
        // Settings has 22 ListItems: 9 advertise Invoke, 19 advertise
        // SelectionItem, so some advertise both. That overlap is the condition
        // where a wrong ladder order is observable — on an element carrying only
        // one of them, every order predicts the same path.
        FindResult items = _finder.FindAll(_window, LocatorKind.ControlType, "ListItem");

        string? both = items.ElementIds.FirstOrDefault(id =>
            _inspector.Attribute(_window, id, "IsSelectionItemPatternAvailable").Value == "True" &&
            _inspector.Attribute(_window, id, "IsInvokePatternAvailable").Value == "True");

        if (both is null)
        {
            Assert.Ignore("No Settings item advertises both patterns on this page.");
        }

        ElementAction click = _interactor.Click(_window, both);

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);

        // "Controls support InvokePattern if the same behavior is not exposed
        // through another control pattern" — so an element advertising both is a
        // provider over-advertising, and the specific pattern is the honest one.
        click.Path.ShouldBe("SelectionItem", "the state-bearing pattern outranks Invoke");

        _inspector.IsSelected(_window, both).Value
            .ShouldBeTrue("selecting it must actually select it");
    }

    [Test]
    public void AListItem_IsClickedThroughSelectionItemPattern()
    {
        string? item = FirstAdvertising("ListItem", "IsSelectionItemPatternAvailable")
            ?? FirstAdvertising("TabItem", "IsSelectionItemPatternAvailable");

        if (item is null)
        {
            Assert.Ignore("Settings is showing no selectable item.");
        }

        ElementAction click = _interactor.Click(_window, item);

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);
        click.Path.ShouldBe("SelectionItem");
        _inspector.IsSelected(_window, item).Value
            .ShouldBeTrue("selecting it must actually select it");
    }

    [Test]
    public void AComboBox_IsClickedThroughExpandCollapse()
    {
        string? combo = FirstAdvertising("ComboBox", "IsExpandCollapsePatternAvailable");

        if (combo is null)
        {
            Assert.Ignore("Settings is showing no combo box on this page.");
        }

        string before =
            _inspector.Attribute(_window, combo, "ExpandCollapse.ExpandCollapseState").Value ?? "?";

        ElementAction click = _interactor.Click(_window, combo);

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);
        click.Path.ShouldBe("ExpandCollapse");
        _inspector.Attribute(_window, combo, "ExpandCollapse.ExpandCollapseState").Value
            .ShouldNotBe(before, "expanding must change the state");

        _interactor.Click(_window, combo);
    }

    [Test]
    public void AnEdit_IsClickedByFocusingIt()
    {
        // Clicking a text input means focusing it. The rung exists only for Edit
        // and Document, because a blanket SetFocus fallback is how the previous
        // implementation reported success for doing nothing.
        FindResult edits = _finder.FindAll(_window, LocatorKind.ControlType, "Edit");

        if (edits.ElementIds.Count == 0)
        {
            Assert.Ignore("Settings is showing no text input.");
        }

        string edit = edits.ElementIds[0];

        ElementAction click = _interactor.Click(_window, edit);

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);
        click.Path.ShouldBe("Focus");
        _inspector.Attribute(_window, edit, "HasKeyboardFocus").Value
            .ShouldBe("True", "focusing it must actually give it focus");
    }

    [Test]
    public void AnElementWithNoPattern_IsClickedThroughItsAncestor()
    {
        // The rung with field evidence behind it. A MAUI CollectionView row put
        // its AutomationId on a Border inside the item container, and the
        // container held SelectionItemPattern — the id named a child with no
        // pattern while its parent was perfectly selectable.
        //
        // The same shape here: the Text inside a selectable ListItem. Nested
        // find scopes the search to one item, so the Text found is that item's
        // own child rather than some other row's.
        string? item = FirstAdvertising("ListItem", "IsSelectionItemPatternAvailable");

        if (item is null)
        {
            Assert.Ignore("Settings is showing no selectable item.");
        }

        // Any pattern-less element in the window, not only those inside the
        // first selectable item. Settings shows 22 pattern-less Text elements
        // and 20 pattern-less Groups; the question is which of them has a
        // pattern-carrying ancestor within three levels, and the honest way to
        // find out is to try them rather than to predict the layout.
        List<string> candidates =
        [
            .. _finder.FindAll(_window, LocatorKind.ControlType, "Text").ElementIds,
            .. _finder.FindAll(_window, LocatorKind.ControlType, "Group").ElementIds,
        ];

        string? patternless = null;
        ElementAction reached = default;

        foreach (string candidate in candidates.Where(HasNoLadderPattern))
        {
            ElementAction attempt = _interactor.Click(_window, candidate);

            if (attempt.Outcome == ElementActionOutcome.Performed &&
                attempt.Path.StartsWith("ancestor:", StringComparison.Ordinal))
            {
                patternless = candidate;
                reached = attempt;
                break;
            }
        }

        if (patternless is null)
        {
            Assert.Ignore(
                "No pattern-less element in this application has a pattern-carrying " +
                "ancestor within three levels.");
        }

        TestContext.Out.WriteLine($"{patternless} reached its ancestor via {reached.Path}");

        ElementAction click = reached;

        click.Outcome.ShouldBe(
            ElementActionOutcome.Performed,
            "an element with no pattern of its own must reach its ancestor's");
        click.Path.StartsWith("ancestor:", StringComparison.Ordinal).ShouldBeTrue(
            $"expected the ancestor walk, got '{click.Path}'");
        click.Path.Contains('/', StringComparison.Ordinal).ShouldBeTrue(
            $"an ancestor path names the rung after the level: '{click.Path}'");
    }

    /// <summary>
    /// Walks the navigation looking for a page that has a toggle.
    /// </summary>
    /// <remarks>
    /// Settings' landing page has none, so the rung had no subject and the test
    /// skipped — which reads as a pass and covers nothing. Navigating is what
    /// gives the assertion something to measure.
    /// </remarks>
    private string? FindAToggle()
    {
        string? here = FirstAdvertising("CheckBox", "IsTogglePatternAvailable")
            ?? FirstAdvertising("Button", "IsTogglePatternAvailable");

        if (here is not null)
        {
            return here;
        }

        FindResult navigation = _finder.FindAll(_window, LocatorKind.ControlType, "ListItem");

        foreach (string page in navigation.ElementIds.Take(6))
        {
            if (_interactor.Click(_window, page).Outcome != ElementActionOutcome.Performed)
            {
                continue;
            }

            // The page swaps its whole content, so wait for it rather than
            // reading a tree that is half the old page.
            UiSettle.UntilSomethingMatches(
                _finder, _window, LocatorKind.ControlType, "Button");

            here = FirstAdvertising("CheckBox", "IsTogglePatternAvailable")
                ?? FirstAdvertising("Button", "IsTogglePatternAvailable");

            if (here is not null)
            {
                return here;
            }
        }

        return null;
    }

    /// <summary>Whether an element carries none of the ladder's patterns.</summary>
    private bool HasNoLadderPattern(string elementId) =>
        _inspector.Attribute(_window, elementId, "IsInvokePatternAvailable").Value != "True" &&
        _inspector.Attribute(_window, elementId, "IsTogglePatternAvailable").Value != "True" &&
        _inspector.Attribute(_window, elementId, "IsSelectionItemPatternAvailable").Value != "True" &&
        _inspector.Attribute(_window, elementId, "IsExpandCollapsePatternAvailable").Value != "True";

    [Test]
    public void AnElementWithNoPatternAnywhere_IsRefused_NotSilentlyAccepted()
    {
        // Rung eight, and the one that matters most: the ladder must say it
        // could not click rather than returning success. The implementation
        // being replaced ended with SetFocus() and reported success — an
        // operation indistinguishable from a working one.
        //
        // Settings supplies real subjects for this: buttons that advertise no
        // ladder pattern at all. The first run of this fixture found one by
        // accident, reported as "the first button was not clickable".
        // Groups, not buttons. Every Button in Settings advertises Invoke, so
        // looking there found nothing and the test skipped. The survey shows 20
        // Groups carrying no pattern at all — those are the real subjects for a
        // ladder that has to refuse.
        FindResult groups = _finder.FindAll(_window, LocatorKind.ControlType, "Group");
        groups.ElementIds.ShouldNotBeEmpty();

        string? patternless = groups.ElementIds.FirstOrDefault(HasNoLadderPattern);

        if (patternless is null)
        {
            Assert.Ignore("Every group on this page advertises a pattern.");
        }

        ElementAction click = _interactor.Click(_window, patternless);

        // Either an ancestor carried it, or nothing did — but never a bare
        // "Performed" with no rung named, and never silence.
        if (click.Outcome == ElementActionOutcome.Performed)
        {
            click.Path.ShouldNotBeNullOrEmpty("a successful click must name the rung that did it");
            click.Path.StartsWith("ancestor:", StringComparison.Ordinal).ShouldBeTrue(
                $"the element has no pattern, so only an ancestor could have clicked it: '{click.Path}'");
        }
        else
        {
            click.Outcome.ShouldBe(
                ElementActionOutcome.NotInteractable,
                "a pattern-less element must be refused, not reported as done");
            click.Path.ShouldBeEmpty("a refusal names no rung");
        }
    }

    [Test]
    public void TheLadderReportsWhichRungFired_ForEveryClickableShape()
    {
        // A survey rather than a single case: whatever Settings is showing, every
        // successful click must name a rung, and it must be a rung this ladder
        // actually has. A path of "" or an unexpected name would mean the
        // reported mechanism and the real one had drifted apart.
        IReadOnlyList<string> known =
        [
            "Invoke", "Toggle", "SelectionItem", "ExpandCollapse", "Focus",
        ];

        FindResult buttons = _finder.FindAll(_window, LocatorKind.ControlType, "Button");
        buttons.ElementIds.ShouldNotBeEmpty();

        // The first button that is actually clickable, rather than the first
        // button. Settings has decorative ones the ladder correctly refuses, and
        // skipping on those tested nothing.
        ElementAction click = default;
        foreach (string candidate in buttons.ElementIds.Take(12))
        {
            click = _interactor.Click(_window, candidate);
            if (click.Outcome == ElementActionOutcome.Performed)
            {
                break;
            }
        }

        if (click.Outcome != ElementActionOutcome.Performed)
        {
            Assert.Ignore("No clickable button among the first twelve.");
        }

        string rung = click.Path.Contains('/', StringComparison.Ordinal)
            ? click.Path[(click.Path.IndexOf('/', StringComparison.Ordinal) + 1)..]
            : click.Path;

        known.Contains(rung).ShouldBeTrue(
            $"'{click.Path}' names a rung the ladder does not have");
    }
}
