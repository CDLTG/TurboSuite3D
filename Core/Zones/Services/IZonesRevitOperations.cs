using System.Collections.Generic;
using TurboSuite.Abstractions;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Revit-free contracts for the TurboZones modeless tabs. Each is implemented shim-side
    /// (binding to the active <c>Document</c>/<c>UIDocument</c>) and invoked by a Core
    /// ViewModel inside an <see cref="IRevitWorkQueue"/> work item, so every Revit
    /// transaction/selection runs on the Revit API thread. These replace the old typed
    /// <c>RevitApiRequest</c>/<c>RevitApiRequestHandler</c> pair (now retired).
    /// </summary>
    public interface ILoadNameWriter
    {
        /// <summary>Writes load name / circuit comments / room-override for each circuit;
        /// returns the count of circuits actually updated.</summary>
        int UpdateLoadNames(IReadOnlyList<ZonesCircuitData> circuits);
    }

    /// <summary>Persists the Panel Breakdown tab's <see cref="PanelSettings"/> snapshot to
    /// ExtensibleStorage.</summary>
    public interface IPanelSettingsStore
    {
        void Save(PanelSettings settings);
    }

    /// <summary>Selects + reveals a circuit in the active project; returns false if the
    /// element no longer exists.</summary>
    public interface ICircuitSelector
    {
        bool SelectInProject(ElementRef circuitRef);
    }
}
