#nullable disable
using System;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// One control link off a processor — two per HomeWorks QSX processor, each either a wired QS
    /// link or a Clear Connect Type A (wireless) link.
    ///
    /// <b>A link has three budgets, and they are not the same kind of thing.</b> Devices is the count
    /// of things on the link — 99 either way, so a wireless keypad consumes a Clear Connect link's
    /// budget exactly as a wired one consumes a QS link's. Switch legs are the controllable outputs
    /// and both types have them, just very differently: 512 on QS, <b>100</b> on Clear Connect.
    /// Repeaters (4) caps one <i>kind</i> of device rather than devices at all, so it rides alongside
    /// the device count instead of replacing it.
    ///
    /// Conflating those last two — reading "four repeaters per link" as "a Clear Connect link holds
    /// four devices" — makes every wireless device past the fourth look like an overflow.
    ///
    /// Capacities are quoted from Lutron 3691127f (HomeWorks QSX Processor, rev. 08.06.25) p.2.
    /// <b>Not modelled, and wrong if it ever appears:</b> the MDU processors (HQP7-MDU-1/-2) run
    /// 50 devices / 100 switch legs on their wired link, so every number here is generous for them.
    /// </summary>
    public class ProcessorLink : ViewModelBase
    {
        /// <summary>Devices per link, either type. Modules, keypads, and interfaces alike — and on a
        /// Clear Connect link, the wireless devices and the repeaters themselves.</summary>
        public const int MaxDevices = 99;

        /// <summary>
        /// QS link switch-leg cap.
        ///
        /// A switch leg is the smallest controllable output. Lutron 3691127f p.2 enumerates them:
        /// <i>"dimmed or switched circuits, HomeWorks Digital or DALI addressable devices (ballasts,
        /// drivers, and interfaces), a single DMX channel, contact closure outputs, and Sivoia QS
        /// shade drives."</i>
        ///
        /// Two consequences worth carrying forward. A QSE-IO's <b>contact closure outputs are legs</b>,
        /// and we count it as one device and no legs — the outputs are not modelled, so that figure is
        /// knowably low. And a <b>DALI addressable device is a leg</b>, so a DALI loop will pressure
        /// this cap directly when Phase 3 lands.
        /// </summary>
        public const int MaxLoads = 512;

        /// <summary>
        /// Clear Connect Type A switch-leg cap — a fifth of the wired link's (3691127f p.2).
        ///
        /// Wireless <i>keypads</i> contribute none of these: a keypad is a control, not an output. The
        /// bar fills from wireless dimmers, shades and Sivoia drives, none of which TurboZones collects
        /// yet — so it reads zero on every current job while being the correct number the moment one
        /// of them is modelled.
        /// </summary>
        public const int MaxClearConnectLoads = 100;

        /// <summary>
        /// Hybrid Repeaters per Clear Connect Type A link.
        ///
        /// Lutron 369-351b (HomeWorks QS Hybrid Repeaters, rev. 02.10.12), p.1: <i>"Up to four (4)
        /// total Hybrid Repeaters can be used per link to extend the RF range for larger system
        /// applications."</i> Corroborated by the unit's own Repeater Status LEDs, which read
        /// <c>P · 1 · 2 · 3 · 4</c> — the processor plus four repeaters.
        ///
        /// This was <see cref="MaxDevices"/> (99) until the spec was checked, which meant any job up
        /// to 99 repeaters reported a single CC-A link. Since a CC-A link is taken out of a
        /// processor's pair of two, five repeaters is enough to consume a whole processor — the
        /// "wireless alone forces a second processor" case, which used to be reported as free.
        ///
        /// <b>This caps repeaters, not devices.</b> The link still holds <see cref="MaxDevices"/>
        /// devices; the wireless keypads riding it are bounded by that, not by this.
        ///
        /// The sheet is HomeWorks QS era; if a job is on Athena/QSX, re-check before trusting it.
        /// </summary>
        public const int MaxRepeatersPerClearConnectLink = 4;

        public const string QsLinkType = "QS";
        public const string ClearConnectLinkType = "Clear Connect Type A";

        private int _usedDevices;
        private int _usedLoads;
        private int _usedRepeaters;
        private string _linkType = QsLinkType;

        public string ProcessorPanelName { get; set; }
        public int LinkNumber { get; set; }

        public string LinkType
        {
            get => _linkType;
            set
            {
                if (SetProperty(ref _linkType, value))
                {
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(IsClearConnect));
                    OnPropertyChanged(nameof(ShowRepeaterBar));
                    OnPropertyChanged(nameof(LoadCapacity));
                    OnPropertyChanged(nameof(LoadPercent));
                    OnPropertyChanged(nameof(IsOverLoadCapacity));
                }
            }
        }

        public bool IsClearConnect => string.Equals(_linkType, ClearConnectLinkType, StringComparison.OrdinalIgnoreCase);

        public string DisplayName => $"Link {LinkNumber} ({_linkType})";

        /// <summary>Devices this link can hold. The same 99 either way — a Clear Connect link is not a
        /// four-device link, it is a 99-device link that happens to cap one kind of device at four.</summary>
        public int DeviceCapacity => MaxDevices;

        /// <summary>Switch legs this link can carry — both types have them, at very different scales.</summary>
        public int LoadCapacity => IsClearConnect ? MaxClearConnectLoads : MaxLoads;

        /// <summary>Repeaters this link can hold. Only meaningful on Clear Connect.</summary>
        public int RepeaterCapacity => MaxRepeatersPerClearConnectLink;

        /// <summary>Only a Clear Connect link carries repeaters, so only it shows the repeater bar —
        /// which is why Clear Connect shows three bars where QS shows two.</summary>
        public bool ShowRepeaterBar => IsClearConnect;

        public int UsedDevices
        {
            get => _usedDevices;
            set
            {
                if (SetProperty(ref _usedDevices, value))
                {
                    OnPropertyChanged(nameof(DevicePercent));
                    OnPropertyChanged(nameof(IsOverDeviceCapacity));
                }
            }
        }

        public int UsedLoads
        {
            get => _usedLoads;
            set
            {
                if (SetProperty(ref _usedLoads, value))
                {
                    OnPropertyChanged(nameof(LoadPercent));
                    OnPropertyChanged(nameof(IsOverLoadCapacity));
                }
            }
        }

        /// <summary>Hybrid Repeaters on this link. A subset of <see cref="UsedDevices"/>, not a
        /// separate population — a repeater is a device on the link like anything else.</summary>
        public int UsedRepeaters
        {
            get => _usedRepeaters;
            set
            {
                if (SetProperty(ref _usedRepeaters, value))
                {
                    OnPropertyChanged(nameof(RepeaterPercent));
                    OnPropertyChanged(nameof(IsOverRepeaterCapacity));
                }
            }
        }

        public double DevicePercent => DeviceCapacity > 0 ? (double)_usedDevices / DeviceCapacity : 0;
        public double LoadPercent => LoadCapacity > 0 ? (double)_usedLoads / LoadCapacity : 0;
        public double RepeaterPercent => RepeaterCapacity > 0 ? (double)_usedRepeaters / RepeaterCapacity : 0;
        public bool IsOverDeviceCapacity => _usedDevices > DeviceCapacity;
        public bool IsOverLoadCapacity => _usedLoads > LoadCapacity;
        public bool IsOverRepeaterCapacity => _usedRepeaters > RepeaterCapacity;
    }
}
