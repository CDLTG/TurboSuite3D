#nullable enable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx.Lock;

namespace TurboSuite.Dmx.OneLine
{
    /// <summary>
    /// Lays a solved <see cref="DmxBill"/> + its reconciled <see cref="DmxNumbering"/> out into one
    /// <see cref="DmxOneLineDrawing"/> per loop (BuildPlan Phase 4), in the SAME DEC-walk order as the
    /// placement planner so the diagram's DEC#s match the placed families and the lock baseline (§8c).
    /// Pure geometry off <see cref="DmxOneLineGeometry"/> — no Revit. Each loop: interface + processor at
    /// top, a vertical column of <c>[driver][decoder][homerun]</c> rows grouped into "120V FEED" blocks
    /// (from <see cref="InterfaceSolution.Feeds"/>, so the blocks == the §0c breaker count), the DMX daisy
    /// chain running top→bottom to a terminator, and the wire-type markers off the legend. Single homerun
    /// leg per decoder (185 style) + one "REFER TO PLAN" header; tape type is not drawn (§8a).
    /// </summary>
    public static class DmxOneLinePlanner
    {
        /// <param name="driverTypeMarkByName">Driver engine Name ("Family : Type") → its Type Mark
        /// ("CV"/"MD"/"ME"), so the driver box shows the Type Mark, not the family name. When the map lacks a
        /// driver (or is null — the unit tests), the box falls back to the engine Name.</param>
        public static IReadOnlyList<DmxOneLineDrawing> Build(DmxBill bill, DmxNumbering numbering,
                                                             IReadOnlyDictionary<string, string>? driverTypeMarkByName = null)
        {
            var byZone = bill.Zones.ToDictionary(z => z.ZoneName);
            return bill.Interfaces.Select(iface => BuildLoop(iface, byZone, numbering, driverTypeMarkByName)).ToList();
        }

        private readonly struct Row
        {
            public Row(int dec, int address, string driverMark, int channels)
            {
                Dec = dec; Address = address; DriverMark = driverMark; Channels = channels;
            }
            public int Dec { get; }
            public int Address { get; }
            public string DriverMark { get; }
            public int Channels { get; }
        }

        private static DmxOneLineDrawing BuildLoop(InterfaceSolution iface,
                                                   IReadOnlyDictionary<string, ZoneSolution> byZone,
                                                   DmxNumbering numbering,
                                                   IReadOnlyDictionary<string, string>? driverTypeMarkByName)
        {
            // 1. Flatten this interface's decoders in DEC-walk order (zones → clusters → decoders), pairing
            //    each with its DEC # (numbering, lock-aware), zone address, driver type mark, channel count.
            var rows = new List<Row>();
            foreach (var addressed in iface.Interface.Zones)
            {
                if (!byZone.TryGetValue(addressed.ZoneName, out var sol)) continue;
                numbering.DecIdsByZone.TryGetValue(addressed.ZoneName, out var decIds);
                int address = ZoneAddress(addressed);

                int idx = 0;
                foreach (var cluster in sol.Clusters)
                foreach (var pd in cluster.Power.Decoders)
                {
                    int dec = decIds != null && idx < decIds.Count ? decIds[idx] : 0;
                    idx++;
                    string driverMark = driverTypeMarkByName != null
                                        && driverTypeMarkByName.TryGetValue(pd.Driver.Name, out var tm)
                        ? tm : pd.Driver.Name;
                    rows.Add(new Row(dec, address, driverMark, sol.Channels));
                }
            }

            // 2. Feed-block sizes from the interface's §0c feeds — one "120V FEED" per breaker, in DEC order.
            var feedSizes = iface.Feeds.Select(f => f.DriverCount).ToList();

            return Compose(iface.Interface.InterfaceNumber, iface.Interface.LoopName, rows, feedSizes);
        }

