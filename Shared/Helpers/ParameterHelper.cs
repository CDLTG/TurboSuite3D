#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Shared.Constants;

namespace TurboSuite.Shared.Helpers
{
    /// <summary>
    /// Helper class for reading and writing Revit parameters
    /// </summary>
    public static class ParameterHelper
    {
        #region Element Parameters

        /// <summary>
        /// Get Switch ID from element (built-in parameter)
        /// </summary>
	public static string GetSwitchID(Element element)
        {
            if (element == null) return string.Empty;

            Parameter param = element.LookupParameter(ParameterNames.SwitchId);
            if (param != null && param.HasValue)
            {
                string value = param.AsString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }

        /// <summary>
        /// Get Type Mark from element (built-in TYPE parameter)
        /// </summary>
        public static string GetTypeMark(Element element)
        {
            if (element == null) return string.Empty;

            // Type Mark is a type parameter — read from FamilySymbol, not instance
            FamilyInstance familyInstance = element as FamilyInstance;
            if (familyInstance != null && familyInstance.Symbol != null)
            {
                Parameter param = familyInstance.Symbol.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
                if (param != null && param.HasValue)
                {
                    string value = param.AsString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }

                // Fallback: try by name on the type
                param = familyInstance.Symbol.LookupParameter(ParameterNames.TypeMark);
                if (param != null && param.HasValue)
                {
                    return param.AsString() ?? string.Empty;
                }
            }

            // Last resort: try on the instance itself (shouldn't work but just in case)
            Parameter instanceParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
            if (instanceParam != null && instanceParam.HasValue)
            {
                return instanceParam.AsString() ?? string.Empty;
            }

            return string.Empty;
        }

        /// <summary>
        /// Get Comments from element (built-in instance parameter)
        /// </summary>
        public static string GetComments(Element element)
        {
            if (element == null) return string.Empty;

            Parameter param = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (param != null && param.HasValue)
            {
                return param.AsString() ?? string.Empty;
            }

            return string.Empty;
        }

        /// <summary>
        /// Get Linear Length from element (instance parameter)
        /// </summary>
        public static double GetLinearLength(Element element)
        {
            if (element == null) return 0.0;

            Parameter param = element.LookupParameter(ParameterNames.LinearLength);
            return param?.AsDouble() ?? 0.0;
        }

        /// <summary>
        /// Get Linear Power (wattage) from element (instance parameter)
        /// </summary>
        public static double GetLinearPower(Element element)
        {
            if (element == null) return 0.0;

            Parameter param = element.LookupParameter(ParameterNames.LinearPower);
            if (param != null && param.HasValue)
            {
                double internalValue = param.AsDouble();
                try
                {
                    return UnitUtils.ConvertFromInternalUnits(
                        internalValue,
                        UnitTypeId.Watts);
                }
                catch
                {
                    return internalValue;
                }
            }
            return 0.0;
        }

        /// <summary>
        /// Get Manufacturer from a FamilySymbol (type parameter)
        /// </summary>
        public static string GetManufacturer(FamilySymbol symbol)
        {
            if (symbol == null) return string.Empty;

            Parameter param = symbol.get_Parameter(BuiltInParameter.ALL_MODEL_MANUFACTURER);
            if (param != null && param.HasValue)
            {
                return param.AsString() ?? string.Empty;
            }

            return string.Empty;
        }

        /// <summary>
        /// Get Manufacturer from a FamilyInstance (delegates to symbol overload)
        /// </summary>
        public static string GetManufacturer(FamilyInstance instance)
        {
            if (instance?.Symbol == null) return string.Empty;
            return GetManufacturer(instance.Symbol);
        }

        /// <summary>
        /// Get Dimming Protocol from a FamilySymbol (type parameter)
        /// </summary>
        public static string GetDimmingProtocol(FamilySymbol symbol)
        {
            if (symbol == null) return string.Empty;
            Parameter param = symbol.LookupParameter(ParameterNames.DimmingProtocol);
            if (param != null && param.HasValue)
            {
                return param.AsString() ?? string.Empty;
            }
            return string.Empty;
        }

        /// <summary>
        /// Get Dimming Protocol from a FamilyInstance (delegates to symbol overload)
        /// </summary>
        public static string GetDimmingProtocol(FamilyInstance instance)
        {
            if (instance?.Symbol == null) return string.Empty;
            return GetDimmingProtocol(instance.Symbol);
        }

        /// <summary>
        /// Get Voltage from a FamilySymbol (type parameter)
        /// </summary>
        public static string GetVoltage(FamilySymbol symbol)
        {
            if (symbol == null) return string.Empty;
            Parameter param = symbol.LookupParameter(ParameterNames.Voltage);
            if (param != null && param.HasValue)
            {
                if (param.StorageType == StorageType.String)
                {
                    string val = param.AsString() ?? string.Empty;
                    return val == "0" ? string.Empty : val;
                }
                if (param.StorageType == StorageType.Double)
                {
                    double internalValue = param.AsDouble();
                    if (Math.Abs(internalValue) < 0.001) return string.Empty;
                    try
                    {
                        double volts = UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Volts);
                        return volts.ToString("F0");
                    }
                    catch
                    {
                        return internalValue.ToString("F0");
                    }
                }
                if (param.StorageType == StorageType.Integer)
                {
                    int val = param.AsInteger();
                    return val == 0 ? string.Empty : val.ToString();
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Get Voltage from a FamilyInstance (delegates to symbol overload)
        /// </summary>
        public static string GetVoltage(FamilyInstance instance)
        {
            if (instance?.Symbol == null) return string.Empty;
            return GetVoltage(instance.Symbol);
        }

        /// <summary>
        /// Get Maximum Fixtures from a FamilySymbol (type parameter, integer).
        /// Returns 0 if the parameter is absent or has no value (meaning no limit).
        /// </summary>
        public static int GetMaximumFixtures(FamilySymbol symbol)
        {
            if (symbol == null) return 0;
            Parameter param = symbol.LookupParameter(ParameterNames.MaximumFixtures);
            if (param != null && param.HasValue)
            {
                return param.AsInteger();
            }
            return 0;
        }

        /// <summary>
        /// Get Power (total watts) from a FamilySymbol type parameter
        /// </summary>
        public static double GetDriverPower(FamilySymbol symbol)
        {
            if (symbol == null) return 0.0;

            Parameter param = symbol.LookupParameter(ParameterNames.Power);
            if (param != null && param.HasValue)
            {
                double internalValue = param.AsDouble();
                try
                {
                    return UnitUtils.ConvertFromInternalUnits(
                        internalValue,
                        UnitTypeId.Watts);
                }
                catch
                {
                    return internalValue;
                }
            }
            return 0.0;
        }

        /// <summary>
        /// Get Sub-Driver Power (watts per sub-driver) from a FamilySymbol type parameter
        /// </summary>
        public static double GetSubDriverPower(FamilySymbol symbol)
        {
            if (symbol == null) return 0.0;

            Parameter param = symbol.LookupParameter(ParameterNames.SubDriverPower);
            if (param != null && param.HasValue)
            {
                double internalValue = param.AsDouble();
                try
                {
                    return UnitUtils.ConvertFromInternalUnits(
                        internalValue,
                        UnitTypeId.Watts);
                }
                catch
                {
                    return internalValue;
                }
            }
            return 0.0;
        }

        #endregion

        #region Circuit Parameters

        /// <summary>
        /// Get Circuit Number from ElectricalSystem
        /// </summary>
        public static string GetCircuitNumber(ElectricalSystem circuit)
        {
            if (circuit == null) return string.Empty;

            Parameter param = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER);
            return param?.AsString() ?? string.Empty;
        }

        /// <summary>
        /// Get Load Name from ElectricalSystem
        /// </summary>
        public static string GetLoadName(ElectricalSystem circuit)
        {
            if (circuit == null) return string.Empty;

            Parameter param = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME);
            return param?.AsString() ?? string.Empty;
        }

        /// <summary>
        /// Get Load Classification Abbreviation from ElectricalSystem
        /// </summary>
        public static string GetLoadClassification(ElectricalSystem circuit)
        {
            if (circuit == null) return string.Empty;

            // Only get the "Load Classification Abbreviation" parameter directly
            // Do NOT extract from "Load Classification" as they are independent
            Parameter param = circuit.LookupParameter(ParameterNames.LoadClassificationAbbreviation);
            if (param != null && param.HasValue)
            {
                return param.AsString() ?? string.Empty;
            }

            return string.Empty;
        }

        /// <summary>
        /// Get Apparent Load from ElectricalSystem
        /// </summary>
        public static double GetApparentLoad(ElectricalSystem circuit)
        {
            if (circuit == null) return 0.0;

            Parameter param = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD);
            if (param != null && param.HasValue)
            {
                double internalValue = param.AsDouble();
                try
                {
                    double displayValue = Autodesk.Revit.DB.UnitUtils.ConvertFromInternalUnits(
                        internalValue,
                        Autodesk.Revit.DB.UnitTypeId.VoltAmperes);
                    return displayValue;
                }
                catch
                {
                    // Fallback: return raw value
                    return internalValue;
                }
            }

            return 0.0;
        }

