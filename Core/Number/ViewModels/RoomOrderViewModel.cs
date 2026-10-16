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
        // A room's location drives where its keypads land on the control one-line, resolved by a
        // ladder (see RoomLocationSeeder.Resolve): a UNANIMOUS auto-seed derived from the room's own
        // circuits/DALI is authoritative — to move it, move the panel/zone assignments — and the row
        // is LOCKED (shown greyed, read-only, with a lock button where the combo's chevron would be).
        // For the seed-less states (Mixed / None) a hand-entered pick is the backstop. The one escape
        // on a unanimous row is a deliberate OVERRIDE: click the lock to edit, type a different
        // location, and it wins over the seed (the rare mis-detected-room fix). Only picks/overrides
        // persist; seeds re-derive each session and inject at construction.

        private RoomLocationSeed _seed = RoomLocationSeeder.Classify(Array.Empty<int>());

        /// <summary>The derived (auto-seed) location classification for this room — unanimous,
        /// mixed, or none. Set once at construction; not persisted.</summary>
        public RoomLocationSeed Seed => _seed;

        /// <summary>Whether a keypad resolves to this room. The only row-state the sidebar actively
        /// prompts is a keypad-bearing room with no resolved location.</summary>
        public bool HasKeypad { get; private set; }

        private int _explicitLocation;
        private bool _isOverride;   // persisted: pick deliberately overriding a unanimous seed
        private bool _unlocked;     // transient (session): lock clicked, combo editable, not yet changed

        private bool IsUnanimous => _seed.Kind == RoomLocationSeedKind.Unanimous;

        /// <summary>The designer's explicit location pick (0 = none). Read-only here — all user edits
        /// run through <see cref="LocationEntry"/>/<see cref="UnlockCommand"/>; the VM reads it (with
        /// <see cref="IsOverride"/>) to build the persisted snapshot.</summary>
        public int ExplicitLocation => _explicitLocation;

        /// <summary>Whether <see cref="ExplicitLocation"/> is an acknowledged override of a unanimous
        /// seed (persisted as the <c>|O</c> flag). Distinguishes a deliberate override from an inert
        /// stale pick left on a since-gone-unanimous room.</summary>
        public bool IsOverride => _isOverride;

        /// <summary>The effective location after the resolution ladder (0 = blank).</summary>
        public int ResolvedLocation => RoomLocationSeeder.Resolve(_explicitLocation, _isOverride, _seed);

        /// <summary>True when an acknowledged override is actually in effect — a unanimous row routed,
        /// deliberately, against its own seed. Shown amber with a ⚠ marker.</summary>
        public bool IsOverridden => IsUnanimous && _isOverride && _explicitLocation > 0;

        /// <summary>True when the row is a unanimous seed shown read-only (not yet unlocked, not
        /// overridden). Drives the locked view: greyed value + a lock button in the chevron's place.
        /// Clicking the lock (<see cref="UnlockCommand"/>) flips <see cref="CanEditLocation"/> true.</summary>
        public bool IsLocked => IsUnanimous && !_unlocked && !IsOverridden;

        /// <summary>True when the editable combo (text + chevron + dropdown) is shown instead of the
        /// locked view — every seed-less row, plus an unlocked or overridden unanimous row.</summary>
        public bool CanEditLocation => !IsLocked;

        /// <summary>True when a plain explicit pick is in effect on a seed-less row (shown normal, not
        /// greyed). Not an override — Mixed/None have no seed to override.</summary>
        public bool IsExplicit => !IsUnanimous && _explicitLocation > 0;

        /// <summary>True when a unanimous seed is the value in effect (greyed) — whether locked or
        /// unlocked-but-unchanged. An override flips this off (it reads as an explicit amber value).</summary>
        public bool IsSeeded => IsUnanimous && !IsOverridden;

        /// <summary>True when the signals disagree and no pick resolves it (red "!" mixed-flag hint).</summary>
        public bool IsMixed => _seed.Kind == RoomLocationSeedKind.Mixed && _explicitLocation == 0;

        /// <summary>The one actively-prompted state: a keypad-bearing room with nowhere to route.</summary>
        public bool NeedsAssign => HasKeypad && ResolvedLocation == 0;

        /// <summary>The badge text — the resolved location, or empty when blank.</summary>
        public string LocationDisplay => ResolvedLocation > 0 ? ResolvedLocation.ToString() : string.Empty;

        /// <summary>Two-way text for the location combo. Shows the resolved value; committing a value
        /// routes through <see cref="ApplyUserLocation"/> — on a unanimous row a value differing from
        /// the seed becomes an acknowledged override (clearing it, or re-typing the seed value, drops
        /// the override and re-locks), on a seed-less row it is an ordinary pick.</summary>
        public string LocationEntry
        {
            get => ResolvedLocation > 0 ? ResolvedLocation.ToString() : string.Empty;
            set
            {
                int loc = (!string.IsNullOrWhiteSpace(value) &&
                           int.TryParse(value.Trim(), out int v) && v > 0) ? v : 0;
                ApplyUserLocation(loc);
            }
        }

        /// <summary>The mixed-flag histogram hint, e.g. "1/4" (the disagreeing candidates).</summary>
        public string MixedHint =>
            IsMixed && _seed.Candidates != null ? string.Join("/", _seed.Candidates) : string.Empty;

        /// <summary>The override tooltip spelling out the disagreement, e.g. routing to 3 while the
        /// room's circuits say 1 — empty unless an override is in effect.</summary>
        public string OverrideHint => IsOverridden
            ? $"Override — keypad routes to Location {_explicitLocation}, though this room's circuits say " +
              $"Location {_seed.Location}. Fix the model's space detection if that's wrong."
            : string.Empty;

        /// <summary>Unlocks a locked (unanimous) row for a deliberate override: shows the editable
        /// combo so the designer can type a different location. No-op on a seed-less row (already
        /// editable). Transient — a row left unchanged re-locks on next open.</summary>
        public ICommand UnlockCommand { get; }

        public RoomOrderItem(string name, int position)
        {
            Name = name;
            _position = position;
            UnlockCommand = new RelayCommand(Unlock, () => IsLocked);
        }

        private void Unlock()
        {
            if (!IsLocked) return;
            _unlocked = true;
            RaiseLocationDerived();
        }

        /// <summary>Applies a user-committed location, deciding pick vs. override vs. re-lock. Raises
        /// <see cref="ExplicitLocation"/> (the VM's save trigger) only when the persisted state changes.</summary>
        private void ApplyUserLocation(int loc)
        {
            int newPick;
            bool newOverride;

            if (IsUnanimous)
            {
                if (loc > 0 && loc != _seed.Location)
                {
                    // A value differing from the derived seed → a deliberate, acknowledged override.
                    newPick = loc;
                    newOverride = true;
                    _unlocked = true;
                }
                else
                {
                    // Seed value re-entered or cleared → drop any override and re-lock.
                    newPick = 0;
                    newOverride = false;
                    _unlocked = false;
                }
            }
            else
            {
                // Seed-less row (Mixed / None): an ordinary gap-fill pick, never an override.
                newPick = loc;
                newOverride = false;
            }

            bool changed = newPick != _explicitLocation || newOverride != _isOverride;
            _explicitLocation = newPick;
            _isOverride = newOverride;
            RaiseLocationDerived();
            if (changed) OnPropertyChanged(nameof(ExplicitLocation)); // VM save trigger
        }

        /// <summary>Seeds the location state at construction (no save; the edit path is for user edits
        /// only). Called by the VM factory after it resolves the room's seed, keypad flag, and any
        /// persisted pick + override flag.</summary>
        internal void InitLocation(RoomLocationSeed seed, bool hasKeypad, int explicitLocation, bool isOverride)
        {
            _seed = seed;
            HasKeypad = hasKeypad;
            _explicitLocation = explicitLocation < 0 ? 0 : explicitLocation;
            _isOverride = isOverride && _explicitLocation > 0;
            RaiseLocationDerived();
        }

        private void RaiseLocationDerived()
        {
            OnPropertyChanged(nameof(ResolvedLocation));
            OnPropertyChanged(nameof(IsOverridden));
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(CanEditLocation));
            OnPropertyChanged(nameof(IsExplicit));
            OnPropertyChanged(nameof(IsSeeded));
            OnPropertyChanged(nameof(IsMixed));
            OnPropertyChanged(nameof(NeedsAssign));
            OnPropertyChanged(nameof(LocationDisplay));
            OnPropertyChanged(nameof(LocationEntry));
            OnPropertyChanged(nameof(MixedHint));
            OnPropertyChanged(nameof(OverrideHint));
            CommandManager.InvalidateRequerySuggested(); // re-query UnlockCommand.CanExecute
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
        private readonly HashSet<string> _overrideRooms;
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
                {
                    OnPropertyChanged(nameof(HasSearchText));
                    CollectionViewSource.GetDefaultView(RoomOrder).Refresh();
                }
            }
        }

        /// <summary>Whether the filter box holds any text — gates the inline clear ("✕") button.</summary>
        public bool HasSearchText => !string.IsNullOrEmpty(_searchText);

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
            IReadOnlyList<(string Name, int Location, bool IsOverride)> savedRoomLocations,
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
            _overrideRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (savedRoomLocations != null)
                foreach (var (name, loc, isOverride) in savedRoomLocations)
                {
                    if (string.IsNullOrEmpty(name) || loc <= 0) continue;
                    _explicitLocations[name] = loc;
                    if (isOverride) _overrideRooms.Add(name);
                }

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
            item.InitLocation(seed, _keypadRooms.Contains(name), pick, _overrideRooms.Contains(name));
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

        /// <summary>Persists the sparse set of explicit location picks (rows with a positive pick),
        /// each carrying its acknowledged-override flag; auto-seeds are never written. Queued on the
        /// Revit API thread like the order save.</summary>
        private void SaveRoomLocations()
        {
            var snapshot = RoomOrder
                .Where(r => r.ExplicitLocation > 0)
                .Select(r => (r.Name, r.ExplicitLocation, r.IsOverride))
                .ToList();
            _workQueue.Enqueue(() =>
            {
                _roomOrderStore.SaveRoomLocations(snapshot);
                return null;
            }, null);
        }
    }
}
