using System.Collections.Generic;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WindowsDriverCore.Automation.Uia;

/// <summary>
/// Finds and answers the modal dialog inside a session's window.
/// </summary>
/// <remarks>
/// <para>
/// <b>An alert here is a <c>Window</c> in the session window's subtree whose
/// <c>IsModal</c> is true.</b> That definition covers both shapes this project
/// has met: a Win32 message box, which owns its HWND but is a UIA CHILD of the
/// frame because it is an owned pop-up, and a WinUI <c>ContentDialog</c>, which
/// has no HWND at all.
/// </para>
/// <para>
/// <b>Scoped to the session's window, never the desktop.</b> A modal belonging to
/// another application is not this session's to accept, and answering about one
/// would let a test dismiss a dialog it never raised.
/// </para>
/// <para>
/// <b>Which button is "accept" is a real decision, not an obvious one.</b>
/// Windows has no property that says so. The order tried is the XAML content
/// dialog's own automation ids, then the conventional Win32 captions. Naming the
/// wrong one would press "Don't Save" when the caller asked to accept — a silent
/// difference with a destructive outcome — so a dialog whose buttons match
/// nothing is REFUSED rather than guessed at.
/// </para>
/// </remarks>
public sealed class UiaAlertInspector : IAlertInspector
{
    /// <summary>UIA_WindowIsModalPropertyId.</summary>
    private const int WindowIsModal = 30027;

    /// <summary>UIA_ControlTypeId for a Window.</summary>
    private const int WindowControlType = 50032;

    /// <summary>UIA_ControlTypeId for a Button.</summary>
    private const int ButtonControlType = 50000;

    /// <summary>UIA_ControlTypeId for Text.</summary>
    private const int TextControlType = 50020;

    /// <summary>Automation ids and captions that mean "yes, go ahead".</summary>
    /// <remarks>
    /// <c>PrimaryButton</c> is WinUI's <c>ContentDialog</c>; the rest are the
    /// conventional Win32 dialog captions. Ordered most specific first, so an
    /// automation id beats a caption that happens to match.
    /// </remarks>
    private static readonly string[] Affirmative =
        ["PrimaryButton", "OK", "Yes", "Save", "Continue", "Allow"];

    /// <summary>Automation ids and captions that mean "no, back out".</summary>
    /// <remarks>
    /// <b>Both <c>SecondaryButton</c> and <c>CloseButton</c>, in that order.</b>
    /// A WinUI dialog may define either or both, and Notepad's discard prompt was
    /// measured using <c>CloseButton</c> where <c>SecondaryButton</c> was the
    /// expected one — which cost a real investigation in this project.
    /// </remarks>
    private static readonly string[] Negative =
        ["SecondaryButton", "CloseButton", "Cancel", "No", "Do Not Save", "Deny"];

    private readonly IUIAutomation _automation;

