#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace TurboSuite.Driver.Models
{
    /// <summary>
    /// Plan for deploying power supplies across all qualifying circuits.
    /// Built by MainViewModel when user clicks Warp; consumed by DeploymentExecutor.
    /// </summary>
    public class DeploymentPlan
    {
        public List<CircuitDeployment> Circuits { get; set; } = new List<CircuitDeployment>();

        /// <summary>
        /// Total number of power supply instances to place across all circuits.
        /// </summary>
        public int TotalQuantity
        {
            get
            {
                int total = 0;
                foreach (var c in Circuits)
                    total += c.QuantityToPlace;
                return total;
            }
        }
    }

    /// <summary>
    /// Deployment instructions for a single circuit.
    /// </summary>
    public class CircuitDeployment
    {
        public ElementId CircuitId { get; set; }
        public string CircuitNumber { get; set; }
        public FamilySymbol DriverSymbol { get; set; }
        public int QuantityToPlace { get; set; }
        public string SwitchId { get; set; }
    }
}
