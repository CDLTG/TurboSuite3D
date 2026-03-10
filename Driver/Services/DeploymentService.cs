#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using TurboSuite.Shared.Helpers;
using ElectricalWire = Autodesk.Revit.DB.Electrical.Wire;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Model-level operations for deploying power supplies.
    /// All methods expect to be called inside an active Transaction.
    /// </summary>
    public class DeploymentService
    {
        private readonly Document _doc;

        public DeploymentService(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Place a power supply family instance at the given point.
        /// </summary>
        public FamilyInstance PlacePowerSupply(XYZ point, FamilySymbol symbol)
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                _doc.Regenerate();
            }

            var instance = _doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
            _doc.Regenerate();
            return instance;
        }

        /// <summary>
        /// Add a placed power supply to an existing electrical circuit.
        /// Returns true on success.
        /// </summary>
        public bool AddToCircuit(FamilyInstance instance, ElementId circuitId)
        {
            var circuit = _doc.GetElement(circuitId) as ElectricalSystem;
            if (circuit == null)
                return false;

            var connector = GeometryHelper.GetElectricalConnector(instance);
            if (connector == null)
                return false;

            var elSet = new ElementSet();
            elSet.Insert(instance);
            circuit.AddToCircuit(elSet);
            return true;
        }

        /// <summary>
        /// Set the Switch ID parameter on a placed power supply to establish the switch relationship.
        /// </summary>
        public bool SetSwitchId(FamilyInstance instance, string switchId)
        {
            if (string.IsNullOrEmpty(switchId))
                return false;

            Parameter param = instance.LookupParameter("Switch ID");
            if (param == null || param.IsReadOnly)
                return false;

            param.Set(switchId);
            return true;
        }

        private const string SwitchlegTagFamily = "AL_Tag_Lighting Device (Switchleg)";

        private static readonly string[] TagFamilyNames =
        {
            "AL_Tag_Lighting Device (SwitchID)",
            SwitchlegTagFamily
        };

        private Dictionary<string, ElementId> _tagTypeCache;

        /// <summary>
        /// Place tags on a lighting device instance.
        /// When includeSwitchleg is true (default), places both SwitchID and Switchleg tags.
        /// When false, places only the SwitchID tag.
        /// Returns number of tags placed.
        /// </summary>
        public int TagDevice(FamilyInstance instance, View activeView, bool includeSwitchleg = true)
        {
            if (_tagTypeCache == null)
                _tagTypeCache = ResolveTagTypes();

            var location = GeometryHelper.GetFixtureLocation(instance);
            if (location == null)
                return 0;

            int placed = 0;
            var reference = new Reference(instance);

            foreach (string familyName in TagFamilyNames)
            {
                if (!includeSwitchleg && familyName == SwitchlegTagFamily)
                    continue;

                if (!_tagTypeCache.TryGetValue(familyName, out var tagTypeId))
                    continue;

                var tag = IndependentTag.Create(
                    _doc, activeView.Id, reference, false,
                    TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal,
                    location);
                tag.ChangeTypeId(tagTypeId);
                tag.TagHeadPosition = location;
                placed++;
            }

            return placed;
        }

        /// <summary>
        /// Collect wire ElementIds connected between any of the given devices.
        /// Must be called before deleting the devices.
        /// </summary>
        public static List<ElementId> GetWiresBetweenDevices(Document doc, List<ElementId> deviceIds)
        {
            var deviceIdSet = new HashSet<ElementId>(deviceIds);
            var wireIds = new HashSet<ElementId>();

            foreach (var deviceId in deviceIds)
            {
                var device = doc.GetElement(deviceId) as FamilyInstance;
                if (device == null) continue;

                var connector = GeometryHelper.GetElectricalConnector(device);
                if (connector == null) continue;

                foreach (Connector connected in connector.AllRefs)
                {
                    if (connected.Owner is ElectricalWire wire)
                    {
                        wireIds.Add(wire.Id);
                    }
                }
            }

            return wireIds.ToList();
        }

        /// <summary>
        /// Create a straight wire between two power supply instances.
        /// Returns true on success.
        /// </summary>
        public bool CreateWireBetween(FamilyInstance device1, FamilyInstance device2, View activeView)
        {
            var c1 = GeometryHelper.GetElectricalConnector(device1);
            var c2 = GeometryHelper.GetElectricalConnector(device2);
            if (c1 == null || c2 == null)
                return false;

            if (_wireTypeId == null)
                _wireTypeId = ResolveWireTypeId();

            if (_wireTypeId == ElementId.InvalidElementId)
                return false;

            IList<XYZ> points = new List<XYZ> { c1.Origin, c2.Origin };
            ElectricalWire.Create(_doc, _wireTypeId, activeView.Id, WiringType.Chamfer, points, c1, c2);
            return true;
        }

        private ElementId _wireTypeId;

        private ElementId ResolveWireTypeId()
        {
            var wireType = new FilteredElementCollector(_doc)
                .OfClass(typeof(WireType))
                .Cast<WireType>()
                .FirstOrDefault();

            return wireType?.Id ?? ElementId.InvalidElementId;
        }

        private Dictionary<string, ElementId> ResolveTagTypes()
        {
            var result = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

            var allTagTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_LightingDeviceTags)
                .Cast<FamilySymbol>()
                .ToList();

            foreach (string familyName in TagFamilyNames)
            {
                var match = allTagTypes.FirstOrDefault(fs =>
                    string.Equals(fs.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    result[familyName] = match.Id;
            }

            return result;
        }
    }
}
