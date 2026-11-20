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

    /// <summary>One tied driver family in a TIE row's detail pane.</summary>
    public class TieFamilyRow
    {
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string CatalogNumber { get; set; }

        /// <summary>True when this family is the one already placed on the circuit.</summary>
        public bool IsPlaced { get; set; }

        public string PlacedNote => IsPlaced ? "← placed" : string.Empty;
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

        /// <summary>Comma-joined Switch IDs of the placed drivers (e.g. "X07a, X07b"); em dash when
        /// none are placed. Drives the grid's "Switch IDs" column and the search-box match.</summary>
        public string SwitchIdsDisplay =>
            _data.SwitchIds != null && _data.SwitchIds.Count > 0
                ? string.Join(", ", _data.SwitchIds)
                : "—";

        public string LoadName => _data.LoadName;
        public string DimmingProtocol => _data.DimmingProtocol;
        public double ApparentPower => _data.ApparentPower;

        /// <summary>RPS-fixture load (watts) — the power-supply contribution shown in the grid.</summary>
        public double RpsLoadWatts => _data.RpsLoadWatts;

        public string Panel => _data.Panel;

        public RpsStatus Status => _data.Status;

        public bool IsDeferred => _data.IsDeferred;

        /// <summary>A deferred circuit whose config has drifted since it was deferred — re-surfaced
        /// for review rather than staying silently neutral.</summary>
        public bool DeferralConfigChanged =>
            _data.IsDeferred
            && !string.Equals(_data.DeferredSignature, RpsDeferral.Signature(_data), StringComparison.Ordinal);

        /// <summary>The underlying classifier verdict, independent of deferral.</summary>
        private string BaseStatusText => _data.Status switch
        {
            RpsStatus.Ok => "OK",
            RpsStatus.Stale => "STALE",
            RpsStatus.Rebuild => "REBUILD",
            RpsStatus.NotDeployed => "NEW",
            RpsStatus.NoMatch => "NO MATCH",
            RpsStatus.DmxManaged => "DMX",
            RpsStatus.Tie => "TIE",
            _ => ""
        };

        /// <summary>Two or more driver families serve this circuit equally — no family is preferred
        /// until the fixture type is linked in Driver Match.</summary>
        public bool IsTie => _data.Status == RpsStatus.Tie;

        private string TiedFamilyNames =>
            string.Join(" / ", (_data.TiedCandidates ?? new List<DriverCandidateInfo>()).Select(c => c.FamilyName));

        /// <summary>What the Status column shows and the row tint keys off. Deferral masks the real
        /// verdict to a neutral "DEFERRED"; a drifted deferral becomes "REVIEW".</summary>
        public string StatusText
        {
            get
            {
                if (DeferralConfigChanged) return "REVIEW";
                if (_data.IsDeferred) return "DEFERRED";
                return BaseStatusText;
            }
        }

        /// <summary>Only non-deferred <see cref="RpsStatus.Stale"/> rows can be batch-corrected in place.</summary>
        public bool CanSelect => _data.Status == RpsStatus.Stale && !_data.IsDeferred;

        public string CurrentDisplay
        {
            get
            {
                // DMX-managed circuits show the wired decoder, not a (nonexistent) driver.
                if (_data.Status == RpsStatus.DmxManaged)
                {
                    if (_data.DecoderCount == 0) return "—";
                    string name = string.IsNullOrEmpty(_data.DecoderTypeName) ? "decoder" : _data.DecoderTypeName;
                    return $"{name} ×{_data.DecoderCount}";
                }
                if (_data.PlacedCount == 0) return "—";
                if (_data.DistinctPlacedTypeCount > 1) return $"mixed ({_data.PlacedCount})";
                return $"{_data.PlacedTypeName} ×{_data.PlacedCount}";
            }
        }

        public string RecommendedDisplay
        {
            get
            {
                if (IsTie) return $"tie ({_data.TiedCandidates?.Count ?? 0} families)";
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
                // Deferral context takes the Note slot: it explains why the row is neutral and
                // preserves the masked verdict so the original problem isn't lost.
                if (DeferralConfigChanged)
                    return $"deferred config changed — review (was {BaseStatusText})";
                if (_data.IsDeferred)
                    return $"deferred — was {BaseStatusText}";
                if (_data.Status == RpsStatus.DmxManaged)
                    return "DMX Decoder controlled";
                if (IsTie)
                    return $"tie: {TiedFamilyNames} → link in Driver Match";
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

        public string RecommendedHeader =>
            _data.Status == RpsStatus.DmxManaged ? "DMX decoder — not driver-managed"
            : IsTie ? "Tie: no driver family is preferred"
            : string.IsNullOrEmpty(_data.RecommendedTypeName) ? "No matching driver"
            : RecommendedDisplay;

        /// <summary>Why no driver matched (e.g. two links conflict, or the linked family can't serve
        /// the circuit); null when a driver matched. Shown under the header in the detail pane.</summary>
        public string RecommendationWarning =>
            _data.Recommendation != null && !_data.Recommendation.HasMatch
                ? _data.Recommendation.WarningMessage
                : null;

        public bool HasRecommendationWarning => !string.IsNullOrEmpty(RecommendationWarning);

        /// <summary>The tied families (one representative type each), winner first.</summary>
        public List<TieFamilyRow> TieRows => (_data.TiedCandidates ?? new List<DriverCandidateInfo>())
            .Select(c => new TieFamilyRow
            {
                FamilyName = c.FamilyName,
                TypeName = c.FamilyTypeName,
                CatalogNumber = c.CatalogNumber,
                IsPlaced = _data.PlacedFamilyRef.IsValid && c.FamilyRef == _data.PlacedFamilyRef
            })
            .ToList();

        /// <summary>The fixture types on this tied circuit that need a link (with project-wide circuit counts).</summary>
        public List<TieFixtureType> TieFixtureTypes => _data.UnlinkedFixtureTypes ?? new List<TieFixtureType>();

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

        /// <summary>Apply a defer/clear result from the Revit side. Updates the stored flag +
        /// signature and refreshes the masked status, tint, selectability, and note.</summary>
        public void SetDeferred(bool deferred, string signature)
        {
            _data.IsDeferred = deferred;
            _data.DeferredSignature = deferred ? signature : null;
            if (deferred)
                _isSelected = false;

            OnPropertyChanged(nameof(IsDeferred));
            OnPropertyChanged(nameof(DeferralConfigChanged));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanSelect));
            OnPropertyChanged(nameof(ReasonNote));
            OnPropertyChanged(nameof(HasReasonNote));
            OnPropertyChanged(nameof(IsSelected));
        }
    }
}