    /// <summary>Creates an inspector.</summary>
    /// <param name="automation">The UI Automation root.</param>
    public UiaAlertInspector(IUIAutomation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);
        _automation = automation;
    }

    /// <inheritdoc />
    public ElementRead<string> Text(nint window)
    {
        try
        {
            IUIAutomationElement? alert = FindAlert(window);

            return alert is null
                ? ElementRead.Failed<string>(ElementReadOutcome.NotFound)
                : ElementRead.Success(MessageOf(alert));
        }
        catch (COMException)
        {
            // The window went away mid-read. Reported as such rather than as "no
            // alert", which a caller would report to the client as a normal
            // absence.
            return ElementRead.Failed<string>(ElementReadOutcome.NoSuchWindow);
        }
    }

    /// <inheritdoc />
    public ElementAction Accept(nint window) => Press(window, Affirmative, "accept");

    /// <inheritdoc />
    public ElementAction Dismiss(nint window) => Press(window, Negative, "dismiss");

    private ElementAction Press(nint window, string[] wanted, string verb)
    {
        try
        {
            IUIAutomationElement? alert = FindAlert(window);

            if (alert is null)
            {
                return ElementAction.Failed(ElementActionOutcome.NotFound);
            }

            IUIAutomationElement? button = ButtonOn(alert, wanted);

            if (button is null)
            {
                // REFUSED RATHER THAN GUESSED. Pressing an arbitrary button
                // because one had to be pressed is how a caller asking to accept
                // gets "Don't Save" instead.
                return ElementAction.Failed(ElementActionOutcome.NotInteractable);
            }

            if (button.GetCurrentPattern(UiaPatternIds.Invoke) is IUIAutomationInvokePattern invoke)
            {
                invoke.Invoke();
                return ElementAction.Performed($"{verb} via Invoke");
            }

            return ElementAction.Failed(ElementActionOutcome.NotInteractable);
        }
        catch (COMException)
        {
            return ElementAction.Failed(ElementActionOutcome.NoSuchWindow);
        }
    }

    /// <summary>The modal Window inside this session's window, or null.</summary>
    /// <remarks>
    /// <c>TreeScope_Subtree</c> rather than Descendants, because the session's
    /// window can ITSELF be the modal one — an attached session whose handle is
    /// the dialog. Descendants excludes the element you start from, which would
    /// answer "no alert" for a session looking straight at one.
    /// </remarks>
    private IUIAutomationElement? FindAlert(nint window)
    {
        IUIAutomationElement? root = _automation.ElementFromHandle(window);

        if (root is null)
        {
            return null;
        }

        IUIAutomationCondition modalWindow = _automation.CreateAndCondition(
            _automation.CreatePropertyCondition(UiaPropertyIds.ControlType, WindowControlType),
            _automation.CreatePropertyCondition(WindowIsModal, true));

        return root.FindFirst(TreeScope.TreeScope_Subtree, modalWindow);
    }

    /// <summary>The first button on the dialog matching any wanted name.</summary>
    /// <remarks>
    /// Matched in the ORDER GIVEN rather than by walking the dialog's buttons, so
    /// an automation id beats a caption. A dialog carrying both a
    /// <c>PrimaryButton</c> id and an "OK" caption on a different control must
    /// press the first.
    /// </remarks>
    private IUIAutomationElement? ButtonOn(IUIAutomationElement alert, string[] wanted)
    {
        IUIAutomationCondition button =
            _automation.CreatePropertyCondition(UiaPropertyIds.ControlType, ButtonControlType);

        IUIAutomationElementArray candidates = alert.FindAll(TreeScope.TreeScope_Subtree, button);

        List<IUIAutomationElement> buttons = [];

        for (int index = 0; index < candidates.Length; index++)
        {
            buttons.Add(candidates.GetElement(index));
        }

        foreach (string name in wanted)
        {
            IUIAutomationElement? match = buttons.Find(candidate => Matches(candidate, name));

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>Whether a button answers to a name, by id or by caption.</summary>
    /// <remarks>
    /// Case-insensitive because a caption is written by a designer and an
    /// automation id by a developer, and neither is guaranteed to match the
    /// casing here. An id and a caption are treated alike deliberately: a Win32
    /// message box has no automation ids at all, and a WinUI dialog's buttons
    /// often have no useful caption.
    /// </remarks>
    private static bool Matches(IUIAutomationElement candidate, string name) =>
        string.Equals(candidate.CurrentAutomationId, name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(candidate.CurrentName, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The dialog's message, which is not its title.</summary>
    /// <remarks>
    /// <para>
    /// A dialog's <c>Name</c> is its caption — "Notepad", "Error" — and a client
    /// asking for the alert text wants what it SAYS. So the text comes from the
    /// Text elements inside it, and the caption is the fallback for a dialog with
    /// none.
    /// </para>
    /// <para>
    /// Joined with newlines because a message box splits across several static
    /// controls, and concatenating them would run the last word of one line into
    /// the first of the next.
    /// </para>
    /// </remarks>
    private string MessageOf(IUIAutomationElement alert)
    {
        IUIAutomationCondition text =
            _automation.CreatePropertyCondition(UiaPropertyIds.ControlType, TextControlType);

        IUIAutomationElementArray parts = alert.FindAll(TreeScope.TreeScope_Subtree, text);

        List<string> lines = [];

        for (int index = 0; index < parts.Length; index++)
        {
            string? line = parts.GetElement(index).CurrentName;

            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }
        }

        return lines.Count > 0
            ? string.Join("\n", lines)
            : alert.CurrentName ?? string.Empty;
    }
}
