using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace Probe
{
    internal static class Program
    {
        private static List<string> _notices = new List<string>();

        private static void Main(string[] args)
        {
            string src = args[0];
            string outDir = args[1];
            Directory.CreateDirectory(outDir);

            CadDocument doc = DwgReader.Read(src, OnNotify);
            Console.WriteLine("READ version=" + doc.Header.Version + " notices=" + _notices.Count);

            var spaces = new List<KeyValuePair<string, BlockRecord>>();
            spaces.Add(new KeyValuePair<string, BlockRecord>("*MODEL*", doc.ModelSpace));
            foreach (BlockRecord b in doc.BlockRecords)
            {
                if (b.Layout != null)
                {
                    spaces.Add(new KeyValuePair<string, BlockRecord>(b.Layout.Name, b));
                }
            }

            Console.WriteLine("SPACES=" + spaces.Count);
            foreach (var kv in spaces)
            {
                var ents = kv.Value.Entities;
                Console.WriteLine("  [" + kv.Key + "] total=" + ents.Count
                                  + " TEXT=" + ents.OfType<TextEntity>().Count(t => !(t is AttributeBase))
                                  + " MTEXT=" + ents.OfType<MText>().Count()
                                  + " DIM=" + ents.OfType<Dimension>().Count()
                                  + " TABLE=" + ents.OfType<TableEntity>().Count()
                                  + " INSERT=" + ents.OfType<Insert>().Count(i => !(i is TableEntity)));
            }

            var ins = spaces.SelectMany(kv => kv.Value.Entities).OfType<Insert>()
                            .Where(i => !(i is TableEntity) && i.Attributes.Count > 0).ToList();
            Console.WriteLine("INSERT-with-attrib=" + ins.Count);
            foreach (var i in ins.Take(2))
            {
                Console.WriteLine("  block=" + (i.Block == null ? "?" : i.Block.Name) + " attribs=" + i.Attributes.Count);
                foreach (var at in i.Attributes.Take(8))
                {
                    Console.WriteLine("     tag=" + at.Tag + " value=[" + Trunc(at.Value) + "] invisible=" + at.IsInvisible + " layer=" + (at.Layer == null ? "?" : at.Layer.Name));
                }
            }

            var tables = spaces.SelectMany(kv => kv.Value.Entities).OfType<TableEntity>().ToList();
            Console.WriteLine("TABLE total=" + tables.Count);
            if (tables.Count > 0)
            {
                var t0 = tables[0];
                Console.WriteLine("  rows=" + t0.Rows.Count + " cols=" + t0.Columns.Count
                                  + " mergedRanges=" + t0.MergedCellRanges.Count + " bbox=" + t0.GetBoundingBox());
                for (int r = 0; r < Math.Min(2, t0.Rows.Count); r++)
                {
                    for (int c = 0; c < Math.Min(3, t0.Columns.Count); c++)
                    {
                        var cell = t0.GetCell(r, c);
                        Console.WriteLine("    cell[" + r + "," + c + "] type=" + cell.Type + " merged=" + cell.MergedValue
                                          + " contents=" + cell.Contents.Count
                                          + " value=" + Describe(cell.Content == null ? null : cell.Content.CadValue));
                    }
                }

                var tdoc = DwgReader.Read(src, OnNotify);
                var tt = spacesOf(tdoc).SelectMany(b => b.Entities).OfType<TableEntity>().First();
                var c00 = tt.GetCell(0, 0);
                if (c00.Content != null)
                {
                    object before = c00.Content.CadValue.Value;
                    try
                    {
                        c00.Content.CadValue.SetValue("PROBE-REPLACED");
                        string tpath = Path.Combine(outDir, "table.dwg");
                        DwgWriter.Write(tpath, tdoc, null, OnNotify);
                        var bt = spacesOf(DwgReader.Read(tpath, OnNotify)).SelectMany(b => b.Entities).OfType<TableEntity>().First();
                        Console.WriteLine("  cell write: before=" + before + " after=" + Describe(bt.GetCell(0, 0).Content == null ? null : bt.GetCell(0, 0).Content.CadValue));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  cell write FAILED: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            var mtexts = spaces.SelectMany(kv => kv.Value.Entities).OfType<MText>().ToList();
            if (mtexts.Count > 0)
            {
                foreach (double s in new[] { 1.0, 1.1, 1.2, 1.15, 1.5, 2.0 })
                {
                    var mdoc = DwgReader.Read(src, OnNotify);
                    var m = mdoc.ModelSpace.Entities.OfType<MText>().First();
                    m.BackgroundFillFlags = BackgroundFillFlags.UseDrawingWindowColor;
                    m.BackgroundScale = s;
                    string p = Path.Combine(outDir, "bg_" + s.ToString("0.00") + ".dwg");
                    DwgWriter.Write(p, mdoc, null, OnNotify);
                    var bm = DwgReader.Read(p, OnNotify).ModelSpace.Entities.OfType<MText>().First();
                    Console.WriteLine("  bgScale in=" + s + " out=" + bm.BackgroundScale + " flags=" + bm.BackgroundFillFlags);
                }
            }

            var dims = spaces.SelectMany(kv => kv.Value.Entities).OfType<Dimension>().ToList();
            Console.WriteLine("DIM total=" + dims.Count);
            int withExplicit = dims.Count(d => !string.IsNullOrEmpty(d.Text) && d.Text != "<>");
            Console.WriteLine("  explicitText=" + withExplicit + " measurementOnly=" + (dims.Count - withExplicit));
            foreach (var d in dims.Take(3))
            {
                var db = d.GetBoundingBox();
                Console.WriteLine("  " + d.GetType().Name + " text=[" + d.Text + "] userLoc=" + d.IsTextUserDefinedLocation
                                  + " bbox=(" + db.Min.X.ToString("0.#") + "," + db.Min.Y.ToString("0.#") + ")-("
                                  + db.Max.X.ToString("0.#") + "," + db.Max.Y.ToString("0.#") + ")");
                if (d.Block != null)
                {
                    var mt = d.Block.Entities.OfType<MText>().FirstOrDefault();
                    var tx = d.Block.Entities.OfType<TextEntity>().FirstOrDefault(t => !(t is AttributeBase));
                    Console.WriteLine("    block=" + d.Block.Name + " mtext=" + (mt == null ? "none" : "[" + Trunc(mt.Value) + "] at " + mt.InsertPoint)
                                      + " text=" + (tx == null ? "none" : "[" + Trunc(tx.Value) + "]"));
                    if (mt != null)
                    {
                        var mb = mt.GetBoundingBox();
                        Console.WriteLine("    mtext bbox=(" + mb.Min.X.ToString("0.#") + "," + mb.Min.Y.ToString("0.#") + ")-("
                                          + mb.Max.X.ToString("0.#") + "," + mb.Max.Y.ToString("0.#") + ") h=" + mt.Height);
                    }
                }
            }

            // ---------- bbox sanity: plain MTEXT / TEXT ----------
            var mm = doc.ModelSpace.Entities.OfType<MText>().First();
            Console.WriteLine("PLAIN MTEXT value=[" + Trunc(mm.Value) + "] h=" + mm.Height
                              + " rectW=" + mm.RectangleWidth + " attach=" + mm.AttachmentPoint + " rot=" + mm.Rotation
                              + " bgFlags=" + mm.BackgroundFillFlags + " bgScale=" + mm.BackgroundScale);
            var mmb = mm.GetBoundingBox();
            Console.WriteLine("  bbox=(" + mmb.Min.X.ToString("0.#") + "," + mmb.Min.Y.ToString("0.#") + ")-("
                              + mmb.Max.X.ToString("0.#") + "," + mmb.Max.Y.ToString("0.#") + ")");

            var t = new TextEntity { Value = "TEST-123", Height = 2.5, InsertPoint = new XYZ(10, 20, 0) };
            var tb = t.GetBoundingBox();
            Console.WriteLine("NEW TEXT bbox=(" + tb.Min.X.ToString("0.#") + "," + tb.Min.Y.ToString("0.#") + ")-("
                              + tb.Max.X.ToString("0.#") + "," + tb.Max.Y.ToString("0.#") + ")");

            var mt2 = new MText { Value = "HELLO", Height = 2.5, InsertPoint = new XYZ(0, 0, 0) };
            var mb2 = mt2.GetBoundingBox();
            Console.WriteLine("NEW MTEXT bbox=(" + mb2.Min.X.ToString("0.#") + "," + mb2.Min.Y.ToString("0.#") + ")-("
                              + mb2.Max.X.ToString("0.#") + "," + mb2.Max.Y.ToString("0.#") + ")");

            // ---------- bbox of Insert / Line / Polyline2D / Hatch ----------
            var anyIns = doc.ModelSpace.Entities.OfType<Insert>().FirstOrDefault(i => !(i is TableEntity));
            if (anyIns != null)
            {
                var ib = anyIns.GetBoundingBox();
                Console.WriteLine("INSERT block=" + (anyIns.Block == null ? "?" : anyIns.Block.Name)
                                  + " scale=" + anyIns.XScale + "," + anyIns.YScale + " rot=" + anyIns.Rotation
                                  + " blockEnts=" + (anyIns.Block == null ? -1 : anyIns.Block.Entities.Count));
                Console.WriteLine("  insert bbox=(" + ib.Min.X.ToString("0.#") + "," + ib.Min.Y.ToString("0.#") + ")-("
                                  + ib.Max.X.ToString("0.#") + "," + ib.Max.Y.ToString("0.#") + ")");
            }

            var anyLine = doc.ModelSpace.Entities.OfType<Line>().FirstOrDefault();
            if (anyLine != null)
            {
                var lb = anyLine.GetBoundingBox();
                Console.WriteLine("LINE bbox=(" + lb.Min.X.ToString("0.#") + "," + lb.Min.Y.ToString("0.#") + ")-("
                                  + lb.Max.X.ToString("0.#") + "," + lb.Max.Y.ToString("0.#") + ")");
            }

            var anyPl = doc.ModelSpace.Entities.OfType<Polyline2D>().FirstOrDefault();
            if (anyPl != null)
            {
                var pb = anyPl.GetBoundingBox();
                Console.WriteLine("POLYLINE2D bbox=(" + pb.Min.X.ToString("0.#") + "," + pb.Min.Y.ToString("0.#") + ")-("
                                  + pb.Max.X.ToString("0.#") + "," + pb.Max.Y.ToString("0.#") + ")");
            }

            var anyHatch = doc.ModelSpace.Entities.OfType<Hatch>().FirstOrDefault();
            if (anyHatch != null)
            {
                var hb = anyHatch.GetBoundingBox();
                Console.WriteLine("HATCH bbox=(" + hb.Min.X.ToString("0.#") + "," + hb.Min.Y.ToString("0.#") + ")-("
                                  + hb.Max.X.ToString("0.#") + "," + hb.Max.Y.ToString("0.#") + ")");
            }

            // ---------- block definitions: attdef / attrib / table / anonymous ----------
            Console.WriteLine("BLOCKS (top 12 by entity count):");
            foreach (BlockRecord b in doc.BlockRecords.OrderByDescending(x => x.Entities.Count).Take(12))
            {
                int attdef = b.Entities.OfType<AttributeDefinition>().Count();
                int attrib = b.Entities.OfType<AttributeEntity>().Count();
                int tbl = b.Entities.OfType<TableEntity>().Count();
                int insCnt = b.Entities.OfType<Insert>().Count(i => !(i is TableEntity));
                Console.WriteLine("  " + b.Name + " ents=" + b.Entities.Count + " ATTDEF=" + attdef + " ATTRIB=" + attrib
                                  + " TABLE=" + tbl + " INSERT=" + insCnt + " anon=" + b.IsAnonymous
                                  + " layout=" + (b.Layout == null ? "-" : b.Layout.Name));
            }
        }

        private static IEnumerable<BlockRecord> spacesOf(CadDocument doc)
        {
            yield return doc.ModelSpace;
            foreach (BlockRecord b in doc.BlockRecords)
            {
                if (b.Layout != null) yield return b;
            }
        }

        private static string Describe(CadValue v)
        {
            if (v == null) return "<null>";
            return "[" + (v.Value == null ? "null" : v.Value.ToString()) + "] type=" + v.ValueType;
        }

        private static string Trunc(string s)
        {
            if (s == null) return "null";
            return s.Length > 70 ? s.Substring(0, 70) + "..." : s;
        }

        private static void OnNotify(object sender, NotificationEventArgs e)
        {
            if (e != null && (e.NotificationType == NotificationType.Error || e.NotificationType == NotificationType.Warning))
            {
                _notices.Add(e.NotificationType + ": " + e.Message);
            }
        }
    }
}
