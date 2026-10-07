#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Number.Services;
using TurboSuite.Shared.ViewModels;
using TurboSuite.Zones.Services;

namespace TurboSuite.Number.ViewModels
{
    public class RoomOrderItem : ViewModelBase
    {
        public string Name { get; }

        private int _position;
        public int Position
        {
            get => _position;
            set => SetProperty(ref _position, value);
        }

        private int _clickOrder;
        public int ClickOrder
        {
            get => _clickOrder;
            set
            {
                if (SetProperty(ref _clickOrder, value))
                    OnPropertyChanged(nameof(IsClicked));
            }
        }

        public bool IsClicked => _clickOrder > 0;

        private bool _isReordering;
        public bool IsReordering
        {
            get => _isReordering;
            set => SetProperty(ref _isReordering, value);
        }

        // ── Location (Phase A: located keypads) ───────────────────────────────────────────────────
        // A room's location drives where its keypads land on the control one-line. The value is
        // resolved by a ladder: an explicit designer pick wins, else a unanimous auto-seed derived
        // from the room's control signals, else blank (a valid state). Only the explicit pick is
        // persisted; the seed is re-derived each session and injected at construction.

        private RoomLocationSeed _seed = RoomLocationSeeder.Classify(Array.Empty<int>());

        /// <summary>The derived (auto-seed) location classification for this room — unanimous,
        /// mixed, or none. Set once at construction; not persisted.</summary>
        public RoomLocationSeed Seed => _seed;

        /// <summary>Whether a keypad resolves to this room. The only row-state the sidebar actively
        /// prompts is a keypad-bearing room with no resolved location.</summary>
        public bool HasKeypad { get; private set; }

        private int _explicitLocation;
        /// <summary>The designer's explicit location pick (0 = none). Editable; persisted. Setting it
        /// re-fires every derived location property.</summary>
        public int ExplicitLocation
        {
            get => _explicitLocation;
            set
            {
                int v = value < 0 ? 0 : value;
                if (SetProperty(ref _explicitLocation, v))
                    RaiseLocationDerived();
            }
        }

        /// <summary>The effective location after the resolution ladder (0 = blank).</summary>
        public int ResolvedLocation => RoomLocationSeeder.Resolve(_explicitLocation, _seed);

        /// <summary>True when the location comes from an explicit pick (shown normal, not greyed).</summary>
        public bool IsExplicit => _explicitLocation > 0;

        /// <summary>True when the shown location is an un-overridden unanimous auto-seed (greyed).</summary>
        public bool IsSeeded => !IsExplicit && _seed.Kind == RoomLocationSeedKind.Unanimous;

        /// <summary>True when the signals disagree and no pick resolves it (mixed-flag hint).</summary>
        public bool IsMixed => !IsExplicit && _seed.Kind == RoomLocationSeedKind.Mixed;

        /// <summary>The one actively-prompted state: a keypad-bearing room with nowhere to route.</summary>
        public bool NeedsAssign => HasKeypad && ResolvedLocation == 0;

        /// <summary>The badge text — the resolved location, or empty when blank.</summary>
        public string LocationDisplay => ResolvedLocation > 0 ? ResolvedLocation.ToString() : string.Empty;

