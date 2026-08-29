using System.Runtime.InteropServices;

namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// The Win32 clipboard, opened and closed around each call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raw Win32 rather than <c>System.Windows.Forms.Clipboard</c>.</b> The
/// managed wrapper drags in WinForms, which is a UI framework this driver
/// otherwise has no use for, and it is the kind of layer whose cost and
/// behaviour you inherit and then have to explain — the same reason this project
/// talks to <c>IUIAutomation</c> directly.
/// </para>
/// <para>
/// <b>Every call runs on its own STA thread.</b> <c>OpenClipboard</c> requires
/// one, and ASP.NET Core's thread pool is MTA — so calling it from a request
/// thread fails rather than blocking, which would read as "the clipboard is
/// empty". A thread per call rather than a shared one because a clipboard
/// operation is rare, brief, and must not be serialised behind an unrelated
/// request.
/// </para>
/// <para>
/// <b>The clipboard is a shared, contended resource.</b> Another process holding
/// it makes <c>OpenClipboard</c> fail, which is ordinary rather than
/// exceptional — hence the try/return shape rather than throwing.
/// </para>
/// </remarks>
public sealed class WindowsClipboard : IClipboard
{
    /// <summary>CF_UNICODETEXT.</summary>
    /// <remarks>
    /// Not CF_TEXT, which is code-page bound and would mangle anything outside
    /// the machine's ANSI page — a silent corruption of the caller's data.
    /// </remarks>
    private const uint UnicodeText = 13;

    /// <summary>GMEM_MOVEABLE.</summary>
    /// <remarks>
    /// Required: <c>SetClipboardData</c> takes ownership of the handle and the
    /// system frees it, which it can only do for a moveable global allocation.
    /// </remarks>
    private const uint MoveableMemory = 0x0002;

    /// <summary>How long a call may wait for its STA thread.</summary>
    /// <remarks>
    /// Bounds a FAILURE rather than the ordinary case, like every other budget
    /// here. A clipboard held open by another process is the realistic way this
    /// hangs, and a driver that never answers is worse than one that says no.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public bool TryRead(out string? content)
    {
        string? read = null;
        bool ok = OnStaThread(() =>
        {
            if (!Win32.OpenClipboard(0))
            {
                return false;
            }

            try
            {
                nint handle = Win32.GetClipboardData(UnicodeText);
                if (handle == 0)
                {
                    // No TEXT on the clipboard. Not the same as an empty string,
                    // and reported as a failed read so a caller is not told the
                    // clipboard was empty when it holds an image.
                    return false;
                }

                nint memory = Win32.GlobalLock(handle);
                if (memory == 0)
                {
                    return false;
                }

                try
                {
                    read = Marshal.PtrToStringUni(memory);
                    return read is not null;
                }
                finally
                {
                    Win32.GlobalUnlock(handle);
                }
            }
            finally
            {
                Win32.CloseClipboard();
            }
        });

        content = ok ? read : null;
        return ok;
    }

    /// <inheritdoc />
    public bool TryWrite(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return OnStaThread(() =>
        {
            if (!Win32.OpenClipboard(0))
            {
                return false;
            }

            nint memory = 0;

            try
            {
                Win32.EmptyClipboard();

                // The terminating null is part of what the clipboard holds, so
                // the allocation counts it. Omitting it hands the next reader a
                // string that runs on into whatever follows in memory.
                int bytes = (content.Length + 1) * sizeof(char);

                memory = Win32.GlobalAlloc(MoveableMemory, (nuint)bytes);
                if (memory == 0)
                {
                    return false;
                }

                nint target = Win32.GlobalLock(memory);
                if (target == 0)
                {
                    return false;
                }

                try
                {
                    Marshal.Copy(content.ToCharArray(), 0, target, content.Length);
                    Marshal.WriteInt16(target, content.Length * sizeof(char), 0);
                }
                finally
                {
                    Win32.GlobalUnlock(memory);
                }

                if (Win32.SetClipboardData(UnicodeText, memory) == 0)
                {
                    return false;
                }

                // OWNERSHIP HAS TRANSFERRED. The system frees this handle now, so
                // the failure path below must not — and this assignment is what
                // stops it.
                memory = 0;
                return true;
            }
            finally
            {
                // Only reached when SetClipboardData never took it. Freeing a
                // handle the system owns corrupts the clipboard for every
                // process on the desktop.
                if (memory != 0)
                {
                    Win32.GlobalFree(memory);
                }

                Win32.CloseClipboard();
            }
        });
    }

    /// <summary>Runs a clipboard operation on a fresh STA thread.</summary>
    /// <remarks>
    /// <c>Join</c> with a budget rather than an unbounded wait: a clipboard held
    /// by a hung process would otherwise hold a request thread for ever. The
    /// thread is a background one, so a timed-out operation cannot keep the
    /// process alive either.
    /// </remarks>
    private static bool OnStaThread(Func<bool> operation)
    {
        bool result = false;

        Thread worker = new(() => result = operation()) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        return worker.Join(Budget) && result;
    }
}
