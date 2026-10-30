using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace NbaQuant
{
    static class U
    {
        public static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static List<string[]> ReadCsv(string path, char sep = ',')
        {
            var rows = new List<string[]>();
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                rows.Add(SplitLine(line, sep));
            }
            return rows;
        }

        public static string[] SplitLine(string line, char sep)
        {
            var res = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { q = !q; continue; }
                if (c == sep && !q) { res.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            res.Add(sb.ToString());
            return res.ToArray();
        }

        public static double D(string s, double def = 0)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            s = s.Trim().Replace("+", "");
            if (s.StartsWith(".")) s = "0" + s;
            if (s.StartsWith("-.")) s = "-0" + s.Substring(1);
            double v;
            return double.TryParse(s, NumberStyles.Float, IC, out v) ? v : def;
        }

        public static string Norm(string name)
        {
            if (name == null) return "";
            string s = name.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in s)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            s = sb.ToString().ToLowerInvariant().Replace(".", "").Replace("'", "").Replace("-", " ").Replace(",", " ");
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p != "jr" && p != "sr" && p != "ii" && p != "iii" && p != "iv").ToArray();
            return string.Join(" ", parts);
        }

        // American odds helpers
        public static double ImpliedProb(double am) { return am > 0 ? 100.0 / (am + 100.0) : -am / (-am + 100.0); }
        public static double NetPayout(double am) { return am > 0 ? am / 100.0 : 100.0 / -am; }
        public static double FairAmerican(double p)
        {
            p = Math.Min(0.9999, Math.Max(0.0001, p));
            return p >= 0.5 ? -100.0 * p / (1 - p) : 100.0 * (1 - p) / p;
        }
        public static double EvPerDollar(double p, double am) { return p * NetPayout(am) - (1 - p); }
        public static string Am(double a) { return (a > 0 ? "+" : "") + Math.Round(a).ToString(IC); }
        public static string Pct(double p) { return (100 * p).ToString("0.0", IC) + "%"; }
        public static string F1(double v) { return v.ToString("0.0", IC); }
        public static string F2(double v) { return v.ToString("0.00", IC); }
        public static string F3(double v) { return v.ToString("0.000", IC); }

        public static double Quantile(int[] sorted, double q)
        {
            if (sorted.Length == 0) return 0;
            int idx = (int)Math.Floor(q * (sorted.Length - 1));
            return sorted[Math.Max(0, Math.Min(sorted.Length - 1, idx))];
        }

        public static int HeightInches(string h)
        {
            // ESPN "6' 11\"" style
            if (string.IsNullOrEmpty(h)) return 78;
            double dv;
            if (double.TryParse(h, NumberStyles.Float, IC, out dv) && dv >= 48) return (int)Math.Round(dv);
            var digits = new string(h.Select(c => char.IsDigit(c) ? c : ' ').ToArray()).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (digits.Length >= 2) return int.Parse(digits[0]) * 12 + int.Parse(digits[1]);
            if (digits.Length == 1) { int v = int.Parse(digits[0]); return v >= 48 ? v : v * 12; }
            return 78;
        }
    }

    // xoshiro256** PRNG
    class Rng
    {
        ulong s0, s1, s2, s3;
        public Rng(ulong seed)
        {
            ulong z = seed;
            s0 = SplitMix(ref z); s1 = SplitMix(ref z); s2 = SplitMix(ref z); s3 = SplitMix(ref z);
        }
        static ulong SplitMix(ref ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
        static ulong Rotl(ulong x, int k) { return (x << k) | (x >> (64 - k)); }
        public ulong NextU()
        {
            ulong result = Rotl(s1 * 5, 7) * 9;
            ulong t = s1 << 17;
            s2 ^= s0; s3 ^= s1; s1 ^= s2; s0 ^= s3; s2 ^= t; s3 = Rotl(s3, 45);
            return result;
        }
        public double U01() { return (NextU() >> 11) * (1.0 / 9007199254740992.0); }
        bool hasSpare; double spare;
        public double Normal()
        {
            if (hasSpare) { hasSpare = false; return spare; }
            double u, v, s;
            do { u = 2 * U01() - 1; v = 2 * U01() - 1; s = u * u + v * v; } while (s >= 1 || s == 0);
            double m = Math.Sqrt(-2 * Math.Log(s) / s);
            spare = v * m; hasSpare = true;
            return u * m;
        }
    }

    // ---------- Minimal XLSX writer (inline strings, numbers, formulas with cached values) ----------
    enum CT { Str, Num, Formula, Empty }
    class Cell
    {
        public CT Type; public string S; public double N; public string F; public int Style;
        public static Cell Str(string s, int st = 0) { return new Cell { Type = CT.Str, S = s ?? "", Style = st }; }
        public static Cell Num(double n, int st = 0) { return new Cell { Type = CT.Num, N = n, Style = st }; }
        public static Cell Fx(string f, double cached, int st = 0) { return new Cell { Type = CT.Formula, F = f, N = cached, Style = st }; }
        public static Cell FxS(string f, string cached, int st = 0) { return new Cell { Type = CT.Formula, F = f, S = cached, Style = st }; }
        public static Cell Empty(int st = 0) { return new Cell { Type = CT.Empty, Style = st }; }
    }
    class Sheet
    {
        public string Name;
        public List<List<Cell>> Rows = new List<List<Cell>>();
        public Dictionary<int, double> Widths = new Dictionary<int, double>();
        public int FreezeRow = 0;
        public List<string> Merges = new List<string>();
        public List<Cell> Row() { var r = new List<Cell>(); Rows.Add(r); return r; }
        public int CurRow { get { return Rows.Count; } }   // 1-based index of last added row
    }

    static class Styles
    {
        public const int Def = 0, Hdr = 1, Title = 2, Pct = 3, N1 = 4, Int = 5, Odds = 6, InputOdds = 7, Wrap = 8, Sub = 9,
            PctB = 10, N2 = 11, InputMoney = 12, Money = 13, Bold = 14, Section = 15, OddsBlue = 16, PctBlue = 17, NBlue = 18, Good = 19, Bad = 20;
    }

    static class Xlsx
    {
        public static string ColName(int c)
        {
            string s = "";
            c++;
            while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; }
            return s;
        }
        static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (ch == '&') sb.Append("&amp;");
                else if (ch == '<') sb.Append("&lt;");
                else if (ch == '>') sb.Append("&gt;");
                else if (ch == '"') sb.Append("&quot;");
                else if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') continue;
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        public static void Save(string path, List<Sheet> sheets)
        {
            if (File.Exists(path)) File.Delete(path);
            using (var fs = new FileStream(path, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Put(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
                Put(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
                var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                for (int i = 0; i < sheets.Count; i++)
                {
                    wb.AppendFormat("<sheet name=\"{0}\" sheetId=\"{1}\" r:id=\"rId{1}\"/>", Esc(sheets[i].Name), i + 1);
                    rels.AppendFormat("<Relationship Id=\"rId{0}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{0}.xml\"/>", i + 1);
                }
                wb.Append("</sheets><calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/></workbook>");
                rels.AppendFormat("<Relationship Id=\"rId{0}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>", sheets.Count + 1);
                Put(zip, "xl/workbook.xml", wb.ToString());
                Put(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
                Put(zip, "xl/styles.xml", StylesXml());
                for (int i = 0; i < sheets.Count; i++) Put(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", SheetXml(sheets[i]));
            }
        }

        static void Put(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(content);
        }

        static string ContentTypes(int n)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (int i = 1; i <= n; i++) sb.AppendFormat("<Override PartName=\"/xl/worksheets/sheet{0}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>", i);
            sb.Append("</Types>");
            return sb.ToString();
        }

        static string StylesXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<numFmts count=\"5\"><numFmt numFmtId=\"164\" formatCode=\"0.0%\"/><numFmt numFmtId=\"165\" formatCode=\"0.0\"/><numFmt numFmtId=\"166\" formatCode=\"+0;-0;0\"/><numFmt numFmtId=\"167\" formatCode=\"$0.00;($0.00);-\"/><numFmt numFmtId=\"168\" formatCode=\"0.00\"/></numFmts>" +
                "<fonts count=\"8\">" +
                "<font><sz val=\"10\"/><name val=\"Arial\"/></font>" +
                "<font><b/><sz val=\"10\"/><color rgb=\"FFFFFFFF\"/><name val=\"Arial\"/></font>" +
                "<font><b/><sz val=\"14\"/><color rgb=\"FF1F3864\"/><name val=\"Arial\"/></font>" +
                "<font><sz val=\"10\"/><color rgb=\"FF0000FF\"/><name val=\"Arial\"/></font>" +
                "<font><i/><sz val=\"9\"/><color rgb=\"FF595959\"/><name val=\"Arial\"/></font>" +
                "<font><b/><sz val=\"10\"/><name val=\"Arial\"/></font>" +
                "<font><b/><sz val=\"10\"/><color rgb=\"FF006100\"/><name val=\"Arial\"/></font>" +
                "<font><b/><sz val=\"10\"/><color rgb=\"FF9C0006\"/><name val=\"Arial\"/></font>" +
                "</fonts>" +
                "<fills count=\"6\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F3864\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFFF00\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9E1F2\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "</fills>" +
                "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"21\">" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" + // 0 Def
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"center\"/></xf>" + // 1 Hdr
                "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" + // 2 Title
                "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" + // 3 Pct
                "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" + // 4 N1
                "<xf numFmtId=\"1\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" + // 5 Int
                "<xf numFmtId=\"166\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" + // 6 Odds
                "<xf numFmtId=\"166\" fontId=\"3\" fillId=\"3\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\"/>" + // 7 InputOdds
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"top\"/></xf>" + // 8 Wrap
                "<xf numFmtId=\"0\" fontId=\"4\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment wrapText=\"0\"/></xf>" + // 9 Sub
                "<xf numFmtId=\"164\" fontId=\"5\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" + // 10 PctB
                "<xf numFmtId=\"168\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" + // 11 N2
                "<xf numFmtId=\"167\" fontId=\"3\" fillId=\"3\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\"/>" + // 12 InputMoney
                "<xf numFmtId=\"167\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" + // 13 Money
                "<xf numFmtId=\"0\" fontId=\"5\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" + // 14 Bold
                "<xf numFmtId=\"0\" fontId=\"5\" fillId=\"4\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" + // 15 Section
                "<xf numFmtId=\"166\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" + // 16 OddsBlue
                "<xf numFmtId=\"164\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" + // 17 PctBlue
                "<xf numFmtId=\"165\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" + // 18 NBlue
                "<xf numFmtId=\"164\" fontId=\"6\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" + // 19 Good
                "<xf numFmtId=\"164\" fontId=\"7\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" + // 20 Bad
                "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
        }

        static string SheetXml(Sheet sh)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            if (sh.FreezeRow > 0)
                sb.AppendFormat("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"{0}\" topLeftCell=\"A{1}\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>", sh.FreezeRow, sh.FreezeRow + 1);
            else sb.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"13.2\"/>");
            if (sh.Widths.Count > 0)
            {
                sb.Append("<cols>");
                foreach (var kv in sh.Widths.OrderBy(k => k.Key))
                    sb.AppendFormat(U.IC, "<col min=\"{0}\" max=\"{0}\" width=\"{1}\" customWidth=\"1\"/>", kv.Key + 1, kv.Value);
                sb.Append("</cols>");
            }
            sb.Append("<sheetData>");
            for (int r = 0; r < sh.Rows.Count; r++)
            {
                var row = sh.Rows[r];
                sb.AppendFormat("<row r=\"{0}\">", r + 1);
                for (int c = 0; c < row.Count; c++)
                {
                    var cell = row[c];
                    if (cell == null) continue;
                    string refc = ColName(c) + (r + 1);
                    switch (cell.Type)
                    {
                        case CT.Str:
                            sb.AppendFormat("<c r=\"{0}\" s=\"{1}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{2}</t></is></c>", refc, cell.Style, Esc(cell.S));
                            break;
                        case CT.Num:
                            if (double.IsNaN(cell.N) || double.IsInfinity(cell.N)) sb.AppendFormat("<c r=\"{0}\" s=\"{1}\"/>", refc, cell.Style);
                            else sb.AppendFormat(U.IC, "<c r=\"{0}\" s=\"{1}\"><v>{2}</v></c>", refc, cell.Style, cell.N.ToString("R", U.IC));
                            break;
                        case CT.Formula:
                            if (cell.S != null)
                                sb.AppendFormat("<c r=\"{0}\" s=\"{1}\" t=\"str\"><f>{2}</f><v>{3}</v></c>", refc, cell.Style, Esc(cell.F), Esc(cell.S));
                            else
                                sb.AppendFormat(U.IC, "<c r=\"{0}\" s=\"{1}\"><f>{2}</f><v>{3}</v></c>", refc, cell.Style, Esc(cell.F), (double.IsNaN(cell.N) ? 0 : cell.N).ToString("R", U.IC));
                            break;
                        case CT.Empty:
                            if (cell.Style != 0) sb.AppendFormat("<c r=\"{0}\" s=\"{1}\"/>", refc, cell.Style);
                            break;
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");
            if (sh.Merges.Count > 0)
            {
                sb.AppendFormat("<mergeCells count=\"{0}\">", sh.Merges.Count);
                foreach (var m in sh.Merges) sb.AppendFormat("<mergeCell ref=\"{0}\"/>", m);
                sb.Append("</mergeCells>");
            }
            sb.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/></worksheet>");
            return sb.ToString();
        }
    }
}