        /// <summary>
        /// Get Panel Name from ElectricalSystem
        /// </summary>
        public static string GetPanelName(ElectricalSystem circuit)
        {
            if (circuit == null) return string.Empty;

            Parameter param = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_PANEL_PARAM);
            return param?.AsString() ?? string.Empty;
        }

        /// <summary>
        /// Get Comments from an ElectricalSystem circuit (built-in instance parameter)
        /// </summary>
        public static string GetCircuitComments(ElectricalSystem circuit)
        {
            if (circuit == null) return string.Empty;
            Parameter param = circuit.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (param != null && param.HasValue)
                return param.AsString() ?? string.Empty;
            return string.Empty;
        }

        /// <summary>
        /// Get Load Classification (full display name) from an ElectricalSystem circuit
        /// </summary>
        public static string GetLoadClassificationName(ElectricalSystem circuit)
        {
            if (circuit == null) return string.Empty;
            Parameter param = circuit.LookupParameter(ParameterNames.LoadClassification);
            if (param != null && param.HasValue)
                return param.AsString() ?? string.Empty;
            return string.Empty;
        }

        #endregion

        #region Panel Parameters

        /// <summary>Returns the electrical equipment panel instance with the given name, or null if not found.</summary>
        public static Element GetPanelElement(Document doc, string panelName)
        {
            if (doc == null || string.IsNullOrEmpty(panelName)) return null;

            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .OfClass(typeof(FamilyInstance))
                .FirstOrDefault(e => e.Name == panelName);
        }

