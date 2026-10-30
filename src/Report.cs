using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NbaQuant
{
    static partial class Report
    {
        static List<Sheet> Sheets = new List<Sheet>();
        static string R(int col, int row) { return Xlsx.ColName(col) + row; }
        static string RA(int col, int row) { return "$" + Xlsx.ColName(col) + "$" + row; }

        static Sheet New(string name, string title, string sub, params double[] widths)
        {
            var sh = new Sheet { Name = name };
            sh.Row().Add(Cell.Str(title, Styles.Title));
            if (sub != null) sh.Row().Add(Cell.Str(sub, Styles.Sub));
            for (int i = 0; i < widths.Length; i++) sh.Widths[i] = widths[i];
            Sheets.Add(sh);
            return sh;
        }
        static void Hdr(Sheet sh, params string[] cols) { var r = sh.Row(); foreach (var c in cols) r.Add(Cell.Str(c, Styles.Hdr)); }
        static void Section(Sheet sh, string text, int span = 1) { sh.Row(); var r = sh.Row(); r.Add(Cell.Str(text, Styles.Section)); for (int i = 1; i < span; i++) r.Add(Cell.Empty(Styles.Section)); }
        static void Note(Sheet sh, string text) { sh.Row().Add(Cell.Str(text, Styles.Sub)); }
        static Cell FairOddsFx(string pRef, double p) { return Cell.Fx("IF(" + pRef + ">=0.5,-100*" + pRef + "/(1-" + pRef + "),100*(1-" + pRef + ")/" + pRef + ")", U.FairAmerican(p), Styles.Odds); }
        static bool HasOdds(double o) { return !double.IsNaN(o) && Math.Abs(o) >= 100; }

        public static void Build(string path)
        {
            Sheets = new List<Sheet>();
            DumpText();
            Summary();
            BoxScoresSheet();
            RotationsSheet();
            SeasonBankSheet();
            StandoutsSheet();
            DdThreesSheet();
            Moneyline();
            GameModel();
            PropsVsBook();
            ThreeQuarters();
            PlayerDistributions();
            PlayerThresholds();
            EveryQuarter();
            FirstBasket();
            SlateLeaders();
            Sgp();
            Minutes();
            TrackRecord();
            Method();
            Tracking();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Xlsx.Save(path, Sheets);
            Program.Log("Saved report: " + path);
        }

        public class MlRow { public string Team, Opp; public bool Home; public GameCtx G; public double P, PModel, PMkt, DkMl, BestMl; public string BestBook; public int Conf; public string ConfNote; }
        public static List<MlRow> MlRows()
        {
            var rows = new List<MlRow>();
            foreach (var g in Program.Games)
            {
                string note; int conf = Analysis.GameConfidence(g, out note);
                double nvH = Program.NoVigHome(g.Def.DK);
                foreach (bool home in new[] { true, false })
                {
                    var books = (g.Def.Books ?? new List<MarketLine>()).Where(b => b.HasMl && b.Book != "Open" && b.Book != "Consensus").ToList();
                    if (books.Count == 0 && g.Def.DK != null && g.Def.DK.HasMl) books.Add(g.Def.DK);
                    var best = books.OrderByDescending(b => U.NetPayout(home ? b.MlH : b.MlA)).FirstOrDefault();
                    rows.Add(new MlRow
                    {
                        Team = home ? g.Def.Home : g.Def.Away, Opp = home ? g.Def.Away : g.Def.Home, Home = home, G = g,
                        P = home ? g.WinH : 1 - g.WinH, PModel = home ? g.ModelWinH : 1 - g.ModelWinH, PMkt = home ? nvH : 1 - nvH,
                        DkMl = g.Def.DK != null && g.Def.DK.HasMl ? (home ? g.Def.DK.MlH : g.Def.DK.MlA) : double.NaN,
                        BestMl = best != null ? (home ? best.MlH : best.MlA) : double.NaN, BestBook = best != null ? best.Book : "", Conf = conf, ConfNote = note
                    });
                }
            }
            return rows.OrderByDescending(r => r.P).ToList();
        }

        public class ThrRow { public PStat Ps; public string Market; public int Line; public double P; }
        public static List<ThrRow> AllThresholds()
        {
            var res = new List<ThrRow>();
            foreach (var ps in Analysis.Players.Where(p => p.MeanMin >= 15))
            {
                foreach (int t in Analysis.PtsT) res.Add(new ThrRow { Ps = ps, Market = "Points", Line = t, P = ps.Pts.PAtLeast(t) });
                foreach (int t in Analysis.RebT) res.Add(new ThrRow { Ps = ps, Market = "Rebounds", Line = t, P = ps.Reb.PAtLeast(t) });
                foreach (int t in Analysis.AstT) res.Add(new ThrRow { Ps = ps, Market = "Assists", Line = t, P = ps.Ast.PAtLeast(t) });
                foreach (int t in Analysis.TpmT) res.Add(new ThrRow { Ps = ps, Market = "Threes", Line = t, P = ps.Tpm.PAtLeast(t) });
                foreach (int t in Analysis.PraT) res.Add(new ThrRow { Ps = ps, Market = "PRA", Line = t, P = ps.Pra.PAtLeast(t) });
            }
            return res;
        }

        // Book prop lines matched to the simulation (only when the props feed is populated)
        public class PropEdge { public PStat Ps; public PropLine L; public double POver, EvOver, EvUnder; }
        public static List<PropEdge> PropEdges()
        {
            var res = new List<PropEdge>();
            foreach (var g in Program.Games)
                foreach (var l in g.Props)
                {
                    var ps = Analysis.Players.FirstOrDefault(p => p.G == g && U.Norm(p.P.Name) == U.Norm(l.Player));
                    if (ps == null || ps.P.Status == "OUT") continue;
                    var d = l.Stat == "PTS" ? ps.Pts : l.Stat == "REB" ? ps.Reb : l.Stat == "AST" ? ps.Ast : l.Stat == "3PM" ? ps.Tpm : ps.Pra;
                    int k = (int)Math.Floor(l.Line) + 1; // over 24.5 -> 25+
                    double po = d.PAtLeast(k);
                    if (l.Line == Math.Floor(l.Line)) { double push = d.PAtLeast((int)l.Line) - d.PAtLeast((int)l.Line + 1); po = po / Math.Max(1e-9, 1 - push); }
                    res.Add(new PropEdge { Ps = ps, L = l, POver = po, EvOver = HasOdds(l.OverOdds) ? U.EvPerDollar(po, l.OverOdds) : double.NaN, EvUnder = HasOdds(l.UnderOdds) ? U.EvPerDollar(1 - po, l.UnderOdds) : double.NaN });
                }
            return res;
        }

        static string GameLabel(GameCtx g) { return g.Def.Away + " @ " + g.Def.Home; }
        static string PName(PStat p) { return p.P.Name + " (" + p.P.Team + ")"; }

        static void DumpText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== MONEYLINE ===");
            foreach (var r in MlRows()) sb.AppendLine(string.Format(U.IC, "{0,-5} vs {1,-5} P={2:0.000} model={3:0.000} mkt={4:0.000} DK={5} EV@DK={6:+0.000;-0.000} best={7} {8} conf={9}", r.Team, r.Opp, r.P, r.PModel, r.PMkt, HasOdds(r.DkMl) ? U.Am(r.DkMl) : "n/a", HasOdds(r.DkMl) ? U.EvPerDollar(r.P, r.DkMl) : double.NaN, HasOdds(r.BestMl) ? U.Am(r.BestMl) : "n/a", r.BestBook, r.Conf));
            sb.AppendLine("=== TEAM Q1-Q3 ===");
            foreach (var t in Analysis.TeamQs.OrderByDescending(t => t.P100).Take(6)) sb.AppendLine(string.Format(U.IC, "{0,-5} mean {1:0.0} P100 {2:0.000}", t.Team, t.Mean, t.P100));
            sb.AppendLine("=== 25+ ===");
            foreach (var p in Analysis.Players.OrderByDescending(p => p.Pts.PAtLeast(25)).Take(8)) sb.AppendLine(string.Format(U.IC, "{0,-24} {1,-4} {2:0.000} mean {3:0.0}", p.P.Name, p.P.Team, p.Pts.PAtLeast(25), p.Pts.Mean));
            sb.AppendLine("=== BOOST SGP ===");
            foreach (var g in Program.Games) foreach (var c in Analysis.BoostSgp[g].Take(2)) sb.AppendLine(GameLabel(g) + ": " + string.Join(" + ", c.Legs.Select(l => l.Desc)) + string.Format(U.IC, " joint {0:0.000} lift {1:0.00}", c.Joint, c.Lift));
            var edges = PropEdges();
            if (edges.Count > 0)
            {
                sb.AppendLine("=== PROP EDGES (top 10 by EV) ===");
                foreach (var e in edges.OrderByDescending(x => Math.Max(double.IsNaN(x.EvOver) ? -9 : x.EvOver, double.IsNaN(x.EvUnder) ? -9 : x.EvUnder)).Take(10))
                    sb.AppendLine(string.Format(U.IC, "{0} {1} {2} {3}: P(over) {4:0.000} EVo {5:+0.000;-0.000} EVu {6:+0.000;-0.000}", e.Ps.P.Name, e.L.Stat, e.L.Line, e.L.Book, e.POver, e.EvOver, e.EvUnder));
            }
            File.WriteAllText(Program.P(@"data\last_results.txt"), sb.ToString());
        }
    }
}
