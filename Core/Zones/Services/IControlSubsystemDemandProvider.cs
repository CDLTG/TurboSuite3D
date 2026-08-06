#nullable enable
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Something that can report a control subsystem's demand on the panel and the control link.
    ///
    /// Implementations are Revit-coupled (they read the model and the subsystem's persisted design
    /// state) and live in the shim; everything downstream — the BOM, the allocator, the link display —
    /// consumes only <see cref="ControlSubsystemDemand"/> and stays pure.
    ///
    /// <b>Must not throw.</b> A provider sits on the path that builds a purchasing document, and its
    /// subsystem is routinely mid-design. Every failure the solver can raise is caught and returned as
    /// <see cref="ControlSubsystemDemand.Unsolvable"/>: a BOM must never fail to build because DMX is
    /// half-declared.
    /// </summary>
    public interface IControlSubsystemDemandProvider
    {
        /// <summary>The subsystem's current demand. Never null.</summary>
        ControlSubsystemDemand GetDemand();
    }
}