        /// <summary>
        /// Returns the "Circuit Naming" parameter on a panel. Falls back to a manual ordered-parameter
        /// scan because <c>LookupParameter</c> doesn't always find built-in enum parameters by display name.
        /// </summary>
        public static Parameter FindCircuitNamingParameter(Element panel)
        {
            if (panel == null) return null;

            // LookupParameter may not find built-in enum parameters by display name.
            // Fall back to iterating all parameters for an exact name match.
            Parameter param = panel.LookupParameter(ParameterNames.CircuitNaming);
            if (param != null) return param;

            foreach (Parameter p in panel.GetOrderedParameters())
            {
                if (p.Definition.Name == "Circuit Naming")
                    return p;
            }
            return null;
        }

        /// <summary>Returns the panel's Circuit Naming option as its display string (e.g. "Standard", "Prefixed"), or "" if unset.</summary>
        public static string GetCircuitNaming(Element panel)
        {
            Parameter param = FindCircuitNamingParameter(panel);
            if (param == null || !param.HasValue) return "";
            // For ElementId storage, AsValueString() returns the display name
            string display = param.AsValueString();
            if (string.IsNullOrEmpty(display)) return "";
            return display;
        }

        // Built-in Revit ElementIds for Circuit Naming options (stable across projects)
        private static readonly Dictionary<string, ElementId> CircuitNamingMap = new Dictionary<string, ElementId>
        {
            { "Prefixed",   new ElementId(-7000010L) },
            { "Standard",   new ElementId(-7000011L) },
            { "Panel Name", new ElementId(-7000012L) },
            { "By Phase",   new ElementId(-7000013L) },
            { "By Project", new ElementId(-7000014L) },
        };

