using Windows.Devices.Geolocation;

namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// The Windows location service.
/// </summary>
/// <remarks>
/// <para>
/// <b>WinRT, and above the compatibility floor of Windows 10 1607 only in
/// practice rather than in principle.</b> <c>Geolocator</c> has existed since
/// Windows 8, so the type is available across the whole supported range — but a
/// machine with location turned off answers the same way one without the API
/// would, so the failure path is the common one either way.
/// </para>
/// <para>
/// <b>Consent is asked for once and refused permanently on a server SKU.</b>
/// <c>RequestAccessAsync</c> must run before a position can be read, and it
/// returns Denied rather than throwing where there is no location service at
/// all. Both are reported as a failed read.
/// </para>
/// </remarks>
public sealed class WindowsGeolocation : IGeolocation
{
    /// <summary>How long to wait for a fix before giving up.</summary>
    /// <remarks>
    /// Bounds a FAILURE, like every other budget here. A first fix from a cold
    /// GPS genuinely can take longer than this; a driver that never answers is
    /// worse than one that says it does not know, and the client can ask again.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public bool TryRead(out GeoPosition? position)
    {
        position = null;

        try
        {
            // ACCESS FIRST. Reading a position without it throws
            // UnauthorizedAccessException, and asking is what surfaces the
            // consent prompt on a desktop that has one.
            if (Geolocator.RequestAccessAsync().AsTask().WaitAsync(Budget).GetAwaiter().GetResult()
                != GeolocationAccessStatus.Allowed)
            {
                return false;
            }

            Geolocator locator = new();
            Geoposition fix = locator.GetGeopositionAsync().AsTask()
                .WaitAsync(Budget).GetAwaiter().GetResult();

            BasicGeoposition point = fix.Coordinate.Point.Position;

            position = new GeoPosition(point.Latitude, point.Longitude, point.Altitude);
            return true;
        }
        catch (Exception error) when (
            error is UnauthorizedAccessException or TimeoutException or
                     System.Runtime.InteropServices.COMException or
                     TypeLoadException or PlatformNotSupportedException)
        {
            // EVERY ONE OF THESE IS ORDINARY. No consent, no fix within the
            // budget, no location service, or no such API on this SKU - a
            // machine that does not know where it is, which is most of them.
            //
            // Caught by TYPE rather than bare, so anything else still propagates:
            // an unexpected failure here is a defect, and swallowing it would hide
            // it behind "no location", which is unfalsifiable.
            return false;
        }
    }
}
