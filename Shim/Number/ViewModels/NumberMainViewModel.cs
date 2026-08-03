#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Abstractions;
using TurboSuite.Number.Models;
using TurboSuite.Number.Services;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Number.ViewModels
{
    public class NumberMainViewModel : ViewModelBase
    {
        public CircuitNumberTabViewModel CircuitTab { get; }
        public KeypadTabViewModel KeypadTab { get; }
        public PowerSupplyTabViewModel PowerSupplyTab { get; }

        public NumberMainViewModel(Document doc,
            List<CircuitNumberRow> circuits,
            List<DeviceNumberRow> keypads,
            List<DeviceNumberRow> powerSupplies,
            IRevitWorkQueue workQueue,
            ISwitchIdWriter switchIdWriter,
            IPrefixSuffixStore prefixSuffixStore,
            IRoomOrderStore roomOrderStore,
            ICircuitNumberOperations circuitOps,
            IDeviceSelector deviceSelector)
        {
            // All three tabs are Revit-free Core VMs driven by IRevitWorkQueue + the op
            // interfaces. This shim VM does the one-time Revit collection/projection and
            // hands each tab only abstractions.
            var panelSettings = BuildPanelSettings(doc, circuits);
            CircuitTab = new CircuitNumberTabViewModel(circuits, panelSettings,
                ParameterHelper.CircuitNamingOptions, workQueue, circuitOps);

            var keypadRows = keypads.Select(d => new NumberableRowViewModel(
                d.ElementId.ToRef(),
                d.Model,
                d.SwitchId,
                d.RoomName,
                d.RoomNumber,
                typeName: d.TypeName,
                mark: d.Mark)).ToList();
            var savedRoomOrder = RoomOrderStorageService.Load(doc);
            var sidebarWasOpen = RoomOrderStorageService.LoadSidebarVisible(doc);
            KeypadTab = new KeypadTabViewModel(keypadRows, savedRoomOrder, sidebarWasOpen,
                workQueue, switchIdWriter, roomOrderStore, deviceSelector);

            var psRows = powerSupplies.Select(d => new NumberableRowViewModel(
                d.ElementId.ToRef(),
                d.Model,
                d.SwitchId,
                circuitNumber: d.CircuitNumber,
                circuitElementId: d.CircuitElementId.ToRef(),
                loadName: d.LoadName,
                typeName: d.TypeName,
                mark: d.Mark,
                positionY: d.PositionY)).ToList();
            var (savedPrefix, savedSuffix) = RoomOrderStorageService.LoadPrefixSuffix(doc);
            PowerSupplyTab = new PowerSupplyTabViewModel(psRows, savedPrefix, savedSuffix,
                workQueue, switchIdWriter, prefixSuffixStore, deviceSelector);
        }

        private static List<PanelSettingsModel> BuildPanelSettings(Document doc, List<CircuitNumberRow> circuits)
        {
            var result = new List<PanelSettingsModel>();
            var distinctPanels = circuits
                .Select(c => c.Panel ?? "")
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .OrderBy(p => p)
                .ToList();

            foreach (var panelName in distinctPanels)
            {
                Element panelEl = ParameterHelper.GetPanelElement(doc, panelName);
                if (panelEl == null) continue;

                string naming = ParameterHelper.GetCircuitNaming(panelEl);
                if (string.IsNullOrEmpty(naming) || !ParameterHelper.CircuitNamingOptions.Contains(naming))
                    naming = "(None)";

                result.Add(new PanelSettingsModel(
                    panelName,
                    panelEl.Id.ToRef(),
                    naming,
                    ParameterHelper.GetCircuitPrefix(panelEl),
                    ParameterHelper.GetCircuitPrefixSeparator(panelEl)));
            }

            return result;
        }
    }
}
