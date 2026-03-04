#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.ViewModels
{
    public class OptimizePreviewViewModel
    {
        public OptimizePreviewViewModel(RedistributionPlan plan)
        {
            Plan = plan;
            HasChanges = plan.HasChanges;
            TotalMoves = plan.Moves.Count;

            LocationSummaries = plan.LocationSummaries.Values
                .OrderBy(ls => ls.LocationNumber)
                .Select(ls => new LocationPreviewViewModel(ls, plan.Moves))
                .ToList();
        }

        public RedistributionPlan Plan { get; }
        public bool HasChanges { get; }
        public bool NoChanges => !HasChanges;
        public int TotalMoves { get; }
        public List<LocationPreviewViewModel> LocationSummaries { get; }
    }

    public class LocationPreviewViewModel
    {
        public LocationPreviewViewModel(LocationSummaryPair summary, List<CircuitMove> allMoves)
        {
            LocationNumber = summary.LocationNumber;
            Before = summary.Before;
            After = summary.After;

            var panelNames = new HashSet<string>(
                summary.Before.Select(p => p.PanelName),
                StringComparer.OrdinalIgnoreCase);

            MovesInLocation = allMoves
                .Where(m => panelNames.Contains(m.FromPanel) || panelNames.Contains(m.ToPanel))
                .OrderBy(m => m.FromPanel)
                .ThenBy(m => m.CircuitNumber)
                .ToList();

            BeforeModuleTotal = Before.Sum(p => p.TotalModules);
            AfterModuleTotal = After.Sum(p => p.TotalModules);
            ModuleSavings = BeforeModuleTotal - AfterModuleTotal;
        }

        public int LocationNumber { get; }
        public List<PanelSummary> Before { get; }
        public List<PanelSummary> After { get; }
        public List<CircuitMove> MovesInLocation { get; }
        public int BeforeModuleTotal { get; }
        public int AfterModuleTotal { get; }
        public int ModuleSavings { get; }
        public bool HasSavings => ModuleSavings > 0;
    }
}