        /// <summary>Two-way text for the editable location combo. Shows the resolved value (seed or
        /// pick); setting it to a positive integer records an explicit pick, and clearing it (blank or
        /// non-positive) drops the pick so the row reverts to its auto-seed (or blank).</summary>
        public string LocationEntry
        {
            get => ResolvedLocation > 0 ? ResolvedLocation.ToString() : string.Empty;
            set
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    int.TryParse(value.Trim(), out int loc) && loc > 0)
                    ExplicitLocation = loc;
                else
                    ExplicitLocation = 0;
            }
        }

        /// <summary>The mixed-flag histogram hint, e.g. "1/4" (the disagreeing candidates).</summary>
        public string MixedHint =>
            IsMixed && _seed.Candidates != null ? string.Join("/", _seed.Candidates) : string.Empty;

        public RoomOrderItem(string name, int position)
        {
            Name = name;
            _position = position;
        }

        /// <summary>Seeds the location state at construction (no save; the setter path is for user
        /// edits only). Called by the VM factory after it resolves the room's seed and keypad flag.</summary>
        internal void InitLocation(RoomLocationSeed seed, bool hasKeypad, int explicitLocation)
        {
            _seed = seed;
            HasKeypad = hasKeypad;
            _explicitLocation = explicitLocation < 0 ? 0 : explicitLocation;
            RaiseLocationDerived();
        }

        private void RaiseLocationDerived()
        {
            OnPropertyChanged(nameof(ResolvedLocation));
            OnPropertyChanged(nameof(IsExplicit));
            OnPropertyChanged(nameof(IsSeeded));
            OnPropertyChanged(nameof(IsMixed));
            OnPropertyChanged(nameof(NeedsAssign));
            OnPropertyChanged(nameof(LocationDisplay));
            OnPropertyChanged(nameof(LocationEntry));
            OnPropertyChanged(nameof(MixedHint));
        }
    }

    /// <summary>
    /// Window-level, tab-independent editor for the project-wide room order: the ordered
    /// room list plus its click-order + drag reorder gestures. Hoisted out of
    /// <see cref="KeypadTabViewModel"/> so the permanent sidebar and every consumer
    /// (keypad numbering, the CircuitNumber "Sort by room order") share one order.
    ///
    /// Population is the project-wide room enumeration (all Spaces/regions), injected as
    /// <c>allRoomNames</c>, not just keypad rows — so keypad-less and circuit-less rooms
    /// are orderable and the click-order gesture works for every room. Raises
    /// <see cref="OrderChanged"/> whenever the committed order changes (apply / move /
    /// reset), which the keypad grid subscribes to to re-run its custom sort.
    ///
    /// Phase A also layers a per-room <b>location</b> onto each row (see
    /// <see cref="RoomOrderItem.ResolvedLocation"/>): injected auto-seeds plus the designer's
    /// explicit picks, persisted separately via <see cref="IRoomOrderStore.SaveRoomLocations"/>.
    /// </summary>
    public class RoomOrderViewModel : ViewModelBase
    {
        private readonly IRoomOrderStore _roomOrderStore;
        private readonly IRevitWorkQueue _workQueue;
        private readonly IReadOnlyList<string> _allRoomNames;
        private readonly Dictionary<string, RoomLocationSeed> _roomSeeds;
        private readonly Dictionary<string, int> _explicitLocations;
        private readonly HashSet<string> _keypadRooms;
        private readonly RoomLocationSeed _emptySeed = RoomLocationSeeder.Classify(Array.Empty<int>());
        private bool _isReordering;
        private int _nextClickOrder;
        private Dictionary<string, int> _reorderSnapshot;
        private string _searchText = string.Empty;

        public ObservableCollection<RoomOrderItem> RoomOrder { get; } = new ObservableCollection<RoomOrderItem>();

        /// <summary>Raised after the committed room order changes (apply/move/reset), so
        /// consumers such as the keypad grid can re-sort against the new order.</summary>
        public event Action OrderChanged;

        /// <summary>Current display order of room names (top-to-bottom). The keys are the
        /// same strings circuit-room resolution produces, so a downstream sort keyed on
        /// this list never drifts from the list itself.</summary>
        public IReadOnlyList<string> OrderedRoomNames => RoomOrder.Select(r => r.Name).ToList();

        /// <summary>The resolved location per room name (0 = blank). Downstream consumers (the
        /// control one-line keypad placement) read this map; it folds explicit picks over
        /// auto-seeds via the same ladder the rows display.</summary>
        /// <summary>The project's known location numbers (ascending) — every location any room's
        /// signals voted for, plus every explicit pick. Seeds the editable location combo's dropdown;
        /// the combo stays typable so a brand-new (e.g. orphan) location can still be entered.</summary>
        public IReadOnlyList<int> KnownLocations { get; }

        public IReadOnlyDictionary<string, int> ResolvedLocationsByRoom =>
            RoomOrder.Where(r => r.ResolvedLocation > 0)
                     .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                     .ToDictionary(g => g.Key, g => g.First().ResolvedLocation, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Live substring filter (case-insensitive) on the Room Order list. Only surfaced
        /// in the UI during reorder mode; it filters the visible list only — click-order and
        /// Apply read the full <see cref="RoomOrder"/> collection, so a filtered-out room
        /// keeps its number and still lands in the applied order.
        /// </summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty))
                    CollectionViewSource.GetDefaultView(RoomOrder).Refresh();
            }
        }

        public bool IsReordering
        {
            get => _isReordering;
            set
            {
                if (SetProperty(ref _isReordering, value))
                    OnPropertyChanged(nameof(IsNotReordering));
            }
        }

        public bool IsNotReordering => !_isReordering;

        public ICommand ResetRoomOrderCommand { get; }
        public ICommand StartReorderCommand { get; }
        public ICommand ApplyReorderCommand { get; }
        public ICommand CancelReorderCommand { get; }

        public RoomOrderViewModel(IReadOnlyList<string> allRoomNames,
            IReadOnlyList<(string Name, int ClickOrder)> savedRoomOrder,
            IReadOnlyList<(string Name, int Location)> savedRoomLocations,
            IReadOnlyDictionary<string, RoomLocationSeed> roomSeeds,
            IReadOnlyCollection<string> keypadRoomNames,
            IRevitWorkQueue workQueue, IRoomOrderStore roomOrderStore)
        {
            _allRoomNames = allRoomNames ?? Array.Empty<string>();
            _workQueue = workQueue;
            _roomOrderStore = roomOrderStore;

            _roomSeeds = new Dictionary<string, RoomLocationSeed>(StringComparer.OrdinalIgnoreCase);
            if (roomSeeds != null)
                foreach (var kv in roomSeeds) _roomSeeds[kv.Key] = kv.Value;

            _explicitLocations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (savedRoomLocations != null)
                foreach (var (name, loc) in savedRoomLocations)
                    if (!string.IsNullOrEmpty(name) && loc > 0) _explicitLocations[name] = loc;

            _keypadRooms = new HashSet<string>(
                keypadRoomNames?.Where(n => !string.IsNullOrEmpty(n)) ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            // The combo dropdown: every location the signals voted for (seed candidates) plus every
            // explicit pick, ascending. Editable, so a not-yet-voted location can still be typed.
            var known = new SortedSet<int>();
            foreach (var seed in _roomSeeds.Values)
                if (seed.Candidates != null)
                    foreach (var c in seed.Candidates) known.Add(c);
            foreach (var loc in _explicitLocations.Values) known.Add(loc);
            KnownLocations = known.ToList();

            ResetRoomOrderCommand = new RelayCommand(ResetRoomOrder);
            StartReorderCommand = new RelayCommand(StartReorder);
            ApplyReorderCommand = new RelayCommand(ApplyReorder);
            CancelReorderCommand = new RelayCommand(CancelReorder);

            // Room order is read from ExtensibleStorage at collection time and passed in —
            // a Core ctor cannot read Revit synchronously. Seed from the persisted order,
            // then reconcile against the live room enumeration (prune gone, add new).
            for (int i = 0; i < savedRoomOrder.Count; i++)
            {
                var item = CreateItem(savedRoomOrder[i].Name, i + 1);
                item.ClickOrder = savedRoomOrder[i].ClickOrder;
                RoomOrder.Add(item);
            }

            if (RoomOrder.Count == 0)
                BuildRoomOrder();
            else
                ReconcileWithLiveRooms();
            RefreshPositions();

            CollectionViewSource.GetDefaultView(RoomOrder).Filter = RoomMatchesSearch;
        }

        /// <summary>Builds a row and seeds its location state (auto-seed + explicit pick + keypad
        /// flag) from the injected maps, then wires its location edits to the persistence save.</summary>
        private RoomOrderItem CreateItem(string name, int position)
        {
            var item = new RoomOrderItem(name, position);
            var seed = _roomSeeds.TryGetValue(name, out var s) ? s : _emptySeed;
            int pick = _explicitLocations.TryGetValue(name, out int p) ? p : 0;
            item.InitLocation(seed, _keypadRooms.Contains(name), pick);
            item.PropertyChanged += OnItemPropertyChanged;
            return item;
        }

        private void OnItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Only an explicit location edit is persisted; order/click/reorder saves run elsewhere.
            if (e.PropertyName == nameof(RoomOrderItem.ExplicitLocation))
                SaveRoomLocations();
        }

        private bool RoomMatchesSearch(object obj)
        {
            if (string.IsNullOrEmpty(_searchText)) return true;
            return obj is RoomOrderItem item &&
                   item.Name.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void MoveRoom(int fromIndex, int toIndex)
        {
            if (fromIndex == toIndex) return;
            RoomOrder.Move(fromIndex, toIndex);

            // If dragged room lands next to a clicked room, adopt a click order
            var dragged = RoomOrder[toIndex];
            if (!dragged.IsClicked)
            {
                bool neighborClicked =
                    (toIndex > 0 && RoomOrder[toIndex - 1].IsClicked) ||
                    (toIndex < RoomOrder.Count - 1 && RoomOrder[toIndex + 1].IsClicked);
                if (neighborClicked)
                    dragged.ClickOrder = 1; // placeholder, renumbered below
            }

            // Renumber all clicked rooms by list position
            int order = 1;
            foreach (var item in RoomOrder)
            {
                if (item.IsClicked)
                    item.ClickOrder = order++;
            }

            RefreshPositions();
            SaveRoomOrder();
            OrderChanged?.Invoke();
        }

        private void ResetRoomOrder()
        {
            var result = System.Windows.MessageBox.Show(
                "Reset room order to alphabetical?",
                "TurboNumber",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.OK) return;

            BuildRoomOrder();
            SaveRoomOrder();
            OrderChanged?.Invoke();
        }

        private void StartReorder()
        {
            SearchText = string.Empty;
            _reorderSnapshot = RoomOrder.ToDictionary(r => r.Name, r => r.ClickOrder);
            _nextClickOrder = RoomOrder.Count > 0 ? RoomOrder.Max(r => r.ClickOrder) : 0;
            foreach (var item in RoomOrder)
                item.IsReordering = true;
            IsReordering = true;
        }

        public void ToggleRoomClick(RoomOrderItem item)
        {
            if (!IsReordering) return;

            if (item.IsClicked)
            {
                int removed = item.ClickOrder;
                item.ClickOrder = 0;
                foreach (var r in RoomOrder.Where(r => r.ClickOrder > removed))
                    r.ClickOrder--;
                _nextClickOrder--;
            }
            else
            {
                _nextClickOrder++;
                item.ClickOrder = _nextClickOrder;
            }
        }

        private void ApplyReorder()
        {
            var clicked = RoomOrder
                .Where(r => r.IsClicked)
                .OrderBy(r => r.ClickOrder)
                .ToList();
            var unclicked = RoomOrder
                .Where(r => !r.IsClicked)
                .OrderBy(r => r.Name)
                .ToList();

            RoomOrder.Clear();
            int pos = 1;
            foreach (var item in clicked.Concat(unclicked))
            {
                item.IsReordering = false;
                item.Position = pos++;
                RoomOrder.Add(item);
            }

            _reorderSnapshot = null;
            IsReordering = false;
            SearchText = string.Empty;
            SaveRoomOrder();
            OrderChanged?.Invoke();
        }

        private void CancelReorder()
        {
            SearchText = string.Empty;
            if (_reorderSnapshot != null)
            {
                foreach (var item in RoomOrder)
                {
                    item.ClickOrder = _reorderSnapshot.TryGetValue(item.Name, out int order) ? order : 0;
                    item.IsReordering = false;
                }
                _reorderSnapshot = null;
            }
            else
            {
                foreach (var item in RoomOrder)
                    item.IsReordering = false;
            }
            IsReordering = false;
        }

        private void RefreshPositions()
        {
            for (int i = 0; i < RoomOrder.Count; i++)
                RoomOrder[i].Position = i + 1;
        }

        private void BuildRoomOrder()
        {
            RoomOrder.Clear();
            var names = _allRoomNames
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();
            for (int i = 0; i < names.Count; i++)
                RoomOrder.Add(CreateItem(names[i], i + 1));
        }

        /// <summary>
        /// Two-way reconcile of the persisted order against the live room enumeration
        /// (<see cref="_allRoomNames"/>): first prune saved rooms that no longer exist,
        /// then append rooms that have appeared since (alphabetical). The prune is what
        /// reaps a stale name after its Space/region is renamed — e.g. SHOWER →
        /// GRAND SHOWER — which the old add-only merge left lingering forever. Runs once
        /// at window open (live names are captured at construction), so a mid-session
        /// rename reflects on next open, matching the add cadence. In-memory only: the
        /// pruned tombstone is dropped from ExtensibleStorage the next time any reorder
        /// commits, and never re-displays meanwhile.
        /// </summary>
        private void ReconcileWithLiveRooms()
        {
            var live = new HashSet<string>(
                _allRoomNames.Where(n => !string.IsNullOrEmpty(n)),
                StringComparer.OrdinalIgnoreCase);

            // Prune: drop any saved room absent from the current enumeration.
            for (int i = RoomOrder.Count - 1; i >= 0; i--)
            {
                if (!live.Contains(RoomOrder[i].Name))
                {
                    RoomOrder[i].PropertyChanged -= OnItemPropertyChanged;
                    RoomOrder.RemoveAt(i);
                }
            }

            // Add: fold in rooms that have appeared since (append-alphabetical).
            var existing = new HashSet<string>(RoomOrder.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            var newNames = _allRoomNames
                .Where(n => !string.IsNullOrEmpty(n) && !existing.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();
            foreach (var n in newNames)
                RoomOrder.Add(CreateItem(n, RoomOrder.Count + 1));
        }

        private void SaveRoomOrder()
        {
            var snapshot = RoomOrder.Select(r => (r.Name, r.ClickOrder)).ToList();
            _workQueue.Enqueue(() => { _roomOrderStore.SaveRoomOrder(snapshot); return null; }, null);
        }

        /// <summary>Persists the sparse set of explicit location picks (rows with a positive
        /// pick); auto-seeds are never written. Queued on the Revit API thread like the order save.</summary>
        private void SaveRoomLocations()
        {
            var snapshot = RoomOrder
                .Where(r => r.ExplicitLocation > 0)
                .Select(r => (r.Name, r.ExplicitLocation))
                .ToList();
            _workQueue.Enqueue(() =>
            {
                _roomOrderStore.SaveRoomLocations(snapshot);
                return null;
            }, null);
        }
    }
}
