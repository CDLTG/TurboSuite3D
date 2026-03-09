#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using TurboSuite.Shared.Helpers;

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

        private static readonly string[] TagFamilyNames =
        {
            "AL_Tag_Lighting Device (SwitchID)",
            "AL_Tag_Lighting Device (Switchleg)"
        };

        private Dictionary<string, ElementId> _tagTypeCache;

        /// <summary>
        /// Place two tags (SwitchID and Switchleg) on a lighting device instance.
        /// Tags are placed at the device location. Returns number of tags placed.
        /// </summary>
        public int TagDevice(FamilyInstance instance, View activeView)
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
