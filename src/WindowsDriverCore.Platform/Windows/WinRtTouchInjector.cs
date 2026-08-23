using System.Collections.Generic;
using Windows.UI.Input.Preview.Injection;

namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// Touch injection through the WinRT <c>InputInjector</c>, held for the
/// process's lifetime.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the Win32 API cannot hold a contact across separate
/// HTTP requests, and the reference driver does not use it.</b> Measured
/// 2026-08-22 by inspecting <c>MitaLite.Foundation.dll</c>, which WinAppDriver
/// ships and injects through:
/// </para>
/// <code>
/// internal class InputDeviceTouch : InputDevice
/// {
///     public InputDeviceTouch()
///     {
///         injector = InputInjector.TryCreate();
///         injector.InitializeTouchInjection(InjectedInputVisualizationMode.Default);
///     }
/// }
/// </code>
/// <para>
/// The injector is a long-lived device object — the type has a <c>Dispose</c>
/// and a finalizer — and <b>the injector IS the session</b>, so a contact
/// survives across separated calls by construction. MitaLite keeps a raw Win32
/// <c>InjectTouchInput</c> path as its fallback, which is the only path this
/// driver had.
/// </para>
/// <para>
/// <b>What that explained.</b> <c>/actions</c> drags reliably because one
/// request is one continuous burst; taps work on <c>/touch/*</c> because down
/// and up are adjacent; a drag split across three requests dies because there is
/// no session to hold the contact. Ten <c>/touch/move</c> requests refuse the
/// lift at ANY pacing including zero — the variable was separated bursts, never
/// elapsed time — while a bare hold survives 800 ms and still produces a real
/// tap. Roughly thirty timing, threading, frame-rate and coordinate hypotheses
/// were refuted before the import table was read.
/// </para>
/// <para>
/// <b>Touch only, deliberately.</b> Pen already drags correctly through the
/// Win32 path — <c>Pen_DragAndDrop</c> passes — so routing it here as well would
/// change something that works in the same commit that fixes something that does
/// not, and no score afterwards would be attributable to either.
/// </para>
/// </remarks>
internal sealed class WinRtTouchInjector
{
    private readonly InputInjector _injector;

    private WinRtTouchInjector(InputInjector injector) => _injector = injector;

    /// <summary>Creates the injector, or returns null when it is unavailable.</summary>
    /// <returns>An injector, or <see langword="null"/> on a system without one.</returns>
    /// <remarks>
    /// <para>
    /// <b>Null is an expected answer, not a failure.</b> <c>InputInjector</c>
    /// arrived in Windows 10 1809 and this driver's floor is 1607, so on an older
    /// build the type is simply not there — which is why the WinRT API itself
    /// offers <c>TryCreate</c> rather than a throwing constructor. The caller
    /// falls back to Win32, exactly as MitaLite does.
    /// </para>
    /// <para>
    /// <b>The catch is broad on purpose and this is the one place it should
    /// be.</b> A missing type surfaces as <c>TypeLoadException</c>, a missing
    /// method as <c>MissingMethodException</c>, and a refused injector as a
    /// <c>COMException</c> — three unrelated types for one question, "can this
    /// machine do it". Letting any of them escape would take down a driver that
    /// has a perfectly good fallback sitting next to it.
    /// </para>
    /// </remarks>
    internal static WinRtTouchInjector? TryCreate()
    {
        try
        {
            InputInjector? injector = InputInjector.TryCreate();
            if (injector is null)
            {
                return null;
            }

            injector.InitializeTouchInjection(InjectedInputVisualizationMode.Default);
            return new WinRtTouchInjector(injector);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Injects one frame of touch contacts.</summary>
    /// <param name="contacts">Every contact in the frame.</param>
    /// <returns>True when the frame was accepted.</returns>
    internal bool Inject(IReadOnlyList<SyntheticContact> contacts)
    {
        List<InjectedInputTouchInfo> frame = new(contacts.Count);

        foreach (SyntheticContact contact in contacts)
        {
            frame.Add(new InjectedInputTouchInfo
            {
                PointerInfo = new InjectedInputPointerInfo
                {
                    PointerId = 0,
                    PointerOptions = OptionsFor(contact.Phase),
                    PixelLocation = new InjectedInputPoint
                    {
                        PositionX = contact.X,
                        PositionY = contact.Y,
                    },
                },

                // A contact with no area is rejected by some targets, so the
                // frame carries a small square rather than a bare pixel - the
                // same reason the Win32 path sets rcContact.
                Contact = new InjectedInputRectangle
                {
                    Left = contact.X - 2,
                    Top = contact.Y - 2,
                    Right = contact.X + 2,
                    Bottom = contact.Y + 2,
                },
            });
        }

        try
        {
            _injector.InjectTouchInput(frame);
            return true;
        }
        catch (Exception)
        {
            // Reported as a refusal rather than thrown, matching the Win32 path's
            // contract - the caller turns a false into a protocol fault and has
            // nowhere to put an exception.
            return false;
        }
    }

    /// <summary>The pointer options for a phase.</summary>
    /// <remarks>
    /// The same three states the Win32 path uses, spelled in WinRT's enum.
    /// <c>New</c> appears only on the down: it declares a contact that did not
    /// exist before, and repeating it on an update would start a second one.
    ///
    /// <c>Primary</c> appears on ALL THREE. Windows promotes only the primary
    /// pointer to gestures and to mouse messages, so a contact without it is
    /// delivered faithfully and recognised as nothing - measured as a 1099 ms
    /// held contact producing no context menu where a mouse right-click on the
    /// same element produced one. A hold is a sequence of updates and a gesture
    /// is not complete until the lift, so dropping it from either would leave
    /// the recogniser watching a pointer that stops being primary mid-gesture.
    /// </remarks>
    private static InjectedInputPointerOptions OptionsFor(SyntheticContactPhase phase) => phase switch
    {
        SyntheticContactPhase.Down =>
            InjectedInputPointerOptions.New |
            InjectedInputPointerOptions.InRange |
            InjectedInputPointerOptions.InContact |
            InjectedInputPointerOptions.PointerDown |
            InjectedInputPointerOptions.Primary,

        SyntheticContactPhase.Update =>
            InjectedInputPointerOptions.Update |
            InjectedInputPointerOptions.InRange |
            InjectedInputPointerOptions.InContact |
            InjectedInputPointerOptions.Primary,

        _ => InjectedInputPointerOptions.PointerUp | InjectedInputPointerOptions.Primary,
    };
}
