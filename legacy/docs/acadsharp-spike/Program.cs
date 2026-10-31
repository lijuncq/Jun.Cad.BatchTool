using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;

class Probe
{
    static List<string> notes = new List<string>();

    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "inspect";
        switch (mode)
        {
            case "inspect": Inspect(args[1]); break;
            case "roundtrip": RoundTrip(args[1], args[2], args.Length > 3 ? args[3] : null); break;
            case "modify": Modify(args[1], args[2]); break;
            case "blockreplace": BlockReplace(args[1], args[2]); break;
            case "hatch": HatchTest(args[1], args[2]); break;
            case "hatchinfo": HatchInfo(args[1], args[2]); break;
            case "setpattern": SetPattern(args[1], args[2], args[3], args[4], args.Length > 5 ? args[5] : null); break;
            case "enum":
                foreach (var v in Enum.GetValues(typeof(HatchPatternType))) Console.WriteLine("HatchPatternType " + v + " = " + (int)v);
                foreach (var v in Enum.GetValues(typeof(HatchStyleType))) Console.WriteLine("HatchStyleType " + v + " = " + (int)v);
                break;
            default: Console.WriteLine("unknown mode"); break;
        }
    }

    static CadDocument Read(string path)
    {
        notes.Clear();
        var doc = DwgReader.Read(path, (s, e) =>
        {
            if (e.NotificationType == NotificationType.Error || e.NotificationType == NotificationType.Warning)
                notes.Add(e.NotificationType + ": " + e.Message);
        });
        return doc;
    }

    static IEnumerable<Entity> AllEntities(CadDocument doc)
    {
        foreach (var e in doc.ModelSpace.Entities) yield return e;
        foreach (var e in doc.PaperSpace.Entities) yield return e;
        foreach (var br in doc.BlockRecords)
        {
            if (br.Name.StartsWith("*Model_Space") || br.Name.StartsWith("*Paper_Space")) continue;
            foreach (var e in br.Entities) yield return e;
        }
    }

    static void Inspect(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var doc = Read(path);
        sw.Stop();

        Console.WriteLine("FILE      : " + Path.GetFileName(path) + "  (" + new FileInfo(path).Length / 1024 + " KB)");
        Console.WriteLine("VERSION   : " + doc.Header.Version);
        Console.WriteLine("READ ms   : " + sw.ElapsedMilliseconds);
        Console.WriteLine("NOTICES   : " + notes.Count);
        foreach (var n in notes.Take(8)) Console.WriteLine("   ! " + n);

        Console.WriteLine("LAYERS    : " + doc.Layers.Count);
        Console.WriteLine("BLOCKS    : " + doc.BlockRecords.Count);
        Console.WriteLine("TEXTSTYLE : " + doc.TextStyles.Count);
        Console.WriteLine("DIMSTYLE  : " + doc.DimensionStyles.Count);
        Console.WriteLine("LINETYPES : " + doc.LineTypes.Count);

        var ents = AllEntities(doc).ToList();
        Console.WriteLine("ENTITIES  : " + ents.Count + " (model+paper+blockdefs)");
        foreach (var g in ents.GroupBy(e => e.GetType().Name).OrderByDescending(g => g.Count()).Take(18))
            Console.WriteLine("   " + g.Key.PadRight(20) + g.Count());

        var hatches = ents.OfType<Hatch>().ToList();
        if (hatches.Count > 0)
        {
            Console.WriteLine("HATCH PATTERNS:");
            foreach (var g in hatches.GroupBy(h => h.Pattern?.Name ?? "(null)").OrderByDescending(g => g.Count()).Take(10))
                Console.WriteLine("   " + g.Key.PadRight(20) + g.Count() + "   lines=" + (g.First().Pattern?.Lines?.Count ?? 0));
        }

        var inserts = ents.OfType<Insert>().ToList();
        if (inserts.Count > 0)
        {
            Console.WriteLine("TOP BLOCKS:");
            foreach (var g in inserts.GroupBy(i => i.Block?.Name ?? "(null)").OrderByDescending(g => g.Count()).Take(10))
                Console.WriteLine("   " + g.Key.PadRight(24) + g.Count());
        }

        var texts = ents.Where(e => e is TextEntity || e is MText).ToList();
        if (texts.Count > 0)
        {
            Console.WriteLine("TEXT STYLES USED:");
            foreach (var g in texts.GroupBy(t => (t is TextEntity te ? te.Style?.Name : ((MText)t).Style?.Name) ?? "(null)"))
                Console.WriteLine("   " + g.Key);
        }

        Console.WriteLine("TEXTSTYLE DETAIL:");
        foreach (var ts in doc.TextStyles)
            Console.WriteLine("   " + ts.Name.PadRight(16) + " font=" + (ts.Filename ?? "") + " big=" + (ts.BigFontFilename ?? "") + " w=" + ts.Width + " h=" + ts.Height);

        Console.WriteLine("DIMSTYLE DETAIL:");
        foreach (var ds in doc.DimensionStyles)
            Console.WriteLine("   " + ds.Name.PadRight(16) + " text=" + (ds.Style?.Name ?? "") + " dimtxt=" + ds.TextHeight + " dimasz=" + ds.ArrowSize);

        Console.WriteLine("LAYER DETAIL (first 15):");
        foreach (var l in doc.Layers.Take(15))
            Console.WriteLine("   " + l.Name.PadRight(20) + " aci=" + l.Color.Index + " lt=" + (l.LineType?.Name ?? "") + " lw=" + l.LineWeight + " plot=" + l.PlotFlag + " flags=" + l.Flags);
    }

    static void RoundTrip(string input, string output, string forceVersion = null)
    {
        var doc = Read(input);
        if (forceVersion != null)
        {
            var v = (ACadVersion)Enum.Parse(typeof(ACadVersion), forceVersion);
            Console.WriteLine("force version " + doc.Header.Version + " -> " + v);
            doc.Header.Version = v;
        }
        int before = AllEntities(doc).Count();
        Console.WriteLine("read ok, entities=" + before + " notices=" + notes.Count);

        if (File.Exists(output)) File.Delete(output);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DwgWriter.Write(output, doc);
        sw.Stop();
        Console.WriteLine("write ms  : " + sw.ElapsedMilliseconds + " -> " + new FileInfo(output).Length / 1024 + " KB");

        var back = Read(output);
        int after = AllEntities(back).Count();
        Console.WriteLine("re-read   : entities=" + after + " notices=" + notes.Count + " version=" + back.Header.Version);
        foreach (var n in notes.Take(10)) Console.WriteLine("   ! " + n);
        Console.WriteLine(before == after ? "ROUNDTRIP COUNT OK" : "ROUNDTRIP COUNT MISMATCH " + before + " -> " + after);
    }

    static Layer EnsureLayer(CadDocument doc, string name, short aci, LineWeightType lw, bool plot)
    {
        Layer layer;
        if (doc.Layers.TryGetValue(name, out layer)) return layer;
        layer = new Layer(name)
        {
            Color = new Color(aci),
            LineWeight = lw,
            PlotFlag = plot
        };
        doc.Layers.Add(layer);
        return layer;
    }

    static void Modify(string input, string output)
    {
        var doc = Read(input);
        Console.WriteLine("== input " + Path.GetFileName(input) + " version=" + doc.Header.Version);

        // ---- 1) EntityLayerChange: LINE/LWPOLYLINE/CIRCLE with ACI color 1 -> layer EQUIPMENT
        var target = EnsureLayer(doc, "EQUIPMENT", 3, LineWeightType.W25, true);
        int hit = 0, moved = 0;
        foreach (var e in AllEntities(doc).ToList())
        {
            if (!(e is Line || e is LwPolyline || e is Circle)) continue;
            if (e.Color.Index != 1) continue;
            hit++;
            if (e.Layer != null && e.Layer.Name == target.Name) continue;
            e.Layer = target;
            moved++;
        }
        Console.WriteLine("rule EntityLayerChange: hit=" + hit + " moved=" + moved);

        // ---- 2) TextFontChange: create/point text style GB_STANDARD
        TextStyle ts;
        if (!doc.TextStyles.TryGetValue("GB_STANDARD", out ts))
        {
            ts = new TextStyle("GB_STANDARD") { Filename = "gbenor.shx", BigFontFilename = "gbcbig.shx" };
            doc.TextStyles.Add(ts);
        }
        ts.Filename = "gbenor.shx";
        ts.BigFontFilename = "gbcbig.shx";
        ts.Width = 1.0;
        ts.ObliqueAngle = 0;
        int remapped = 0;
        foreach (var e in AllEntities(doc).ToList())
        {
            if (e is TextEntity te) { te.Style = ts; remapped++; }
            else if (e is MText mt) { mt.Style = ts; remapped++; }
        }
        Console.WriteLine("rule TextFontChange: remapped=" + remapped);

        // ---- 3) HatchPatternChange: ANSI31 -> ANSI37 (pattern definition loaded from .pat)
        var patFile = FindPat();
        List<HatchPattern> patterns = patFile == null ? new List<HatchPattern>() : HatchPattern.LoadFrom(patFile).ToList();
        Console.WriteLine("pat file=" + (patFile ?? "NOT FOUND") + " patterns loaded=" + patterns.Count);
        var ansi37 = patterns.FirstOrDefault(p => string.Equals(p.Name, "ANSI37", StringComparison.OrdinalIgnoreCase));
        int hchanged = 0;
        foreach (var h in AllEntities(doc).OfType<Hatch>().ToList())
        {
            if (h.Pattern == null) continue;
            if (!h.Pattern.Name.Equals("ANSI31", StringComparison.OrdinalIgnoreCase)) continue;
            if (ansi37 != null) { h.Pattern = ansi37.Clone(); h.PatternScale = 2.0; h.PatternAngle = Math.PI / 6; }
            else h.Pattern.Name = "ANSI37";
            hchanged++;
        }
        Console.WriteLine("rule HatchPatternChange: changed=" + hchanged + " (ANSI37 def loaded=" + (ansi37 != null) + ", lines=" + (ansi37?.Lines?.Count ?? 0) + ")");

        // ---- 5) DimensionStyleChange: clone Standard -> GB_DIM, remap all dimensions
        DimensionStyle baseDs;
        doc.DimensionStyles.TryGetValue("Standard", out baseDs);
        DimensionStyle gbDim;
        if (!doc.DimensionStyles.TryGetValue("GB_DIM", out gbDim))
        {
            gbDim = (DimensionStyle)(baseDs != null ? baseDs.Clone() : new DimensionStyle("GB_DIM"));
            gbDim.Name = "GB_DIM";
            doc.DimensionStyles.Add(gbDim);
        }
        gbDim.TextHeight = 3.5;
        gbDim.ArrowSize = 2.5;
        gbDim.Style = ts;
        int dims = 0;
        foreach (var dm in AllEntities(doc).OfType<Dimension>().ToList()) { dm.Style = gbDim; dims++; }
        Console.WriteLine("rule DimensionStyleChange: remapped=" + dims);

        // ---- 6) EntityLinetypeChange: create DASHED from scratch, apply to LINE entities
        LineType dashed;
        if (!doc.LineTypes.TryGetValue("DASHED", out dashed))
        {
            dashed = new LineType("DASHED") { Description = "Dashed __  __  __" };
            dashed.AddSegment(new LineType.Segment { Length = 0.5 });
            dashed.AddSegment(new LineType.Segment { Length = -0.25 });
            doc.LineTypes.Add(dashed);
        }
        int ltset = 0;
        foreach (var e in AllEntities(doc).ToList().Where(x => x is Line).Take(5)) { e.LineType = dashed; ltset++; }
        Console.WriteLine("rule EntityLinetypeChange: created DASHED, applied=" + ltset + " segments=" + dashed.Segments.Count() + " len=" + dashed.PatternLength);

        // ---- 4) BlockLayerChange: block TB-* -> layer TK-FRAME (ref + block content)
        var frame = EnsureLayer(doc, "TK-FRAME", 7, LineWeightType.W25, true);
        int bref = 0, bcontent = 0;
        var done = new HashSet<BlockRecord>();
        foreach (var ins in AllEntities(doc).OfType<Insert>().ToList())
        {
            if (ins.Block == null || !ins.Block.Name.StartsWith("TB-", StringComparison.OrdinalIgnoreCase)) continue;
            ins.Layer = frame;
            bref++;
            if (done.Add(ins.Block))
            {
                foreach (var inner in ins.Block.Entities) { inner.Layer = frame; bcontent++; }
            }
        }
        Console.WriteLine("rule BlockLayerChange: refs=" + bref + " innerEntities=" + bcontent);

        if (File.Exists(output)) File.Delete(output);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DwgWriter.Write(output, doc);
        sw.Stop();
        Console.WriteLine("write ms=" + sw.ElapsedMilliseconds + " size=" + new FileInfo(output).Length / 1024 + " KB");

        // ---- verify by re-reading
        var back = Read(output);
        var bents = AllEntities(back).ToList();
        Console.WriteLine("-- verify --");
        Console.WriteLine("EQUIPMENT exists=" + back.Layers.Contains("EQUIPMENT") + " TK-FRAME exists=" + back.Layers.Contains("TK-FRAME"));
        Console.WriteLine("GB_STANDARD font=" + (back.TextStyles.Contains("GB_STANDARD") ? back.TextStyles["GB_STANDARD"].Filename : "(missing)"));
        Console.WriteLine("on EQUIPMENT=" + bents.Count(e => e.Layer != null && e.Layer.Name == "EQUIPMENT"));
        Console.WriteLine("text on GB_STANDARD=" + bents.Count(e => (e is TextEntity te && te.Style != null && te.Style.Name == "GB_STANDARD") || (e is MText mt && mt.Style != null && mt.Style.Name == "GB_STANDARD")));
        var hb = bents.OfType<Hatch>().Where(h => h.Pattern != null && h.Pattern.Name.Equals("ANSI37", StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine("hatch ANSI37=" + hb.Count + " patternLines=" + (hb.FirstOrDefault()?.Pattern?.Lines?.Count ?? -1) + " angle=" + (hb.FirstOrDefault()?.PatternAngle ?? -1));
        Console.WriteLine("notices=" + notes.Count);
        foreach (var n in notes.Take(6)) Console.WriteLine("   ! " + n);
    }

    static void BlockReplace(string input, string output)
    {
        var doc = Read(input);
        Console.WriteLine("== block replace on " + Path.GetFileName(input));

        // 1) create the target block definition with some content
        BlockRecord target;
        if (!doc.BlockRecords.TryGetValue("A1_TITLE_NEW", out target))
        {
            target = new BlockRecord("A1_TITLE_NEW");
            doc.BlockRecords.Add(target);
        }
        target.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(420, 297, 0) });
        target.Entities.Add(new AttributeDefinition { Tag = "DWG_NO", Value = "DEFAULT", InsertPoint = new XYZ(10, 10, 0), Height = 2.5 });

        // 2) replace every insert of TB-2025
        int replaced = 0, migrated = 0;
        foreach (var owner in new[] { doc.ModelSpace, doc.PaperSpace }.Concat(doc.BlockRecords.Where(b => !b.Name.StartsWith("*"))))
        {
            foreach (var ins in owner.Entities.OfType<Insert>().Where(i => i.Block != null && i.Block.Name == "TB-2025").ToList())
            {
                var srcAttrs = ins.Attributes.ToDictionary(a => a.Tag, a => a.Value, StringComparer.OrdinalIgnoreCase);
                var ni = new Insert(target)
                {
                    InsertPoint = ins.InsertPoint,
                    Rotation = ins.Rotation,
                    XScale = ins.XScale, YScale = ins.YScale, ZScale = ins.ZScale,
                    Layer = ins.Layer,
                    Color = ins.Color
                };
                // let the library build ATTRIBs from the block's ATTDEFs, then assign by tag
                ni.UpdateAttributes();
                foreach (var na in ni.Attributes)
                {
                    string v;
                    if (na.Tag != null && srcAttrs.TryGetValue(na.Tag, out v)) { na.Value = v; migrated++; }
                }
                owner.Entities.Remove(ins);
                owner.Entities.Add(ni);
                replaced++;
            }
        }
        Console.WriteLine("rule BlockReplace: replaced=" + replaced + " attributesMigrated=" + migrated + " targetAttDefs=" + target.AttributeDefinitions.Count());

        if (File.Exists(output)) File.Delete(output);
        DwgWriter.Write(output, doc);
        Console.WriteLine("written " + new FileInfo(output).Length / 1024 + " KB");

        var back = Read(output);
        var all = AllEntities(back).ToList();
        foreach (var i2 in all.OfType<Insert>().Where(i => i.Block != null && i.Block.Name == "A1_TITLE_NEW"))
            Console.WriteLine("   re-read insert attrs=" + i2.Attributes.Count() + " hasAttr=" + i2.HasAttributes);
        Console.WriteLine("verify: insertsOfNew=" + all.OfType<Insert>().Count(i => i.Block != null && i.Block.Name == "A1_TITLE_NEW")
            + " insertsOfOld=" + all.OfType<Insert>().Count(i => i.Block != null && i.Block.Name == "TB-2025")
            + " attEntities=" + all.OfType<AttributeEntity>().Count());
    }

    static void HatchTest(string input, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var patFile = FindPat();
        var patterns = patFile == null ? new List<HatchPattern>() : HatchPattern.LoadFrom(patFile).ToList();
        Console.WriteLine("pat=" + (patFile ?? "NOT FOUND") + " loaded=" + patterns.Count);

        // 0) 先看源文件里的填充是什么样（对照 ACadSharp 的枚举值与 AutoCAD 组码）
        var probeDoc = Read(input);
        Console.WriteLine("-- source hatches --");
        foreach (var h in AllEntities(probeDoc).OfType<Hatch>())
            Console.WriteLine("   name=" + h.Pattern?.Name + " ptype=" + h.PatternType + "(" + (int)h.PatternType + ")"
                + " solid=" + h.IsSolid + " double=" + h.IsDouble + " style=" + h.Style
                + " scale=" + h.PatternScale + " angle=" + h.PatternAngle
                + " plines=" + (h.Pattern?.Lines?.Count ?? 0) + " paths=" + h.Paths.Count
                + " assoc=" + h.IsAssociative + " grad=" + (h.GradientColor?.Enabled ?? false));

        // ---- 用例 1：换图案（完整定义 + 比例 + 角度 + 填充样式）----
        RunCase(input, outDir, "c1_swap", doc =>
        {
            var pat = patterns.First(p => p.Name.Equals("ANSI31", StringComparison.OrdinalIgnoreCase));
            int n = 0;
            foreach (var h in AllEntities(doc).OfType<Hatch>().Where(x => x.Pattern?.Name == "ANSI37"))
            {
                h.Pattern = pat.Clone();
                h.PatternType = HatchPatternType.SolidFill;   // =1 = 预定义（枚举名有误导）
                h.PatternScale = 2.0;
                h.PatternAngle = 30 * Math.PI / 180.0;
                h.Style = HatchStyleType.Outer;
                n++;
            }
            return "swapped " + n + " (ANSI37->ANSI31, scale2.0, 30deg, Outer)";
        });

        // ---- 用例 2：图案 → 渐变 ----
        RunCase(input, outDir, "c2_gradient", doc =>
        {
            int n = 0;
            foreach (var h in AllEntities(doc).OfType<Hatch>().Where(x => x.Pattern?.Name == "GRATE"))
            {
                h.IsSolid = true;
                h.GradientColor = new HatchGradientPattern
                {
                    Enabled = true,
                    Name = "LINEAR",
                    Angle = 45 * Math.PI / 180.0,
                    Shift = 0.1,
                    IsSingleColorGradient = true,
                    ColorTint = 0.5
                };
                n++;
            }
            return "to gradient " + n + " (LINEAR, 45deg, shift 0.1, single color)";
        });

        // ---- 用例 3：图案 → 纯色 ----
        RunCase(input, outDir, "c3_solid", doc =>
        {
            int n = 0;
            foreach (var h in AllEntities(doc).OfType<Hatch>().Where(x => x.Pattern?.Name == "ANSI38"))
            {
                h.IsSolid = true;
                if (h.GradientColor != null) h.GradientColor.Enabled = false;
                n++;
            }
            return "to solid " + n;
        });

        // ---- 用例 4：图案 → 用户自定义（手工构造图案线，间距 5）----
        RunCase(input, outDir, "c4_userdef", doc =>
        {
            int n = 0;
            foreach (var h in AllEntities(doc).OfType<Hatch>().Where(x => x.Pattern?.Name == "JIS_LC_8A"))
            {
                var up = new HatchPattern("U") { Description = "User defined", Lines = new List<HatchPattern.Line>() };
                up.Lines.Add(new HatchPattern.Line
                {
                    Angle = 0,
                    BasePoint = new XY(0, 0),
                    Offset = new XY(0, 5),
                    DashLengths = new List<double>()
                });
                h.Pattern = up;
                h.PatternType = HatchPatternType.PatternFill;
                h.PatternScale = 1.0;
                h.PatternAngle = 0;
                n++;
            }
            return "to user-defined spacing 5 -> " + n;
        });

        // ---- 用例 5：双线填充 ----
        RunCase(input, outDir, "c5_double", doc =>
        {
            int n = 0;
            foreach (var h in AllEntities(doc).OfType<Hatch>().Where(x => x.Pattern?.Name == "ANSI31"))
            {
                var pat = patterns.First(p => p.Name.Equals("ANSI37", StringComparison.OrdinalIgnoreCase));
                h.Pattern = pat.Clone();
                h.IsDouble = true;
                n++;
            }
            return "ANSI31->ANSI37 IsDouble=true -> " + n;
        });

        // ---- 用例 8：渐变参数定位（角度 0.5rad / 位移 0.25 / 双色）----
        RunCase(input, outDir, "c8_grad2", doc =>
        {
            int n = 0;
            foreach (var h in AllEntities(doc).OfType<Hatch>().Where(x => x.Pattern?.Name == "GRATE"))
            {
                h.IsSolid = true;
                var g = h.GradientColor ?? new HatchGradientPattern();
                g.Enabled = true;
                g.Name = "CYLINDER";
                g.Angle = 0.5;
                g.Shift = 0.25;
                g.IsSingleColorGradient = false;
                g.ColorTint = 0.75;
                h.GradientColor = g;
                n++;
            }
            return "gradient CYLINDER angle=0.5rad shift=0.25 twoColor tint=0.75 -> " + n;
        });

        // ---- 用例 7：故意用错枚举（PatternFill=0=用户自定义），看 AutoCAD 怎么认 ----
        RunCase(input, outDir, "c7_ptype_wrong", doc =>
        {
            var pat = patterns.First(p => p.Name.Equals("ANSI31", StringComparison.OrdinalIgnoreCase));
            int n = 0;
            foreach (var h in AllEntities(doc).OfType<Hatch>().Where(x => x.Pattern?.Name == "ANSI38"))
            {
                h.Pattern = pat.Clone();
                h.PatternType = HatchPatternType.PatternFill;  // 0 = 用户自定义
                h.PatternScale = 2.0;
                n++;
            }
            return "ANSI38->ANSI31 with PatternType=PatternFill(0) -> " + n;
        });

        // ---- 用例 6：只改比例/角度，不动图案 ----
        RunCase(input, outDir, "c6_scale", doc =>
        {
            int n = 0;
            foreach (var h in AllEntities(doc).OfType<Hatch>().Where(x => x.Pattern?.Name == "GRATE"))
            {
                h.PatternScale = 3.0;
                h.PatternAngle = 15 * Math.PI / 180.0;
                n++;
            }
            return "scale/angle only -> " + n;
        });
    }

    static void RunCase(string input, string outDir, string name, Func<CadDocument, string> action)
    {
        var doc = Read(input);
        string msg;
        try { msg = action(doc); }
        catch (Exception ex) { Console.WriteLine(name + " : EXCEPTION " + ex.GetType().Name + ": " + ex.Message); return; }

        string outPath = Path.Combine(outDir, name + ".dwg");
        if (File.Exists(outPath)) File.Delete(outPath);
        try { DwgWriter.Write(outPath, doc); }
        catch (Exception ex) { Console.WriteLine(name + " : WRITE FAILED " + ex.GetType().Name + ": " + ex.Message); return; }

        var back = Read(outPath);
        Console.WriteLine("== " + name + " : " + msg);
        foreach (var h in AllEntities(back).OfType<Hatch>())
            Console.WriteLine("     -> name=" + h.Pattern?.Name + " ptype=" + h.PatternType + " solid=" + h.IsSolid
                + " double=" + h.IsDouble + " style=" + h.Style + " scale=" + h.PatternScale.ToString("0.###")
                + " angle=" + (h.PatternAngle * 180 / Math.PI).ToString("0.#") + "deg"
                + " plines=" + (h.Pattern?.Lines?.Count ?? 0) + " paths=" + h.Paths.Count
                + " grad=" + (h.GradientColor?.Enabled ?? false) + " gname=" + (h.GradientColor?.Name ?? "-"));
    }

    /// <summary>把指定名字的填充图案换成另一个（模拟 config.json 里的 HatchPatternChange）。</summary>
    static void SetPattern(string input, string output, string sourceName, string targetName, string forceVersion)
    {
        var doc = Read(input);
        Console.WriteLine("in=" + Path.GetFileName(input) + " version=" + doc.Header.Version + " notices=" + notes.Count);
        if (forceVersion != null) doc.Header.Version = (ACadVersion)Enum.Parse(typeof(ACadVersion), forceVersion);

        var patFile = FindPat();
        var pats = HatchPattern.LoadFrom(patFile).ToList();
        var target = pats.FirstOrDefault(x => x.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            Console.WriteLine("目标图案「" + targetName + "」在图案库中不存在，中止。");
            return;
        }
        Console.WriteLine("target pattern: " + target.Name + " lines=" + target.Lines.Count
            + " [" + string.Join("; ", target.Lines.Select(l => l.Angle.ToString("0.####") + "@off(" + l.Offset.X + "," + l.Offset.Y + ")")) + "]");

        int matched = 0, changed = 0;
        var owned = new List<KeyValuePair<string, Hatch>>();
        foreach (var e in doc.ModelSpace.Entities.OfType<Hatch>()) owned.Add(new KeyValuePair<string, Hatch>("Model", e));
        foreach (var e in doc.PaperSpace.Entities.OfType<Hatch>()) owned.Add(new KeyValuePair<string, Hatch>("Paper", e));
        foreach (var br in doc.BlockRecords.Where(b => !b.Name.StartsWith("*")))
            foreach (var e in br.Entities.OfType<Hatch>()) owned.Add(new KeyValuePair<string, Hatch>("Block:" + br.Name, e));

        foreach (var kv in owned)
        {
            var h = kv.Value;
            string cur = h.Pattern?.Name;
            if (cur == null || !cur.Equals(sourceName, StringComparison.OrdinalIgnoreCase)) continue;
            matched++;
            Console.WriteLine("   hit@" + kv.Key + ": " + cur + " layer=" + h.Layer?.Name + " scale=" + h.PatternScale.ToString("0.###")
                + " angle=" + (h.PatternAngle * 180 / Math.PI).ToString("0.#") + "deg paths=" + h.Paths.Count
                + " ptype=" + h.PatternType);
            h.Pattern = target.Clone();
            h.PatternType = HatchPatternType.SolidFill;   // =1 = 预定义
            changed++;   // 比例/角度保持原样
        }
        Console.WriteLine("matched=" + matched + " changed=" + changed);

        if (File.Exists(output)) File.Delete(output);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DwgWriter.Write(output, doc);
        sw.Stop();
        Console.WriteLine("written " + new FileInfo(output).Length / 1024 + " KB in " + sw.ElapsedMilliseconds + "ms");
    }

    static void HatchInfo(string input, string patternName)
    {
        var doc = Read(input);
        foreach (var h in AllEntities(doc).OfType<Hatch>())
        {
            if (h.Pattern?.Name == null) continue;
            if (patternName != null && !h.Pattern.Name.Equals(patternName, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine(h.Pattern.Name + "  scale=" + h.PatternScale.ToString("0.####") + "  angle=" + (h.PatternAngle * 180 / Math.PI).ToString("0.#") + "deg  ptype=" + h.PatternType + "  lines=" + h.Pattern.Lines.Count);
            foreach (var l in h.Pattern.Lines)
                Console.WriteLine("    line angle=" + (l.Angle * 180 / Math.PI).ToString("0.##") + "deg offset=(" + l.Offset.X.ToString("0.####") + "," + l.Offset.Y.ToString("0.####")
                    + ") |offset|=" + Math.Sqrt(l.Offset.X * l.Offset.X + l.Offset.Y * l.Offset.Y).ToString("0.####")
                    + "  -> effective=" + (Math.Sqrt(l.Offset.X * l.Offset.X + l.Offset.Y * l.Offset.Y) * h.PatternScale).ToString("0.####"));
        }
    }

    static string FindPat()
    {
        string[] candidates = {
            @"C:\Program Files\Autodesk\AutoCAD 2025\UserDataCache\zh-cn\Support\acadiso.pat",
            @"C:\Users\lijun\AppData\Roaming\Autodesk\AutoCAD 2025\R25.0\chs\Support\acadiso.pat",
            @"C:\Program Files\Autodesk\AutoCAD 2025\UserDataCache\zh-cn\Support\acad.pat"
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
