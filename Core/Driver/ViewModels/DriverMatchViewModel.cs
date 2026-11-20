#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Driver.Models;
using TurboSuite.Driver.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Driver.ViewModels
{
    /// <summary>
    /// TurboRPS Driver Match tab. Picks an RPS fixture type and a driver <b>family</b>, shows one row
    /// per driver type in that family (<see cref="DriverMatchDiagnostic.CompareFamily"/> — pure,
    /// recomputed on every pick), and owns the project's fixture-type → driver-family links:
    /// Link / Change link / Unlink plus the "Links in this project" list. Link writes and the Reload
    /// re-collect go through the work queue; after any link change <see cref="LinksChanged"/> fires so
    /// the shell can re-scan the Circuits tab.
    /// </summary>
    public class DriverMatchViewModel : ViewModelBase
    {
        private readonly IDriverMatchOperations _ops;
        private readonly IRevitWorkQueue _workQueue;

        private DriverFamilyOption _selectedFamily;
        private FixtureOption _selectedFixture;
        private string _summary;
        private bool _matches;
        private bool _hasResult;
        private int _matchingCount;
        private bool _isBusy;
        private string _status;
        private string _fixtureSummary;
        private string _linkStatusText;

        public DriverMatchViewModel(MatchCatalog catalog, IDriverMatchOperations ops, IRevitWorkQueue workQueue)
        {
            _ops = ops;
            _workQueue = workQueue;

            DriverFamilies = new ObservableCollection<DriverFamilyOption>();
            Fixtures = new ObservableCollection<FixtureOption>();
            Rows = new ObservableCollection<TypeMatchRow>();
            Links = new ObservableCollection<DriverLink>();

            ReloadCommand = new RelayCommand(Reload, () => !_isBusy);
            LinkCommand = new RelayCommand(Link, CanLink);
            UnlinkCommand = new RelayCommand(Unlink, () => !_isBusy && SelectedFixtureLink != null);
            UnlinkLinkCommand = new RelayCommand<DriverLink>(
                l => RemoveLink(l?.FixtureTypeKey),
                l => !_isBusy && l != null && !string.IsNullOrEmpty(l.FixtureTypeKey));

            Load(catalog);
        }

        /// <summary>Raised after a link is saved or removed, so the Circuits tab can re-scan.</summary>
        public event EventHandler LinksChanged;

        public ObservableCollection<DriverFamilyOption> DriverFamilies { get; }
        public ObservableCollection<FixtureOption> Fixtures { get; }

        /// <summary>One row per driver type in the selected family, compared to the selected fixture.</summary>
        public ObservableCollection<TypeMatchRow> Rows { get; }

        /// <summary>Every link stored in the project, broken ones included (flagged).</summary>
        public ObservableCollection<DriverLink> Links { get; }

        public ICommand ReloadCommand { get; }
        public ICommand LinkCommand { get; }
        public ICommand UnlinkCommand { get; }

        /// <summary>Per-row Unlink in the links list; the parameter is the <see cref="DriverLink"/>.</summary>
        public ICommand UnlinkLinkCommand { get; }

        public DriverFamilyOption SelectedFamily
        {
            get => _selectedFamily;
            set { if (SetProperty(ref _selectedFamily, value)) Recompute(); }
        }

        public FixtureOption SelectedFixture
        {
            get => _selectedFixture;
            set { if (SetProperty(ref _selectedFixture, value)) Recompute(); }
        }

        /// <summary>Plain-language one-line verdict for the summary bar.</summary>
        public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

        /// <summary>True when at least one type in the family can serve the fixture (green/red bar).</summary>
        public bool Matches { get => _matches; private set => SetProperty(ref _matches, value); }

        /// <summary>True once both a family and a fixture are picked (bar is neutral until then).</summary>
        public bool HasResult { get => _hasResult; private set => SetProperty(ref _hasResult, value); }

        /// <summary>The selected fixture's driver-relevant spec on one line.</summary>
        public string FixtureSummary { get => _fixtureSummary; private set => SetProperty(ref _fixtureSummary, value); }

        /// <summary>Whether (and to what) the selected fixture type is linked.</summary>
        public string LinkStatusText { get => _linkStatusText; private set => SetProperty(ref _linkStatusText, value); }

        /// <summary>"Link to family" when unlinked; "Change link" when linked to a different family;
        /// "Linked" when already linked to the selected family.</summary>
        public string LinkButtonText
        {
            get
            {
                var link = SelectedFixtureLink;
                if (link == null) return "Link to family";
                if (_selectedFamily != null && link.DriverFamilyRef == _selectedFamily.FamilyRef && !link.IsBroken)
                    return "Linked";
                return "Change link";
            }
        }

        /// <summary>Footer line: how much is loaded.</summary>
        public string Status { get => _status; private set => SetProperty(ref _status, value); }

        public string LinksHeader => $"Links in this project ({Links.Count})";

        public bool IsBusy
        {
            get => _isBusy;
            private set { if (SetProperty(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        /// <summary>The stored link for the selected fixture type (broken or not), if any.</summary>
        private DriverLink SelectedFixtureLink
        {
            get
            {
                var typeRef = _selectedFixture?.TypeRef ?? ElementRef.None;
                if (!typeRef.IsValid) return null;
                return Links.FirstOrDefault(l => l.FixtureTypeRef == typeRef);
            }
        }

        private void Load(MatchCatalog catalog)
        {
            DriverFamilies.Clear();
            Fixtures.Clear();
            Links.Clear();
            if (catalog != null)
            {
                foreach (var d in catalog.Families) DriverFamilies.Add(d);
                foreach (var f in catalog.Fixtures) Fixtures.Add(f);
                foreach (var l in catalog.Links) Links.Add(l);
            }
            OnPropertyChanged(nameof(LinksHeader));

            int broken = Links.Count(l => l.IsBroken);
            Status = $"{DriverFamilies.Count} driver famil{(DriverFamilies.Count == 1 ? "y" : "ies")} · " +
                     $"{Fixtures.Count} RPS fixture type(s) · {Links.Count} link(s)" +
                     (broken > 0 ? $" ({broken} broken)" : string.Empty);
            Recompute();
        }

        private void Recompute()
        {
            Rows.Clear();
            FixtureSummary = DescribeFixture(_selectedFixture?.Fixture);
            UpdateLinkState();

            if (_selectedFamily == null || _selectedFixture == null)
            {
                HasResult = false;
                Matches = false;
                _matchingCount = 0;
                Summary = "Select an RPS fixture and a driver family to compare.";
                CommandManager.InvalidateRequerySuggested();
                return;
            }

            var report = DriverMatchDiagnostic.CompareFamily(_selectedFixture.Fixture, _selectedFamily);
            foreach (var r in report.Rows) Rows.Add(r);
            _matchingCount = report.MatchingCount;
            Matches = report.Matches;
            Summary = report.Summary;
            HasResult = true;
            CommandManager.InvalidateRequerySuggested();
        }

        private void UpdateLinkState()
        {
            OnPropertyChanged(nameof(LinkButtonText));
            if (_selectedFixture == null)
            {
                LinkStatusText = string.Empty;
                return;
            }

            var link = SelectedFixtureLink;
            if (link == null)
                LinkStatusText = "Not linked — TurboDriver picks the best-ranked family; a tie needs a link.";
            else if (link.IsBroken)
                LinkStatusText = $"Linked to a driver family that no longer exists ({link.FamilyDisplay}) — the link is ignored. Unlink or change it.";
            else
                LinkStatusText = $"Linked to {link.FamilyDisplay} — TurboDriver only uses this family for circuits carrying this type.";
        }

        private static string DescribeFixture(FixtureData f)
        {
            if (f == null) return string.Empty;
            string Show(string v) => string.IsNullOrWhiteSpace(v) ? "(blank)" : v.Trim();
            var parts = new List<string>
            {
                $"Voltage {Show(f.Voltage)}",
                $"Dimming {Show(f.DimmingProtocol)}",
                $"Mfr {Show(f.Manufacturer)}",
                f.EffectiveWattage > 0 ? $"{f.EffectiveWattage:0.##} W" : "W per-foot on the instance"
            };
            return string.Join(" · ", parts);
        }

        private bool CanLink()
        {
            if (_isBusy || _selectedFixture == null || _selectedFamily == null) return false;
            if (!_selectedFixture.TypeRef.IsValid || !_selectedFamily.FamilyRef.IsValid) return false;
            if (_selectedFamily.IsTbd || _matchingCount < 1) return false;
            var link = SelectedFixtureLink;
            return link == null || link.IsBroken || link.DriverFamilyRef != _selectedFamily.FamilyRef;
        }

        private void Link()
        {
            if (!CanLink()) return;
            var fixtureRef = _selectedFixture.TypeRef;
            var familyRef = _selectedFamily.FamilyRef;

            IsBusy = true;
            _workQueue.Enqueue(
                () => _ops.SetLink(fixtureRef, familyRef),
                result => OnLinkWriteComplete(result));
        }

        private void Unlink() => RemoveLink(SelectedFixtureLink?.FixtureTypeKey);

        private void RemoveLink(string fixtureTypeKey)
        {
            if (_isBusy || string.IsNullOrEmpty(fixtureTypeKey)) return;

            IsBusy = true;
            _workQueue.Enqueue(
                () => _ops.RemoveLink(fixtureTypeKey),
                result => OnLinkWriteComplete(result));
        }

        /// <summary>After a link write: on success, re-collect (fresh link list) and tell the shell to
        /// re-scan the circuits. Null = refused / rolled back — already reported by the shim.</summary>
        private void OnLinkWriteComplete(object result)
        {
            bool ok = result is bool b && b;
            IsBusy = false;
            if (!ok) return;

            Reload();
            LinksChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Reload()
        {
            IsBusy = true;
            // Keep the current picks across the reload (families may have been reloaded/renamed).
            var familyRef = _selectedFamily?.FamilyRef ?? ElementRef.None;
            string familyName = _selectedFamily?.FamilyName;
            var fixtureRef = _selectedFixture?.TypeRef ?? ElementRef.None;
            string fixtureName = _selectedFixture?.DisplayName;

            _workQueue.Enqueue(
                () => _ops.Collect(),
                result =>
                {
                    try
                    {
                        if (result is MatchCatalog cat)
                        {
                            Load(cat);
                            SelectedFamily = DriverFamilies.FirstOrDefault(d => familyRef.IsValid && d.FamilyRef == familyRef)
                                ?? DriverFamilies.FirstOrDefault(d => d.FamilyName == familyName);
                            SelectedFixture = Fixtures.FirstOrDefault(f => fixtureRef.IsValid && f.TypeRef == fixtureRef)
                                ?? Fixtures.FirstOrDefault(f => f.DisplayName == fixtureName);
                        }
                    }
                    finally
                    {
                        IsBusy = false;
                    }
                });
        }
    }
}
