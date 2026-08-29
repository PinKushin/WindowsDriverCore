namespace WindowsDriverCore.Platform.Windows;

/// <summary>Where the machine says it is.</summary>
/// <param name="Latitude">Degrees, -90 to 90.</param>
/// <param name="Longitude">Degrees, -180 to 180.</param>
/// <param name="Altitude">Metres above sea level, or 0 when unreported.</param>
public sealed record GeoPosition(double Latitude, double Longitude, double Altitude);

/// <summary>
/// The machine's location, as <c>GET /session/{id}/location</c> reports it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The last endpoint in WinAppDriver's own documented list this driver did not
/// serve.</b> Measured 2026-08-29 by probing all 59 rows of its
/// <c>SupportedAPIs.md</c> against a running server: everything else answered,
/// and three of the four apparent gaps were errors in its own table.
/// </para>
/// <para>
/// <b>A refusal is the honest answer most of the time, and that is fine.</b>
/// Windows needs location consent, and a machine without a location provider —
/// which includes the Windows Server that CI runs on and the offline guest —
/// genuinely does not know where it is. <c>GetLocation</c> is one of the nine
/// environmental failures WinAppDriver itself has on that guest, for exactly this
/// reason.
/// </para>
/// <para>
/// <b>What is NOT acceptable is inventing a coordinate.</b> A plausible latitude
/// is the same defect as a 200 for an action that did not happen, and worse,
/// because the client cannot tell.
/// </para>
/// </remarks>
public interface IGeolocation
{
    /// <summary>Reads the machine's position.</summary>
    /// <param name="position">Where it is, or null.</param>
    /// <returns>
    /// False when there is no provider, no consent, or no fix. All three are
    /// ordinary rather than exceptional.
    /// </returns>
    bool TryRead(out GeoPosition? position);
}
