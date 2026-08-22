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

        return kind == SyntheticPointerKind.Touch
            ? InjectTouch(contacts)
            : InjectPen(contacts);
    }

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

            frame[index].pointerInfo.pointerFlags = FlagsFor(contact.Phase);

            frame[index].touchFlags = TOUCH_FLAG_NONE;
            frame[index].touchMask = TOUCH_MASK_CONTACTAREA;

            // A contact with no area is rejected by some targets, so the frame
            // carries a small square around the point rather than a bare pixel.
            frame[index].rcContact.Left = contact.X - 2;
            frame[index].rcContact.Top = contact.Y - 2;
            frame[index].rcContact.Right = contact.X + 2;
            frame[index].rcContact.Bottom = contact.Y + 2;
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
        frame[0].penInfo.pointerInfo.pointerFlags = FlagsFor(contact.Phase);

        frame[0].penInfo.penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y;

        // W3C carries pressure as 0..1; the pointer API wants 0..1024.
        frame[0].penInfo.pressure = (uint)Math.Clamp(contact.Pressure * 1024, 0, 1024);
        frame[0].penInfo.tiltX = contact.TiltX;
        frame[0].penInfo.tiltY = contact.TiltY;

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
    private static uint FlagsFor(SyntheticContactPhase phase) => phase switch
    {
        SyntheticContactPhase.Down =>
            POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT,
        SyntheticContactPhase.Update =>
            POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT,
        _ => POINTER_FLAG_UP,
    };

    private const uint PT_TOUCH = 2;
    private const uint PT_PEN = 3;

    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;

    private const uint TOUCH_FLAG_NONE = 0x00000000;
    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    private const uint TOUCH_FEEDBACK_INDIRECT = 0x00000002;

    private const uint PEN_MASK_PRESSURE = 0x00000001;
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