        public static readonly List<string> CircuitNamingOptions = new List<string>
        {
            "(None)", "Prefixed", "Standard", "Panel Name", "By Phase", "By Project"
        };

        /// <summary>Returns the panel's Circuit Prefix string, or empty if unset.</summary>
        public static string GetCircuitPrefix(Element panel)
        {
            if (panel == null) return string.Empty;
            Parameter param = panel.LookupParameter(ParameterNames.CircuitPrefix);
            if (param != null && param.HasValue)
                return param.AsString() ?? string.Empty;
            return string.Empty;
        }

        /// <summary>Returns the panel's Circuit Prefix Separator string, or empty if unset.</summary>
        public static string GetCircuitPrefixSeparator(Element panel)
        {
            if (panel == null) return string.Empty;
            Parameter param = panel.LookupParameter(ParameterNames.CircuitPrefixSeparator);
            if (param != null && param.HasValue)
                return param.AsString() ?? string.Empty;
            return string.Empty;
        }

        /// <summary>
        /// Sets the panel's Circuit Naming option from its display string (e.g. "Standard"). "(None)" or empty
        /// clears the parameter. Caller must run inside a Transaction. No-op if the parameter is read-only or
        /// the value is not one of <see cref="CircuitNamingOptions"/>.
        /// </summary>
        public static void SetCircuitNaming(Element panel, string value)
        {
            if (panel == null) return;

            Parameter param = FindCircuitNamingParameter(panel);
            if (param == null || param.IsReadOnly) return;

            if (string.IsNullOrEmpty(value) || value == "(None)")
            {
                param.Set(ElementId.InvalidElementId);
                return;
            }

            if (CircuitNamingMap.TryGetValue(value, out ElementId eid))
                param.Set(eid);
        }

        /// <summary>Sets the panel's Circuit Prefix. Caller must run inside a Transaction.</summary>
        public static void SetCircuitPrefix(Element panel, string value)
        {
            if (panel == null) return;
            Parameter param = panel.LookupParameter(ParameterNames.CircuitPrefix);
            if (param != null && !param.IsReadOnly)
                param.Set(value ?? "");
        }

        /// <summary>Sets the panel's Circuit Prefix Separator. Caller must run inside a Transaction.</summary>
        public static void SetCircuitPrefixSeparator(Element panel, string value)
        {
            if (panel == null) return;
            Parameter param = panel.LookupParameter(ParameterNames.CircuitPrefixSeparator);
            if (param != null && !param.IsReadOnly)
                param.Set(value ?? "");
        }

        #endregion

        #region Validation

        /// <summary>
        /// Check if element has a non-null, non-empty Switch ID
        /// </summary>
        public static bool HasSwitchID(Element element)
        {
            string switchId = GetSwitchID(element);
            return !string.IsNullOrWhiteSpace(switchId);
        }

        /// <summary>
        /// Check if a Lighting Fixture has the "Remote Power Supply" type parameter checked (Yes)
        /// </summary>
        public static bool HasRemotePowerSupply(FamilyInstance element)
        {
            if (element?.Symbol == null) return false;
            Parameter param = element.Symbol.LookupParameter(ParameterNames.RemotePowerSupply);
            return param != null && param.HasValue && param.AsInteger() == 1;
        }

        #endregion
    }
}