        private static DmxOneLineDrawing Compose(int interfaceNumber, string? loopName,
                                                 IReadOnlyList<Row> rows, IReadOnlyList<int> feedSizes)
        {
            var symbols = new List<DmxSymbolInstance>();
            var wires = new List<DmxWireSegment>();
            var markers = new List<DmxMarker>();
            var notes = new List<DmxNote>();

            void Wire(XY a, XY b, bool dashed, DmxWireType? mark)
            {
                wires.Add(new DmxWireSegment(a, b, dashed));
                if (mark.HasValue) markers.Add(new DmxMarker(new XY((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0), mark.Value));
            }

            // ── Coordinate frame ───────────────────────────────────────────────────────────────────────
            double drvX = DmxOneLineGeometry.Layout.DriverCenterX;
            double decX = DmxOneLineGeometry.Layout.DecoderCenterX;
            double chainX = decX + DmxOneLineGeometry.Decoder.DmxIn.X;   // the DMX column; interface/terminator center on it
            double ifaceY = 0.0;
            var ifaceCenter = new XY(chainX, ifaceY);

            const double commGap = 1.0;   // ft, interface→processor comm leader
            double procX = chainX + DmxOneLineGeometry.Interface.CommIn.X + commGap - DmxOneLineGeometry.Processor.Comm.X;
            var procCenter = new XY(procX, ifaceY);

            // Row Y positions + feed-block membership (a feed gets an extra gap before the next).
            double firstRowY = ifaceY - DmxOneLineGeometry.Layout.InterfaceDrop;
            var rowY = new double[rows.Count];
            var feedFirst = new bool[rows.Count];
            var feedLast = new bool[rows.Count];
            {
                double y = firstRowY;
                int r = 0;
                foreach (int size in feedSizes)
                {
                    for (int k = 0; k < size && r < rows.Count; k++, r++)
                    {
                        if (k == 0) feedFirst[r] = true;
                        if (k == size - 1) feedLast[r] = true;
                        rowY[r] = y;
                        y -= DmxOneLineGeometry.Layout.RowPitch;
                    }
                    y -= DmxOneLineGeometry.Layout.FeedGroupGap;
                }
                while (r < rows.Count) { rowY[r] = y; y -= DmxOneLineGeometry.Layout.RowPitch; r++; }   // safety
            }
            double lastRowY = rows.Count > 0 ? rowY[rows.Count - 1] : firstRowY;
            var termCenter = new XY(chainX, lastRowY - DmxOneLineGeometry.Layout.TerminatorDrop);

            // ── Top boxes: interface (+ # param) and processor, joined by the ⑦ comm leader ──────────────
            symbols.Add(new DmxSymbolInstance(DmxSymbolKind.Interface,
                DmxOneLineGeometry.Interface.Family, DmxOneLineGeometry.Interface.Type, ifaceCenter,
                new Dictionary<string, string> { [DmxOneLineGeometry.Interface.NumberParam] = interfaceNumber.ToString() }));
            symbols.Add(new DmxSymbolInstance(DmxSymbolKind.Processor,
                DmxOneLineGeometry.Processor.Family, DmxOneLineGeometry.Processor.Type, procCenter,
                new Dictionary<string, string>()));
            Wire(ifaceCenter.Plus(DmxOneLineGeometry.Interface.CommIn),
                 procCenter.Plus(DmxOneLineGeometry.Processor.Comm), dashed: true, DmxWireType.Comm);

            // One column header above the homerun legs (185 style); sits below the processor row so it can't
            // collide with the interface/processor/⑦. Tape type is NOT drawn (§8a).
            double headerY = firstRowY + DmxOneLineGeometry.Layout.RowPitch * 0.75;
            notes.Add(new DmxNote(new XY(decX + DmxOneLineGeometry.Decoder.HomerunOut.X, headerY),
                "REFER TO PLAN FOR NUMBER OF HOMERUNS PER DECODER", DmxTextAlign.Left));

            // ── Rows: [driver][decoder] + the per-row power and homerun legs ─────────────────────────────
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                var decCenter = new XY(decX, rowY[r]);
                var drvCenter = new XY(drvX, rowY[r]);

                symbols.Add(new DmxSymbolInstance(DmxSymbolKind.Driver,
                    DmxOneLineGeometry.Driver.Family, DmxOneLineGeometry.Driver.Type, drvCenter,
                    new Dictionary<string, string> { [DmxOneLineGeometry.Driver.TypeMarkParam] = row.DriverMark }));
                symbols.Add(new DmxSymbolInstance(DmxSymbolKind.Decoder,
                    DmxOneLineGeometry.Decoder.Family, DmxOneLineGeometry.Decoder.Type, decCenter,
                    new Dictionary<string, string>
                    {
                        [DmxOneLineGeometry.Decoder.DecNumberParam] = $"DEC {row.Dec}",
                        [DmxOneLineGeometry.Decoder.AddressParam] = row.Address.ToString("D3"),
                    }));

                // driver → decoder (24 V, ③)
                Wire(drvCenter.Plus(DmxOneLineGeometry.Driver.PowerOut),
                     decCenter.Plus(DmxOneLineGeometry.Decoder.PowerIn), dashed: false, DmxWireType.Lv2);

                // decoder → tape: single leg + gauge marker (no per-leg text, §8a)
                var hrStart = decCenter.Plus(DmxOneLineGeometry.Decoder.HomerunOut);
                Wire(hrStart, hrStart.Offset(DmxOneLineGeometry.Layout.HomerunLegLength, 0),
                     dashed: false, DmxWireLegend.HomerunFor(row.Channels));

                // first driver of a feed: the 120V FEED stub (①) + label
                if (feedFirst[r])
                {
                    var pin = drvCenter.Plus(DmxOneLineGeometry.Driver.PowerIn);
                    var stubStart = pin.Offset(-DmxOneLineGeometry.Layout.FeedStubLength, 0);
                    Wire(stubStart, pin, dashed: false, DmxWireType.Hv);
                    notes.Add(new DmxNote(stubStart, "120V FEED", DmxTextAlign.Right));
                }

                // driver daisy within the feed (①): this driver down to the next (unless it ends the feed)
                if (!feedLast[r] && r + 1 < rows.Count)
                {
                    Wire(drvCenter.Plus(DmxOneLineGeometry.Driver.DaisyDown),
                         new XY(drvX, rowY[r + 1]).Plus(DmxOneLineGeometry.Driver.DaisyUp),
                         dashed: false, DmxWireType.Hv);
                }
            }

            // ── DMX daisy chain (CAT6, ⑥): interface → decoder0 → … → decoderN → terminator ─────────────
            if (rows.Count > 0)
            {
                Wire(ifaceCenter.Plus(DmxOneLineGeometry.Interface.ChainOut),
                     new XY(decX, rowY[0]).Plus(DmxOneLineGeometry.Decoder.DmxIn), dashed: true, DmxWireType.Cat6);
                for (int r = 0; r + 1 < rows.Count; r++)
                    Wire(new XY(decX, rowY[r]).Plus(DmxOneLineGeometry.Decoder.DmxOut),
                         new XY(decX, rowY[r + 1]).Plus(DmxOneLineGeometry.Decoder.DmxIn), dashed: true, DmxWireType.Cat6);
                Wire(new XY(decX, rowY[rows.Count - 1]).Plus(DmxOneLineGeometry.Decoder.DmxOut),
                     termCenter.Plus(DmxOneLineGeometry.Terminator.DmxIn), dashed: true, DmxWireType.Cat6);

                symbols.Add(new DmxSymbolInstance(DmxSymbolKind.Terminator,
                    DmxOneLineGeometry.Terminator.Family, DmxOneLineGeometry.Terminator.Type, termCenter,
                    new Dictionary<string, string>()));
            }

            return new DmxOneLineDrawing(interfaceNumber, loopName, symbols, wires, markers, notes);
        }

        /// <summary>The decoder address shown on the box = the zone's address-block start (its lowest subzone).</summary>
        private static int ZoneAddress(AddressedZone zone)
            => zone.SubZones.Count > 0 ? zone.SubZones.Min(s => s.StartAddress) : 0;
    }
}
