#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Driver.Models;
using TurboSuite.Driver.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Driver.ViewModels
{
    /// <summary>A grouped fixture line for the detail pane's fixtures table.</summary>
    public class GroupedFixture
    {
        public int Quantity { get; set; }
        public string TypeMark { get; set; }
        public string Comments { get; set; }
        public double LinearLength { get; set; }
    }

    /// <summary>
    /// One row in the TurboRPS dashboard grid. Wraps a <see cref="RpsCircuitData"/> DTO and
    /// exposes display strings, the (checkbox) selection state — settable only on
    /// <see cref="RpsStatus.Stale"/> rows — and the recommended packing/fixtures for the
    /// detail pane.
    /// </summary>
    public class RpsCircuitRowViewModel : ViewModelBase
    {
        private readonly RpsCircuitData _data;
        private bool _isSelected;
        private bool _isActiveRow;

        public RpsCircuitRowViewModel(RpsCircuitData data)
        {
            _data = data;
        }

        public RpsCircuitData Data => _data;

        public string CircuitNumber => _data.CircuitNumber;
        public string LoadName => _data.LoadName;
        public string DimmingProtocol => _data.DimmingProtocol;
        public double ApparentPower => _data.ApparentPower;

        /// <summary>RPS-fixture load (watts) — the power-supply contribution shown in the grid.</summary>
        public double RpsLoadWatts => _data.RpsLoadWatts;

        public string Panel => _data.Panel;

        public RpsStatus Status => _data.Status;

        public string StatusText => _data.Status switch
        {
            RpsStatus.Ok => "OK",
            RpsStatus.Stale => "STALE",
            RpsStatus.Rebuild => "REBUILD",
            RpsStatus.NotDeployed => "NEW",
            RpsStatus.NoMatch => "NO MATCH",
            _ => ""
        };

        /// <summary>Only <see cref="RpsStatus.Stale"/> rows can be batch-corrected in place.</summary>
        public bool CanSelect => _data.Status == RpsStatus.Stale;

        public string CurrentDisplay
        {
            get
            {
                if (_data.PlacedCount == 0) return "—";
                if (_data.DistinctPlacedTypeCount > 1) return $"mixed ({_data.PlacedCount})";
                return $"{_data.PlacedTypeName} ×{_data.PlacedCount}";
            }
        }

        public string RecommendedDisplay
        {
            get
            {
                if (string.IsNullOrEmpty(_data.RecommendedTypeName)) return "—";
                return $"{_data.RecommendedTypeName} ×{_data.RecommendedCount}";
            }
        }

        /// <summary>For Rebuild rows: the "→ TurboDriver" reason. Otherwise, when the linear
        /// cut-list is also stale, an info-only re-split note (shown even on Ok/Stale rows).</summary>
        public string ReasonNote
        {
            get
            {
                if (_data.Status == RpsStatus.Rebuild)
                    return _data.RebuildReason;
                if (_data.HasSplitSegments)
                    return "linear cut-list changed — re-run TurboDriver to re-split";
                return null;
            }
        }

        public bool HasReasonNote => !string.IsNullOrEmpty(ReasonNote);

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                // Guard: only Stale rows are selectable; ignore attempts to check others.
                bool target = value && CanSelect;
                SetProperty(ref _isSelected, target);
            }
        }

        public bool IsActiveRow
        {
            get => _isActiveRow;
            set => SetProperty(ref _isActiveRow, value);
        }

        // ---- Detail pane ----

        public List<GroupedFixture> GroupedFixtures => _data.Fixtures
            .GroupBy(f => new { f.TypeMark, f.Comments, LinearLength = Math.Round(f.LinearLength, 4) })
            .Select(g => new GroupedFixture
            {
                Quantity = g.Count(),
                TypeMark = g.Key.TypeMark,
                Comments = g.Key.Comments,
                LinearLength = g.Key.LinearLength
            })
            .OrderBy(g => g.TypeMark)
            .ToList();

        public List<SubDriverAssignment> SubDriverAssignments =>
            _data.Recommendation?.SubDriverAssignments;

        public string RecommendedHeader => string.IsNullOrEmpty(_data.RecommendedTypeName)
            ? "No matching driver"
            : RecommendedDisplay;

        /// <summary>After a successful in-place swap, flip this row to Ok and update the placed
        /// summary to the recommended type. The split-note (if any) persists — only the driver
        /// hardware type is now current, not necessarily the linear cut-list.</summary>
        public void RefreshSwapped()
        {
            _data.PlacedTypeName = _data.RecommendedTypeName;
            _data.PlacedCount = _data.RecommendedCount;
            _data.DistinctPlacedTypeCount = 1;
            _data.Status = RpsStatus.Ok;
            _isSelected = false;

            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanSelect));
            OnPropertyChanged(nameof(CurrentDisplay));
            OnPropertyChanged(nameof(ReasonNote));
            OnPropertyChanged(nameof(HasReasonNote));
            OnPropertyChanged(nameof(IsSelected));
        }
    }
}
