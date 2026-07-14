#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TurboSuite.Name.Regions
{
    /// <summary>
    /// Dev-aid renderer for the watershed grid — a self-contained 24-bit RGB PNG encoder (no System.Drawing).
    /// Colors each cell by its <see cref="RegionWatershedEngine"/> barrier/owner value and overlays hollow box
    /// markers (proximity bridges). The Shim adapter picks the output path (Desktop) and passes the grid +
    /// markers the engine returned; the algorithm grid is never modified here. <b>Dev aid only</b> — kept
    /// because it's used heavily while iterating on the partition; drop or gate before a clean ship.
    /// </summary>
    public static class RegionDebugImage
    {
        /// <summary>Writes the grid to <paramref name="path"/> as a PNG. Top-down rows; grid row h-1 (max Y)
        /// is drawn on top. <paramref name="markers"/> are grid-space hollow boxes (debug-viz only).</summary>
        public static void ExportPng(int[] grid, int w, int h, string path,
            List<(int X, int Y, byte R, byte G, byte B)> markers = null)
        {
            int stride = 1 + w * 3;
            // Raw scanlines: each prefixed with filter byte 0 (none), then RGB triples.
            var raw = new byte[h * stride];
            int p = 0;
            for (int r = 0; r < h; r++)
            {
                raw[p++] = 0; // filter: none
                int gy = h - 1 - r;
                for (int x = 0; x < w; x++)
                {
                    (byte rr, byte gg, byte bb) = Color(grid[gy * w + x]);
                    raw[p++] = rr; raw[p++] = gg; raw[p++] = bb;
                }
            }

            // Overlay hollow box markers (grid coords → flipped output rows). Debug-viz only; the algorithm grid
            // is untouched, so markers never act as barriers.
            if (markers != null)
            {
                const int rad = 5; // box half-size in px
                void Put(int gx, int gy, byte cr, byte cg, byte cb)
                {
                    if (gx < 0 || gx >= w || gy < 0 || gy >= h) return;
                    int row = h - 1 - gy;               // grid Y is bottom-up; PNG rows are top-down
                    int off = row * stride + 1 + gx * 3;
                    raw[off] = cr; raw[off + 1] = cg; raw[off + 2] = cb;
                }
                foreach (var (mx, my, cr, cg, cb) in markers)
                    for (int d = -rad; d <= rad; d++)
                    {
                        Put(mx + d, my - rad, cr, cg, cb); Put(mx + d, my + rad, cr, cg, cb); // top/bottom edges
                        Put(mx - rad, my + d, cr, cg, cb); Put(mx + rad, my + d, cr, cg, cb); // left/right edges
                    }
            }

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }); // PNG signature

            var ihdr = new List<byte>();
            void Be(List<byte> d, int v)
            { d.Add((byte)(v >> 24)); d.Add((byte)(v >> 16)); d.Add((byte)(v >> 8)); d.Add((byte)v); }
            Be(ihdr, w); Be(ihdr, h);
            ihdr.Add(8);  // bit depth
            ihdr.Add(2);  // color type: truecolor RGB
            ihdr.Add(0); ihdr.Add(0); ihdr.Add(0); // compression / filter / interlace
            WriteChunk(bw, "IHDR", ihdr.ToArray());
            WriteChunk(bw, "IDAT", ZlibCompress(raw));
            WriteChunk(bw, "IEND", Array.Empty<byte>());
        }

        private static void WriteChunk(BinaryWriter bw, string type, byte[] data)
        {
            var t = Encoding.ASCII.GetBytes(type);
            bw.Write(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length });
            bw.Write(t);
            bw.Write(data);
            uint crc = Crc32(t, data);
            bw.Write(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
        }

        // zlib stream = 0x78 0x9C header + raw DEFLATE + big-endian Adler-32 of the uncompressed data.
        private static byte[] ZlibCompress(byte[] raw)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0x9C);
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                ds.Write(raw, 0, raw.Length);
            uint a = Adler32(raw);
            ms.WriteByte((byte)(a >> 24)); ms.WriteByte((byte)(a >> 16));
            ms.WriteByte((byte)(a >> 8)); ms.WriteByte((byte)a);
            return ms.ToArray();
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte d in data) { a = (a + d) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }

        private static uint[] _crcTable;
        private static uint Crc32(byte[] a, byte[] b)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    _crcTable[n] = c;
                }
            }
            uint crc = 0xFFFFFFFF;
            foreach (byte d in a) crc = _crcTable[(crc ^ d) & 0xFF] ^ (crc >> 8);
            foreach (byte d in b) crc = _crcTable[(crc ^ d) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        private static (byte, byte, byte) Color(int v)
        {
            switch (v)
            {
                case RegionWatershedEngine.Wall: return (0, 0, 0);              // black — real CAD wall
                case RegionWatershedEngine.EnvWall: return (255, 0, 255);       // magenta — building envelope
                case RegionWatershedEngine.DoorSeal: return (0, 120, 255);      // blue — door seal, marker inside gap (trustworthy)
                case RegionWatershedEngine.DoorSealLoose: return (255, 230, 0); // yellow — door seal, marker outside gap (suspect)
                case RegionWatershedEngine.ProximityBridge: return (255, 150, 0); // orange — proximity (corner) gap-bridge
                case RegionWatershedEngine.SlotFill: return (0, 200, 120);      // green — sealed thin slot (pocket-door cavity)
                case RegionWatershedEngine.Free: return (255, 255, 255);        // white — unreached
                case RegionWatershedEngine.Exterior: return (210, 210, 210);    // light gray — exterior
                default:                                   // room — hashed distinct color
                    byte r = (byte)(60 + (v * 67) % 190);
                    byte g = (byte)(60 + (v * 133) % 190);
                    byte b = (byte)(60 + (v * 199) % 190);
                    return (r, g, b);
            }
        }
    }
}
