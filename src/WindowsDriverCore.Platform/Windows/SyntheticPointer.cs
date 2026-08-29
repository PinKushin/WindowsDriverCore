using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// Injects pen and touch through the Windows pointer-injection APIs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not <c>SendInput</c>, and that is the entire point.</b> Mouse input has no
/// contact area, no pressure and no tilt, and an application receiving it is told
/// a mouse did it. A driver that answers a touch request with a mouse click has
/// reported the wrong thing happened — the same class of defect as a session on a
/// dead window.
/// </para>
/// <para>
/// <b>Touch and pen use different APIs with different availability.</b>
/// <c>InitializeTouchInjection</c> is Windows 8 and later, inside this driver's
/// floor of Windows 10 1607. <c>CreateSyntheticPointerDevice</c> arrived in
/// Windows 10 <b>1809</b>, which is above the floor — so
/// <see cref="CanInject"/> answers for each kind separately rather than assuming
/// both work, and a system that cannot inject pen says so instead of silently
/// sending touch.
/// </para>
/// <para>
/// <b>Initialisation is once per process and is not idempotent.</b>
/// <c>InitializeTouchInjection</c> called a second time fails, so the first call
/// is latched and its result reused. Failing to latch would make the second
/// gesture of a session fail for a reason that looks like the application's.
/// </para>
/// </remarks>
public sealed class SyntheticPointer : ISyntheticPointer
{
    /// <summary>How many simultaneous contacts the injector is prepared for.</summary>
    /// <remarks>
    /// Ten is what a physical digitiser reports and what the API accepts as a
    /// maximum; asking for fewer would refuse a multi-touch gesture that the
    /// protocol permits.
    /// </remarks>
    private const uint MaximumContacts = 10;

    private static readonly Lock InitialisationGate = new();

    private static bool _touchInitialised;
    private static bool _touchAvailable;

    /// <inheritdoc />
    public bool CanInject(SyntheticPointerKind kind) => kind switch
    {
        SyntheticPointerKind.Touch => EnsureTouchInitialised(),

        // Pen needs CreateSyntheticPointerDevice, Windows 10 1809. Probed rather
        // than assumed from a version number, because a version check is a claim
        // about the platform and this is a question about the API.
        SyntheticPointerKind.Pen => PenDevice() != 0,

        _ => false,
    };

    /// <inheritdoc />
    public bool Inject(IReadOnlyList<SyntheticContact> contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);

        if (contacts.Count == 0)
        {
            return true;
        }

        // One frame may not mix kinds: the two APIs are separate devices, and a
        // mixed frame would have to be split into two frames that are no longer
        // simultaneous. Refused rather than silently reordered.
        SyntheticPointerKind kind = contacts[0].Kind;
        foreach (SyntheticContact contact in contacts)
        {
            if (contact.Kind != kind)
            {
                return false;
            }
        }

        // TOUCH PREFERS THE WinRT INJECTOR WHEN THIS SYSTEM HAS ONE.
        //
        // The Win32 path below cannot hold a contact across separate HTTP
        // requests, which is why a multi-request drag never moved a window while
        // /actions - one request, one continuous burst - always did. The WinRT
        // injector is a long-lived session object and survives the gaps by
        // construction. See WinRtTouchInjector for the measurement and for how
        // WinAppDriver's own MitaLite does exactly this.
        //
        // Null means Windows 10 1607-1803, where the type does not exist. The
        // Win32 path stays as the fallback and behaves precisely as it does
        // today, which is why adopting this does not raise the floor.
        if (kind == SyntheticPointerKind.Touch && _winRtTouch is not null)
        {
            return _winRtTouch.Inject(contacts);
        }

