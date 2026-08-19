using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Services;

namespace TurboSuite.Docs.Services;

/// <summary>
/// The Load Schedule "Dimming" column value for one circuit.
///
/// This answers a <b>purchasing</b> question — what control device does this load need? — which
/// is NOT the same question <see cref="DimmingModuleResolver"/> answers (which control module the
/// panel BOM allocates). The two deliberately diverge on switched circuits, and that divergence is
/// the whole point of this class existing rather than reusing ProtocolDisplay directly.
///
/// <para><b>RELAY dominates.</b> A Switch-type wall device is authored with Dimming Protocol =
/// "RELAY" (its Dimmer-type siblings are left blank so the fixtures' own protocol shows through).
/// When RELAY is present on a circuit — from that switch device, or from a relay-authored fixture —
/// the whole circuit reads "RELAY" (buy a plain switch), overriding any ELV/0-10V the dimmable
/// fixtures carry: they are switched on/off here, not dimmed. Without RELAY, the circuit shows its
/// fixtures' resolved protocol — ELV/0-10V (buy the matching dimmer, or ride the matching panel
/// module).</para>
///
/// <para><b>Loads-only by design.</b> This does not touch <see cref="DimmingModuleResolver"/>, so
/// panel allocation and the control BOM are unaffected. Switched circuits are already kept out of
/// the BOM upstream by ZonesCircuitData.IsWiredToSwitch, so RELAY here never conjures a BOM part.</para>
/// </summary>
public static class LoadsDimmingResolver
{
    private const string Relay = "RELAY";

    public static string ResolveDisplay(IEnumerable<string?>? fixtureProtocols)
    {
        var protocols = fixtureProtocols?.ToList() ?? new List<string?>();

        if (protocols.Any(p => string.Equals(p?.Trim(), Relay, StringComparison.OrdinalIgnoreCase)))
            return Relay;

        return DimmingModuleResolver.Resolve(protocols).ProtocolDisplay;
    }
}