        return kind == SyntheticPointerKind.Touch
            ? InjectTouch(contacts)
            : InjectPen(contacts);
    }

    /// <summary>The WinRT injector, or null when this system has none.</summary>
    /// <remarks>
    /// <b>Created once and held.</b> The whole reason it fixes anything is that
    /// the injector IS the injection session - creating one per gesture, or per
    /// request, would reproduce the defect it exists to remove.
    ///
    /// Lazy rather than a field initialiser so that a system without it pays the
    /// probe once rather than on every construction, and so a test can observe
    /// the fallback by inspecting <see cref="UsesWinRtForTouch"/>.
    /// </remarks>
    private readonly WinRtTouchInjector? _winRtTouch = WinRtTouchInjector.TryCreate();

    /// <summary>Whether touch is going through the WinRT injector.</summary>
    /// <remarks>
    /// <b>Exposed for a test, and the test matters more than usual here.</b> The
    /// platform-compatibility analyzer does NOT flag an unguarded 1809 call -
    /// CA1416 was forced to error against one and the build still succeeded,
    /// because the WinRT projection carries no SupportedOSPlatform attributes. So
    /// nothing in the build protects the 1607 floor; only the fallback does, and
    /// only a test can show the fallback is reachable.
    /// </remarks>
    internal bool UsesWinRtForTouch => _winRtTouch is not null;

    /// <inheritdoc />
    /// <remarks>
    /// Instance state on a type whose injection methods are otherwise static,
    /// which is deliberate: the value belongs to the caller that just got a
    /// false back, and making it static would let two concurrent gestures
    /// overwrite each other's reason.
    /// </remarks>
    public int LastInjectionError { get; private set; }

    private bool InjectTouch(IReadOnlyList<SyntheticContact> contacts)
    {
        if (!EnsureTouchInitialised())
        {
            LastInjectionError = Marshal.GetLastWin32Error();
            return false;
        }

        PointerTouchInfo[] frame = new PointerTouchInfo[contacts.Count];

        for (int index = 0; index < contacts.Count; index++)
        {
            SyntheticContact contact = contacts[index];

            frame[index].pointerInfo.pointerType = PT_TOUCH;

            // The id identifies the CONTACT across frames, so a move is
            // recognised as the same finger rather than a new one. Index is
            // stable for a gesture built by this driver.
            frame[index].pointerInfo.pointerId = (uint)index;
            frame[index].pointerInfo.ptPixelLocation.X = contact.X;
            frame[index].pointerInfo.ptPixelLocation.Y = contact.Y;

            frame[index].pointerInfo.pointerFlags = FlagsForPhase(contact.Phase);

            frame[index].touchFlags = TOUCH_FLAG_NONE;
            frame[index].touchMask = TOUCH_MASK_CONTACTAREA;

            // THE CALLER'S CONTACT AREA, centred on the point. This was a fixed
            // four-pixel square whatever the payload said - W3C's width and
            // height were validated by the route and then dropped, so a client
            // asking for a 40 px fingertip got 4 and was answered 200.
            //
            // A contact with no area is rejected by some targets, so the record's
            // default keeps the old square for every caller that names no size -
            // which is every JSON Wire /touch/* gesture.
            (int left, int right) = Span(contact.X, contact.Width);
            (int top, int bottom) = Span(contact.Y, contact.Height);

            frame[index].rcContact.Left = left;
            frame[index].rcContact.Top = top;
            frame[index].rcContact.Right = right;
            frame[index].rcContact.Bottom = bottom;
        }

        if (Win32.InjectTouchInput((uint)frame.Length, frame))
        {
            return true;
        }

        // CAPTURED IMMEDIATELY, before anything else can overwrite it. Any
        // intervening managed call may issue its own syscall and replace the
        // thread's last error, which is how a captured-too-late error code
        // becomes a confident wrong answer.
        LastInjectionError = Marshal.GetLastWin32Error();
        return false;
    }

    private bool InjectPen(IReadOnlyList<SyntheticContact> contacts)
    {
        nint device = PenDevice();
        if (device == 0)
        {
            return false;
        }

        // A pen is a single contact by definition, and the actions validator
        // already refuses more than one pen source with the suite's own message.
        SyntheticContact contact = contacts[0];

        PointerTypeInfo[] frame = new PointerTypeInfo[1];
        frame[0].type = PT_PEN;
        frame[0].penInfo.pointerInfo.pointerType = PT_PEN;
        frame[0].penInfo.pointerInfo.pointerId = 0;
        frame[0].penInfo.pointerInfo.ptPixelLocation.X = contact.X;
        frame[0].penInfo.pointerInfo.ptPixelLocation.Y = contact.Y;
        frame[0].penInfo.pointerInfo.pointerFlags = FlagsForPhase(contact.Phase);

        frame[0].penInfo.penFlags = PenFlagsFor(contact.Button);

        // THE ROTATION BIT IS SET ONLY WHEN A ROTATION WAS ASKED FOR, and that
        // is a fix rather than a nicety.
        //
        // MEASURED on the guest: setting it unconditionally cost Pen_LongClick
        // and Pen_Scroll_Vertical, which passed at 758f9ec9 and failed at
        // 6481cbb0 - and the only pen-specific change in those 23 commits was
        // this mask. A mask bit is an assertion that the field beside it is
        // meaningful, so asserting a rotation of zero is not the same as saying
        // nothing about rotation.
        //
        // Semantically identical for the caller: zero degrees IS unrotated, so a
        // client asking for it gets what a client saying nothing gets.
        //
        // My own comment on the line below said this was "NOT VERIFIABLE BY TEST
        // HERE". The guest verified it, negatively - which is what the guest is
        // for.
        uint penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y;

        if (contact.Twist != 0)
        {
            penMask |= PEN_MASK_ROTATION;
        }

        frame[0].penInfo.penMask = penMask;

        // W3C carries pressure as 0..1; the pointer API wants 0..1024.
        frame[0].penInfo.pressure = (uint)Math.Clamp(contact.Pressure * 1024, 0, 1024);
        frame[0].penInfo.tiltX = contact.TiltX;
        frame[0].penInfo.tiltY = contact.TiltY;

        // W3C's `twist` is the pen's rotation about its own axis, and both sides
        // measure it in whole degrees 0..359 - so this is a rename rather than a
        // conversion. The mask bit above is the half that was missing: without
        // it the field is ignored however it is filled in.
        //
        // NOT VERIFIABLE BY TEST HERE, and neither is the mask. Whether Windows
        // then delivers a rotated pen that an application distinguishes is a
        // claim about a digitiser this machine does not have - the same limit
        // PenButtonTests records for PEN_FLAG_BARREL. What IS tested is that the
        // caller's twist survives the protocol layer and reaches the contact,
        // which is where it was being dropped.
        //
        // Mutating the mask to prove sensitivity is not available either: it is
        // the constant's only use, so removing it fails the build, and a build
        // failure masquerades as an uncaught mutant.
        frame[0].penInfo.rotation = (uint)Math.Clamp(contact.Twist, 0, 359);

        if (Win32.InjectSyntheticPointerInput(device, frame, (uint)frame.Length))
        {
            return true;
        }

        LastInjectionError = Marshal.GetLastWin32Error();
        return false;
    }

    private static bool EnsureTouchInitialised()
    {
        lock (InitialisationGate)
        {
            if (_touchInitialised)
            {
                return _touchAvailable;
            }

            _touchInitialised = true;

            try
            {
                _touchAvailable = Win32.InitializeTouchInjection(
                    MaximumContacts, TOUCH_FEEDBACK_INDIRECT);
            }
            catch (EntryPointNotFoundException)
            {
                // Below Windows 8. Outside the supported floor, but answered
                // rather than thrown: the caller asked whether it can, not for
                // an exception.
                _touchAvailable = false;
            }

            return _touchAvailable;
        }
    }

    private static nint _penDevice;
    private static bool _penProbed;

    private static nint PenDevice()
    {
        lock (InitialisationGate)
        {
            if (_penProbed)
            {
                return _penDevice;
            }

            _penProbed = true;

            try
            {
                _penDevice = Win32.CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_INDIRECT);
            }
            catch (EntryPointNotFoundException)
            {
                // Below Windows 10 1809. The floor is 1607, so this is a
                // supported system that genuinely cannot inject pen.
                _penDevice = 0;
            }

            return _penDevice;
        }
    }

    /// <summary>The pointer flags for a phase.</summary>
    /// <remarks>
    /// UPDATE carries INRANGE and INCONTACT just as DOWN does — the contact is
    /// still there. UP carries neither, because it is not.
    /// </remarks>
    internal static uint FlagsForPhase(SyntheticContactPhase phase) => phase switch
    {
        SyntheticContactPhase.Down =>
            POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT |
            POINTER_FLAG_PRIMARY,
        SyntheticContactPhase.Update =>
            POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT |
            POINTER_FLAG_PRIMARY,
        _ => POINTER_FLAG_UP | POINTER_FLAG_PRIMARY,
    };

    /// <summary>The pen flags for whichever part of the pen is in contact.</summary>
    /// <remarks>
    /// <para>
    /// <b>These live in <c>penFlags</c>, a field this struct always had and never
    /// set</b>, so every pen gesture arrived as a tip press regardless of the
    /// button the client named. The suite's <c>Pen_Click_BarrelButton</c> reports
    /// that as <i>"An element could not be located"</i>, because the barrel press
    /// is supposed to raise a context menu and the test then looks for "Delete"
    /// inside it.
    /// </para>
    /// <para>
    /// <b>BARREL and ERASER are not exclusive in the API and are here.</b>
    /// <c>PEN_FLAG_ERASER</c> describes which END is being used and
    /// <c>PEN_FLAG_BARREL</c> which BUTTON is held, so a real pen can report
    /// both. Nothing upstream can express that yet - a W3C <c>button</c> names
    /// one value - so the mapping is one to one and the bitwise shape is kept
    /// only because inventing a different one would misdescribe the API.
    /// </para>
    /// </remarks>
    internal static uint PenFlagsFor(SyntheticContactButton button) => button switch
    {
        SyntheticContactButton.Barrel => PEN_FLAG_BARREL,
        SyntheticContactButton.Eraser => PEN_FLAG_ERASER,
        _ => PEN_FLAG_NONE,
    };

    private const uint PT_TOUCH = 2;
    private const uint PT_PEN = 3;

    private const uint PEN_FLAG_NONE = 0x00000000;
    private const uint PEN_FLAG_BARREL = 0x00000001;
    private const uint PEN_FLAG_ERASER = 0x00000004;

    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;

    /// <summary>The pointer Windows promotes to gestures and to mouse messages.</summary>
    /// <remarks>
    /// <para>
    /// <b>Without this a contact is raw pointer input and nothing more.</b>
    /// Press-and-hold, double-tap and the touch-to-mouse promotion legacy
    /// controls depend on are all driven from the PRIMARY pointer; a contact
    /// that never claims to be it is delivered faithfully and recognised as
    /// nothing.
    /// </para>
    /// <para>
    /// Measured inside a single guest run of <c>TouchLongTap</c>: a mouse
    /// right-click on the list item opened the context menu in 3 seconds, and a
    /// 1099 ms held touch contact at the same element produced no menu at all,
    /// followed by 38 failing searches for "Delete". That test has failed in all
    /// nine measured runs.
    /// </para>
    /// </remarks>
    private const uint POINTER_FLAG_PRIMARY = 0x00002000;

    private const uint TOUCH_FLAG_NONE = 0x00000000;
    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    private const uint TOUCH_FEEDBACK_INDIRECT = 0x00000002;

    /// <summary>Half a contact box, rounded so the whole size survives.</summary>
    /// <param name="centre">Where the contact is, in screen pixels.</param>
    /// <param name="size">How wide or tall the caller said the contact is.</param>
    /// <returns>The two edges, centred on <paramref name="centre"/>.</returns>
    /// <remarks>
    /// <para>
    /// Split rather than halved twice, so an ODD size keeps its full extent: 5
    /// becomes -2 and +3 rather than -2 and +2, which would silently deliver a
    /// 4 px contact for a 5 px request. Off-centre by half a pixel, which no
    /// digitiser could express anyway.
    /// </para>
    /// <para>
    /// Clamped at 1 because a zero or negative box is not a smaller contact, it
    /// is a degenerate rectangle some targets reject outright. The route already
    /// refuses a stated size below 1 with the suite's own message; this guards
    /// the path that does not come through validation.
    /// </para>
    /// </remarks>
    internal static (int Low, int High) Span(int centre, int size)
    {
        int extent = Math.Max(size, 1);
        int before = extent / 2;

        return (centre - before, centre + (extent - before));
    }

    private const uint PEN_MASK_PRESSURE = 0x00000001;

    /// <summary>Rotation about the pen's own axis. W3C calls it <c>twist</c>.</summary>
    /// <remarks>
    /// 0x2, which sits between pressure and tiltX rather than after them — the
    /// bits are not in the order the struct's fields are, and guessing the next
    /// free value would have masked the wrong field.
    /// </remarks>
    private const uint PEN_MASK_ROTATION = 0x00000002;

    private const uint PEN_MASK_TILT_X = 0x00000004;
    private const uint PEN_MASK_TILT_Y = 0x00000008;
    private const uint POINTER_FEEDBACK_INDIRECT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerInfo
    {
        internal uint pointerType;
        internal uint pointerId;
        internal uint frameId;
        internal uint pointerFlags;
        internal nint sourceDevice;
        internal nint hwndTarget;
        internal Win32.Point ptPixelLocation;
        internal Win32.Point ptHimetricLocation;
        internal Win32.Point ptPixelLocationRaw;
        internal Win32.Point ptHimetricLocationRaw;
        internal uint dwTime;
        internal uint historyCount;
        internal int inputData;
        internal uint dwKeyStates;
        internal ulong PerformanceCount;
        internal int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerTouchInfo
    {
        internal PointerInfo pointerInfo;
        internal uint touchFlags;
        internal uint touchMask;
        internal Win32.Rect rcContact;
        internal Win32.Rect rcContactRaw;
        internal uint orientation;
        internal uint pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerPenInfo
    {
        internal PointerInfo pointerInfo;
        internal uint penFlags;
        internal uint penMask;
        internal uint pressure;
        internal uint rotation;
        internal int tiltX;
        internal int tiltY;
    }

    /// <summary>
    /// The union the pointer API takes. Explicit layout, because pen and touch
    /// occupy the same bytes and the <c>type</c> field says which is live.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct PointerTypeInfo
    {
        [FieldOffset(0)]
        internal uint type;

        [FieldOffset(8)]
        internal PointerTouchInfo touchInfo;

        [FieldOffset(8)]
        internal PointerPenInfo penInfo;
    }
}
