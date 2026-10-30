using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NbaQuant
{
    // Model audit: slices the walk-forward backtest and the live ledger to show where the simulation is reliable and where it breaks.
    static partial class Report
    {
        class PCtx
        {
            public string Team; public bool Home;
            public double Usage = double.NaN, ProjMin = double.NaN, ActMin = double.NaN, ProjPts = double.NaN, ActPts = double.NaN;
            public double TeamSpread = double.NaN, TeamMargin = double.NaN;
            public double SeasonPpg = double.NaN;   // points per game this season before the date (2+ games)
            public int PriorGames; public bool TeammateOut, B2B, Starter;
            public string GameId;
        }
        class GInfo { public string Home; public double SprH = double.NaN, Margin = double.NaN, MktTotal = double.NaN, Total = double.NaN, ModelMargin = double.NaN, ModelTotal = double.NaN; }
        class Seg
        {
            public string Group, Name; public int N; public double MeanP, Obs, Brier, ClimBrier;
            public double Skill { get { return ClimBrier > 0 ? 1 - Brier / ClimBrier : double.NaN; } }
        }
        class SegDef { public string Group, Name; public Func<PCtx, bool> F; public SegDef(string g, string n, Func<PCtx, bool> f) { Group = g; Name = n; F = f; } }
        class Src { public string Label; public List<Pred> Preds; public Dictionary<string, GInfo> G; public Dictionary<string, PCtx> C; public Dictionary<string, double> Clim; }

        static readonly string[] PropStats = { "PTS", "REB", "AST", "3PM", "PRA" };
        static bool Gradable(Pred p) { return !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.P); }
        static Cell ANum(double v, int st) { return double.IsNaN(v) || double.IsInfinity(v) ? Cell.Str("") : Cell.Num(v, st); }
        static string ClimKey(Pred p) { return p.Kind + "|" + p.Stat + "|" + p.Line.ToString("R", U.IC); }
        static PCtx CtxOf(Src s, Pred p) { PCtx c; return s.C.TryGetValue(p.Date + "|" + p.Pid, out c) ? c : null; }

        static Dictionary<string, GInfo> GameInfo(List<Pred> preds)
        {
            var res = new Dictionary<string, GInfo>();
            Func<string, GInfo> get = id => { GInfo g; if (!res.TryGetValue(id, out g)) res[id] = g = new GInfo(); return g; };
            foreach (var p in preds.Where(x => !string.IsNullOrEmpty(x.GameId)))
            {
                if (p.Kind == "MEAN" && p.Stat == "MARGIN") { var g = get(p.GameId); g.Home = p.Team; g.Margin = p.Actual; g.ModelMargin = p.Mean; }
                else if (p.Kind == "MEAN" && p.Stat == "TOTAL") { var g = get(p.GameId); g.Total = p.Actual; g.ModelTotal = p.Mean; }
                else if (p.Kind == "TOTAL" && p.Stat == "OVER") get(p.GameId).MktTotal = p.Line;
            }
            foreach (var p in preds.Where(x => x.Kind == "SPREAD"))
            { GInfo g; if (res.TryGetValue(p.GameId, out g) && p.Team == g.Home) g.SprH = p.Line; }
            return res;
        }

        // per player-game context: role, game script, rest, absences, sample size (box scores give everything but the projections)
        static Dictionary<string, PCtx> PlayerCtx(List<Pred> preds, Dictionary<string, GInfo> gi, List<GameData> done)
        {
            var byTeamDate = new Dictionary<string, GameData>();
            foreach (var g in done) { byTeamDate[g.Home + "|" + g.Date] = g; byTeamDate[g.Away + "|" + g.Date] = g; }
            var hist = new Dictionary<string, List<Tuple<string, double, string>>>();
            var ptsHist = new Dictionary<string, List<Tuple<string, int>>>();
            foreach (var g in done)
                foreach (var b in g.Box.Where(x => x.Played))
                {
                    List<Tuple<string, double, string>> l;
                    if (!hist.TryGetValue(b.Pid, out l)) hist[b.Pid] = l = new List<Tuple<string, double, string>>();
                    l.Add(Tuple.Create(g.Date, b.Min, b.Team));
                    List<Tuple<string, int>> lp;
                    if (!ptsHist.TryGetValue(b.Pid, out lp)) ptsHist[b.Pid] = lp = new List<Tuple<string, int>>();
                    lp.Add(Tuple.Create(g.Date, b.Pts));
                }
            var outCache = new Dictionary<string, bool>();
            Func<string, string, bool> regularOut = (team, date) =>
            {
                string k = team + "|" + date; bool v;
                if (outCache.TryGetValue(k, out v)) return v;
                GameData g; v = false;
                if (byTeamDate.TryGetValue(k, out g))
                {
                    var played = new HashSet<string>(g.Box.Where(b => b.Team == team && b.Played).Select(b => b.Pid));
                    foreach (var h in hist)
                    {
                        var prior = h.Value.Where(t => t.Item3 == team && string.CompareOrdinal(t.Item1, date) < 0).ToList();
                        if (prior.Count >= 2 && prior.Average(t => t.Item2) >= 24 && !played.Contains(h.Key)) { v = true; break; }
                    }
                }
                return outCache[k] = v;
            };
            var res = new Dictionary<string, PCtx>();
            foreach (var p in preds.Where(x => x.Kind == "MEAN" && !string.IsNullOrEmpty(x.Pid)))
            {
                string key = p.Date + "|" + p.Pid;
                PCtx c;
                if (!res.TryGetValue(key, out c))
                {
                    res[key] = c = new PCtx { Team = p.Team, Usage = p.Line, GameId = p.Date + "|" + p.GameId };
                    List<Tuple<string, int>> ph;
                    if (ptsHist.TryGetValue(p.Pid, out ph))
                    {
                        var before = ph.Where(t => string.CompareOrdinal(t.Item1, p.Date) < 0).ToList();
                        if (before.Count >= 2) c.SeasonPpg = before.Average(t => t.Item2);
                    }
                    GInfo g;
                    if (gi.TryGetValue(p.GameId, out g))
                    {
                        c.Home = p.Team == g.Home; c.TeamSpread = c.Home ? g.SprH : -g.SprH; c.TeamMargin = c.Home ? g.Margin : -g.Margin;
                    }
                    List<Tuple<string, double, string>> h;
                    c.PriorGames = hist.TryGetValue(p.Pid, out h) ? h.Count(t => string.CompareOrdinal(t.Item1, p.Date) < 0) : 0;
                    var prev = DateTime.ParseExact(p.Date, "yyyy-MM-dd", U.IC).AddDays(-1).ToString("yyyy-MM-dd");
                    c.B2B = byTeamDate.ContainsKey(p.Team + "|" + prev);
                    c.TeammateOut = regularOut(p.Team, p.Date);
                }
                if (p.Stat == "MIN") { c.ProjMin = p.Mean; c.ActMin = p.Actual; }
                else if (p.Stat == "PTS") { c.ProjPts = p.Mean; c.ActPts = p.Actual; }
            }
            // projected starters: top five projected minutes on each team in each game
            foreach (var grp in res.Values.Where(c => !double.IsNaN(c.ProjMin)).GroupBy(c => c.GameId + "|" + c.Team))
                foreach (var c in grp.OrderByDescending(x => x.ProjMin).Take(5)) c.Starter = true;
            return res;
        }

        static Src MakeSrc(string label, List<Pred> preds, List<GameData> done)
        {
            var s = new Src { Label = label, Preds = preds, G = GameInfo(preds) };
            s.C = PlayerCtx(preds, s.G, done);
            s.Clim = preds.Where(p => Gradable(p) && p.Kind != "MEAN" && p.Kind != "LEADER").GroupBy(ClimKey).ToDictionary(g => g.Key, g => g.Average(x => x.Y));
            return s;
        }

        static Seg Score(Src s, string group, string name, IEnumerable<Pred> rows)
        {
            var l = rows.ToList(); if (l.Count == 0) return null;
            var r = new Seg { Group = group, Name = name, N = l.Count, MeanP = l.Average(x => x.P), Obs = l.Average(x => x.Y), Brier = l.Average(x => (x.P - x.Y) * (x.P - x.Y)) };
            r.ClimBrier = l.Average(x => { double c = s.Clim[ClimKey(x)]; return (c - x.Y) * (c - x.Y); });
            return r;
        }

        static List<SegDef> SegDefs(Src s)
        {
            var us = s.C.Values.Where(c => c.ProjMin >= 15 && !double.IsNaN(c.Usage)).Select(c => c.Usage).OrderBy(x => x).ToList();
            double u33 = us.Count > 0 ? us[us.Count / 3] : 0, u67 = us.Count > 0 ? us[2 * us.Count / 3] : 0;
            return new List<SegDef>
            {
                new SegDef("Projected minutes", "32+ min", c => c.ProjMin >= 32),
                new SegDef("Projected minutes", "24-32 min", c => c.ProjMin >= 24 && c.ProjMin < 32),
                new SegDef("Projected minutes", "15-24 min", c => c.ProjMin >= 15 && c.ProjMin < 24),
                new SegDef("Projected minutes", "Under 15 min", c => c.ProjMin < 15),
                new SegDef("Role", "Projected starter (top 5 minutes)", c => c.Starter),
                new SegDef("Role", "Bench, 15+ projected min", c => !c.Starter && c.ProjMin >= 15),
                new SegDef("Role", "Bench, under 15 projected min", c => !c.Starter && c.ProjMin < 15),
                new SegDef("Projection vs season scoring (2+ games)", "Projected 3+ pts BELOW season avg (fading a hot start)", c => c.ProjPts <= c.SeasonPpg - 3),
                new SegDef("Projection vs season scoring (2+ games)", "Within 3 pts of season avg", c => Math.Abs(c.ProjPts - c.SeasonPpg) < 3),
                new SegDef("Projection vs season scoring (2+ games)", "Projected 3+ pts ABOVE season avg (expecting a jump)", c => c.ProjPts >= c.SeasonPpg + 3),
                new SegDef("Usage (shot volume per minute)", "Top third: primary scorers", c => c.ProjMin >= 15 && c.Usage >= u67),
                new SegDef("Usage (shot volume per minute)", "Middle third", c => c.ProjMin >= 15 && c.Usage >= u33 && c.Usage < u67),
                new SegDef("Usage (shot volume per minute)", "Bottom third: role players", c => c.ProjMin >= 15 && c.Usage < u33),
                new SegDef("Final margin (hindsight)", "Blowout: 20+ pts", c => Math.Abs(c.TeamMargin) >= 20),
                new SegDef("Final margin (hindsight)", "10-19 pts", c => Math.Abs(c.TeamMargin) >= 10 && Math.Abs(c.TeamMargin) < 20),
                new SegDef("Final margin (hindsight)", "Close: under 10", c => Math.Abs(c.TeamMargin) < 10),
                new SegDef("Pre-game spread", "Team favored by 9+", c => c.TeamSpread <= -9),
                new SegDef("Pre-game spread", "Team underdog by 9+", c => c.TeamSpread >= 9),
                new SegDef("Pre-game spread", "Within 8.5", c => Math.Abs(c.TeamSpread) < 9),
                new SegDef("Teammates", "A 24+ mpg teammate was out", c => c.TeammateOut),
                new SegDef("Teammates", "Full rotation available", c => !c.TeammateOut),
                new SegDef("Rest", "Team on a back-to-back", c => c.B2B),
                new SegDef("Rest", "Team rested", c => !c.B2B),
                new SegDef("Venue", "Home", c => c.Home),
                new SegDef("Venue", "Away", c => !c.Home),
                new SegDef("Season sample before the game", "0-1 games played", c => c.PriorGames <= 1),
                new SegDef("Season sample before the game", "2-4 games played", c => c.PriorGames >= 2 && c.PriorGames <= 4),
                new SegDef("Season sample before the game", "5+ games played", c => c.PriorGames >= 5),
                new SegDef("Minutes outcome (hindsight)", "Within 5 of projection", c => Math.Abs(c.ActMin - c.ProjMin) <= 5),
                new SegDef("Minutes outcome (hindsight)", "Played 6+ fewer than projected", c => c.ActMin <= c.ProjMin - 6),
                new SegDef("Minutes outcome (hindsight)", "Played 6+ more than projected", c => c.ActMin >= c.ProjMin + 6),
            };
        }

        static List<Pred> PropRows(Src s) { return s.Preds.Where(p => p.Kind == "PLAYER" && PropStats.Contains(p.Stat) && Gradable(p)).ToList(); }

        static List<Seg> PropSegments(Src s)
        {
            var rows = PropRows(s);
            var res = new List<Seg> { Score(s, "Overall", "All player thresholds (pts/reb/ast/3pm/pra)", rows) };
            foreach (var st in PropStats) res.Add(Score(s, "Stat", st, rows.Where(p => p.Stat == st)));
            foreach (var d in SegDefs(s)) res.Add(Score(s, d.Group, d.Name, rows.Where(p => { var c = CtxOf(s, p); return c != null && d.F(c); })));
            return res.Where(x => x != null).ToList();
        }

        static void SegHeader(Sheet sh) { Hdr(sh, "Slice", "Segment", "N", "Predicted", "Observed", "Obs - pred", "Brier", "Base-rate Brier", "Skill vs base rate"); }
        static void SegRow(Sheet sh, Seg s)
        {
            var r = sh.Row();
            r.Add(Cell.Str(s.Group)); r.Add(Cell.Str(s.Name)); r.Add(Cell.Num(s.N, Styles.Int)); r.Add(Cell.Num(s.MeanP, Styles.Pct)); r.Add(Cell.Num(s.Obs, Styles.Pct));
            r.Add(Cell.Num(s.Obs - s.MeanP, Math.Abs(s.Obs - s.MeanP) >= 0.02 && s.N >= 300 ? Styles.Bad : Styles.Pct));
            r.Add(Cell.Num(s.Brier, Styles.N2)); r.Add(Cell.Num(s.ClimBrier, Styles.N2)); r.Add(ANum(s.Skill, s.Skill >= 0.25 ? Styles.Good : s.Skill < 0.10 ? Styles.Bad : Styles.Pct));
        }

        // ---------- game markets ----------
        class Lean { public string Name; public int N; public double P, PMkt = double.NaN, Obs, Profit = double.NaN; }
        static Lean LeanOf(string name, List<Pred> l)
        {
            if (l.Count == 0) return new Lean { Name = name };
            var o = l.Where(x => !double.IsNaN(x.Odds) && Math.Abs(x.Odds) >= 100).ToList();
            var m = l.Where(x => !double.IsNaN(x.PMarket)).ToList();
            return new Lean { Name = name, N = l.Count, P = l.Average(x => x.P), PMkt = m.Count > 0 ? m.Average(x => x.PMarket) : double.NaN, Obs = l.Average(x => x.Y), Profit = o.Count == l.Count ? o.Sum(x => x.Y >= 1 ? U.NetPayout(x.Odds) : -1) : double.NaN };
        }

        static void GameMarkets(Sheet sh, Src s, StringBuilder txt)
        {
            Section(sh, s.Label + ": moneyline, spread and total", 9);
            var ml = s.Preds.Where(p => p.Kind == "ML" && Gradable(p)).ToList();
            var mlm = ml.Where(p => !double.IsNaN(p.PMarket)).ToList();
            Hdr(sh, "Market", "Measure", "N", "Model", "DraftKings no-vig", "Observed", "Flat $1 profit", "", "");
            Action<string, string, int, double, double, double, double, int> row = (a, b, n, x, y, z, pr, st) =>
            {
                var r = sh.Row(); r.Add(Cell.Str(a)); r.Add(Cell.Str(b)); r.Add(Cell.Num(n, Styles.Int)); r.Add(ANum(x, st)); r.Add(ANum(y, st)); r.Add(ANum(z, st)); r.Add(ANum(pr, Styles.N2));
            };
            if (mlm.Count > 0)
            {
                double bm = mlm.Average(x => (x.P - x.Y) * (x.P - x.Y)), bk = mlm.Average(x => (x.PMarket - x.Y) * (x.PMarket - x.Y));
                row("Moneyline", "Brier (lower is better)", mlm.Count / 2, bm, bk, double.NaN, double.NaN, Styles.N2);
                txt.AppendLine(string.Format(U.IC, "[{0}] Moneyline Brier model {1:0.0000} vs DK no-vig {2:0.0000} ({3} games)", s.Label, bm, bk, mlm.Count / 2));
                foreach (var b in new[] { Tuple.Create("Favorite 50-60%", 0.5, 0.6), Tuple.Create("Favorite 60-70%", 0.6, 0.7), Tuple.Create("Favorite 70%+", 0.7, 1.01) })
                {
                    var l = mlm.Where(x => x.PMarket >= b.Item2 && x.PMarket < b.Item3).ToList(); if (l.Count == 0) continue;
                    row("Moneyline", b.Item1 + ": win rate", l.Count, l.Average(x => x.P), l.Average(x => x.PMarket), l.Average(x => x.Y), double.NaN, Styles.Pct);
                }
                foreach (var t in new[] { 0.02, 0.04 })
                {
                    var lean = LeanOf("", mlm.Where(x => x.P - x.PMarket >= t).ToList());
                    if (lean.N == 0) continue;
                    row("Moneyline", string.Format(U.IC, "Model {0:0}+ pts above market (would bet)", 100 * t), lean.N, lean.P, lean.PMkt, lean.Obs, lean.Profit, Styles.Pct);
                    txt.AppendLine(string.Format(U.IC, "[{0}] ML sides where model >= market + {1:0}pp: n={2} model {3} market {4} won {5} profit {6:+0.00;-0.00}u", s.Label, 100 * t, lean.N, U.Pct(lean.P), U.Pct(lean.PMkt), U.Pct(lean.Obs), lean.Profit));
                }
            }
            foreach (var k in new[] { "SPREAD", "TOTAL" })
            {
                var l = s.Preds.Where(p => p.Kind == k && Gradable(p)).ToList(); if (l.Count == 0) continue;
                string nm = k == "SPREAD" ? "Spread" : "Total";
                row(nm, "Brier (0.250 = coin flip)", l.Count / 2, l.Average(x => (x.P - x.Y) * (x.P - x.Y)), 0.25, double.NaN, double.NaN, Styles.N2);
                foreach (var t in new[] { 0.53, 0.56 })
                {
                    var lean = LeanOf("", l.Where(x => x.P >= t).ToList()); if (lean.N == 0) continue;
                    row(nm, string.Format(U.IC, "Model side at {0:0}%+ (would bet)", 100 * t), lean.N, lean.P, double.NaN, lean.Obs, lean.Profit, Styles.Pct);
                    txt.AppendLine(string.Format(U.IC, "[{0}] {1} sides model >= {2:0}%: n={3} model {4} hit {5} profit {6:+0.00;-0.00}u", s.Label, nm, 100 * t, lean.N, U.Pct(lean.P), U.Pct(lean.Obs), lean.Profit));
                }
                if (k == "TOTAL")
                    foreach (var side in new[] { "OVER", "UNDER" })
                    {
                        var lean = LeanOf("", l.Where(x => x.Stat == side && x.P >= 0.53).ToList()); if (lean.N == 0) continue;
                        row(nm, "Model leans " + side.ToLowerInvariant() + " (53%+)", lean.N, lean.P, double.NaN, lean.Obs, lean.Profit, Styles.Pct);
                    }
            }
            var gs = s.G.Values.Where(g => !double.IsNaN(g.Margin) && !double.IsNaN(g.SprH)).ToList();
            if (gs.Count > 0)
            {
                Hdr(sh, "Game score", "Measure", "N", "Model", "DraftKings line", "Difference", "", "", "");
                double mm = gs.Average(g => Math.Abs(g.Margin - g.ModelMargin)), mk = gs.Average(g => Math.Abs(g.Margin + g.SprH));
                row("Margin", "Mean absolute error (pts)", gs.Count, mm, mk, mm - mk, double.NaN, Styles.N1);
                var gt = gs.Where(g => !double.IsNaN(g.MktTotal)).ToList();
                double tm = gt.Average(g => Math.Abs(g.Total - g.ModelTotal)), tk = gt.Average(g => Math.Abs(g.Total - g.MktTotal));
                row("Total", "Mean absolute error (pts)", gt.Count, tm, tk, tm - tk, double.NaN, Styles.N1);
                row("Total", "Bias: projected minus actual (pts)", gt.Count, gt.Average(g => g.ModelTotal - g.Total), gt.Average(g => g.MktTotal - g.Total), double.NaN, double.NaN, Styles.N1);
                row("Margin", "Games decided by 20+ (share)", gs.Count, double.NaN, double.NaN, gs.Count(g => Math.Abs(g.Margin) >= 20) / (double)gs.Count, double.NaN, Styles.Pct);
                txt.AppendLine(string.Format(U.IC, "[{0}] Margin MAE model {1:0.0} vs DK {2:0.0}; total MAE model {3:0.0} vs DK {4:0.0}; total bias model {5:+0.0;-0.0} DK {6:+0.0;-0.0}; 20+ blowouts {7}", s.Label, mm, mk, tm, tk, gt.Average(g => g.ModelTotal - g.Total), gt.Average(g => g.MktTotal - g.Total), U.Pct(gs.Count(g => Math.Abs(g.Margin) >= 20) / (double)gs.Count)));
            }
            var q3 = s.Preds.Where(p => p.Kind == "TEAMQ3" && Gradable(p)).ToList();
            if (q3.Count > 0)
            {
                Hdr(sh, "Team points through Q3", "Line", "N", "Predicted", "Observed", "Brier", "Base-rate Brier", "Skill", "");
                foreach (var g in q3.GroupBy(p => p.Line).OrderBy(g => g.Key))
                {
                    var sc = Score(s, "Q3", g.Key + "+", g);
                    var r = sh.Row(); r.Add(Cell.Str("Through Q3")); r.Add(Cell.Str(g.Key + "+")); r.Add(Cell.Num(sc.N, Styles.Int)); r.Add(Cell.Num(sc.MeanP, Styles.Pct)); r.Add(Cell.Num(sc.Obs, Styles.Pct));
                    r.Add(Cell.Num(sc.Brier, Styles.N2)); r.Add(Cell.Num(sc.ClimBrier, Styles.N2)); r.Add(ANum(sc.Skill, Styles.Pct));
                    txt.AppendLine(string.Format(U.IC, "[{0}] Q1-Q3 {1}+: n={2} pred {3} obs {4} skill {5:0.000}", s.Label, g.Key, sc.N, U.Pct(sc.MeanP), U.Pct(sc.Obs), sc.Skill));
                }
            }
        }

        // ---------- headline picks and parlays ----------
        static void PicksAndParlays(Sheet sh, Src s, StringBuilder txt)
        {
            Section(sh, s.Label + ": headline picks (tags)", 9);
            Hdr(sh, "Tag", "N", "Predicted", "Observed", "Hits", "Expected hits", "z (hits vs expected)", "Brier", "");
            foreach (var g in s.Preds.Where(p => !string.IsNullOrEmpty(p.Tag) && Gradable(p)).GroupBy(p => p.Tag).OrderBy(g => g.Key))
            {
                var l = g.ToList(); double hits = l.Sum(x => x.Y), exp = l.Sum(x => x.P), vr = l.Sum(x => x.P * (1 - x.P));
                double z = vr > 0 ? (hits - exp) / Math.Sqrt(vr) : double.NaN;
                var r = sh.Row(); r.Add(Cell.Str(g.Key)); r.Add(Cell.Num(l.Count, Styles.Int)); r.Add(Cell.Num(l.Average(x => x.P), Styles.Pct)); r.Add(Cell.Num(l.Average(x => x.Y), Styles.Pct));
                r.Add(Cell.Num(hits, Styles.N1)); r.Add(Cell.Num(exp, Styles.N1)); r.Add(ANum(z, Math.Abs(z) >= 1.5 ? (z > 0 ? Styles.Good : Styles.Bad) : Styles.N2)); r.Add(Cell.Num(l.Average(x => (x.P - x.Y) * (x.P - x.Y)), Styles.N2));
                txt.AppendLine(string.Format(U.IC, "[{0}] tag {1}: n={2} pred {3} obs {4} hits {5:0}/{6:0.0} expected z={7:+0.00;-0.00}", s.Label, g.Key, l.Count, U.Pct(l.Average(x => x.P)), U.Pct(l.Average(x => x.Y)), hits, exp, z));
            }
            var sgp = s.Preds.Where(p => p.Kind == "SGP" && Gradable(p) && !double.IsNaN(p.Mean)).ToList();
            if (sgp.Count == 0) return;
            Section(sh, s.Label + ": same-game parlays - does the correlation model help?", 9);
            Note(sh, "Joint = simulated probability all legs hit together. Independent = product of the single-leg probabilities (what a no-correlation model would say).");
            Hdr(sh, "Group", "N", "Joint (model)", "Independent", "Observed", "Brier joint", "Brier independent", "Joint better?", "");
            foreach (var g in new[] { Tuple.Create("All SGPs", (Func<Pred, bool>)(x => true)) }.Concat(sgp.Select(x => x.Tag).Distinct().OrderBy(t => t).Select(t => Tuple.Create(t, (Func<Pred, bool>)(x => x.Tag == t)))))
            {
                var l = sgp.Where(g.Item2).ToList(); if (l.Count == 0) continue;
                double bj = l.Average(x => (x.P - x.Y) * (x.P - x.Y)), bi = l.Average(x => (x.Mean - x.Y) * (x.Mean - x.Y));
                var r = sh.Row(); r.Add(Cell.Str(g.Item1)); r.Add(Cell.Num(l.Count, Styles.Int)); r.Add(Cell.Num(l.Average(x => x.P), Styles.Pct)); r.Add(Cell.Num(l.Average(x => x.Mean), Styles.Pct)); r.Add(Cell.Num(l.Average(x => x.Y), Styles.Pct));
                r.Add(Cell.Num(bj, Styles.N2)); r.Add(Cell.Num(bi, Styles.N2)); r.Add(Cell.Str(bj < bi ? "yes" : "no", bj < bi ? Styles.Good : Styles.Bad));
                txt.AppendLine(string.Format(U.IC, "[{0}] SGP {1}: n={2} joint {3} indep {4} obs {5}; Brier joint {6:0.0000} indep {7:0.0000}", s.Label, g.Item1, l.Count, U.Pct(l.Average(x => x.P)), U.Pct(l.Average(x => x.Mean)), U.Pct(l.Average(x => x.Y)), bj, bi));
            }
        }

        static string Cause(PCtx c)
        {
            if (c == null || double.IsNaN(c.ActMin)) return "unknown";
            if (c.ActMin <= c.ProjMin - 6) return "minutes short";
            if (c.ActMin >= c.ProjMin + 6) return "minutes surplus";
            if (Math.Abs(c.TeamMargin) >= 20) return "blowout";
            return "normal minutes";
        }

        static void Misses(Sheet sh, Src s, StringBuilder txt)
        {
            var rows = s.Preds.Where(p => p.Kind == "PLAYER" && Gradable(p) && (PropStats.Contains(p.Stat) || p.Stat == "DD")).ToList();
            Section(sh, s.Label + ": why confident calls fail (player props the model had at 80%+)", 9);
            Note(sh, "Cause is assigned in order: played 6+ min under projection; 6+ over; final margin 20+; otherwise normal minutes (shooting variance or rate error).");
            var conf = rows.Where(p => p.P >= 0.8).ToList();
            Hdr(sh, "Cause", "Misses", "Share of misses", "Hits", "Share of hits", "Miss rate in this cause", "", "", "");
            foreach (var cause in new[] { "minutes short", "minutes surplus", "blowout", "normal minutes", "unknown" })
            {
                var mi = conf.Where(p => p.Y < 1 && Cause(CtxOf(s, p)) == cause).Count(); var hi = conf.Where(p => p.Y >= 1 && Cause(CtxOf(s, p)) == cause).Count();
                int nm = conf.Count(p => p.Y < 1), nh = conf.Count(p => p.Y >= 1);
                if (mi + hi == 0) continue;
                var r = sh.Row(); r.Add(Cell.Str(cause)); r.Add(Cell.Num(mi, Styles.Int)); r.Add(ANum(nm > 0 ? mi / (double)nm : double.NaN, Styles.Pct)); r.Add(Cell.Num(hi, Styles.Int)); r.Add(ANum(nh > 0 ? hi / (double)nh : double.NaN, Styles.Pct)); r.Add(Cell.Num(mi / (double)(mi + hi), Styles.Pct));
                txt.AppendLine(string.Format(U.IC, "[{0}] 80%+ props, cause {1}: misses {2} ({3}) hits {4} ({5}); miss rate {6}", s.Label, cause, mi, U.Pct(nm > 0 ? mi / (double)nm : 0), hi, U.Pct(nh > 0 ? hi / (double)nh : 0), U.Pct(mi / (double)(mi + hi))));
            }
            Action<string, List<Pred>> list = (title, l) =>
            {
                Section(sh, title, 9);
                Hdr(sh, "Date", "Game", "Selection", "Model", "Actual", "Proj min", "Actual min", "Team margin", "Cause");
                foreach (var p in l)
                {
                    var c = CtxOf(s, p);
                    var r = sh.Row(); r.Add(Cell.Str(p.Date)); r.Add(Cell.Str(p.Game)); r.Add(Cell.Str(p.Selection)); r.Add(Cell.Num(p.P, Styles.Pct)); r.Add(ANum(p.Actual, Styles.Int));
                    r.Add(c == null ? Cell.Str("") : ANum(c.ProjMin, Styles.N1)); r.Add(c == null ? Cell.Str("") : ANum(c.ActMin, Styles.N1)); r.Add(c == null ? Cell.Str("") : ANum(c.TeamMargin, Styles.Int)); r.Add(Cell.Str(Cause(c)));
                }
            };
            // one row per player-game: the most confident miss / least likely hit
            list(s.Label + ": most confident misses", rows.Where(p => p.Y < 1 && p.P >= 0.85).GroupBy(p => p.Date + p.Pid).Select(g => g.OrderByDescending(x => x.P).First()).OrderByDescending(p => p.P).Take(25).ToList());
            list(s.Label + ": biggest surprise hits", rows.Where(p => p.Y >= 1 && p.P <= 0.10).GroupBy(p => p.Date + p.Pid).Select(g => g.OrderBy(x => x.P).First()).OrderBy(p => p.P).Take(20).ToList());
        }

        static void Projections(Sheet sh, Src s, StringBuilder txt)
        {
            Section(sh, s.Label + ": projected vs actual points and minutes by segment", 9);
            Note(sh, "Bias = projected minus actual (negative = the model was too low). MAE = mean absolute error.");
            Hdr(sh, "Slice", "Segment", "N", "Proj pts", "Actual pts", "Pts bias", "Pts MAE", "Min bias", "Min MAE");
            var all = s.C.Values.Where(c => !double.IsNaN(c.ProjPts) && !double.IsNaN(c.ActPts) && !double.IsNaN(c.ActMin)).ToList();
            foreach (var d in new[] { new SegDef("Overall", "All projected players", c => true) }.Concat(SegDefs(s)))
            {
                var l = all.Where(d.F).ToList(); if (l.Count < 10) continue;
                var r = sh.Row(); r.Add(Cell.Str(d.Group)); r.Add(Cell.Str(d.Name)); r.Add(Cell.Num(l.Count, Styles.Int)); r.Add(Cell.Num(l.Average(c => c.ProjPts), Styles.N1)); r.Add(Cell.Num(l.Average(c => c.ActPts), Styles.N1));
                double pb = l.Average(c => c.ProjPts - c.ActPts), mb = l.Average(c => c.ProjMin - c.ActMin);
                r.Add(Cell.Num(pb, Math.Abs(pb) >= 1 ? Styles.Bad : Styles.N2)); r.Add(Cell.Num(l.Average(c => Math.Abs(c.ProjPts - c.ActPts)), Styles.N2)); r.Add(Cell.Num(mb, Math.Abs(mb) >= 1.5 ? Styles.Bad : Styles.N2)); r.Add(Cell.Num(l.Average(c => Math.Abs(c.ProjMin - c.ActMin)), Styles.N2));
                txt.AppendLine(string.Format(U.IC, "[{0}] means {1} / {2}: n={3} pts bias {4:+0.00;-0.00} MAE {5:0.00}; min bias {6:+0.00;-0.00} MAE {7:0.00}", s.Label, d.Group, d.Name, l.Count, pb, l.Average(c => Math.Abs(c.ProjPts - c.ActPts)), mb, l.Average(c => Math.Abs(c.ProjMin - c.ActMin))));
            }
        }

        static void LinesAndReliability(Sheet sh, Src s, StringBuilder txt)
        {
            var rows = s.Preds.Where(p => p.Kind == "PLAYER" && Gradable(p)).ToList();
            Section(sh, s.Label + ": every prop line", 9);
            Hdr(sh, "Stat", "Line", "N", "Predicted", "Observed", "Obs - pred", "Brier", "Base-rate Brier", "Skill");
            foreach (var g in rows.GroupBy(p => p.Stat + "|" + p.Line.ToString("R", U.IC)).OrderBy(g => g.First().Stat).ThenBy(g => g.First().Line))
            {
                var f = g.First(); var sc = Score(s, f.Stat, f.Line > 0 ? f.Line + "+" : "yes", g);
                var r = sh.Row(); r.Add(Cell.Str(f.Stat)); r.Add(Cell.Str(sc.Name)); r.Add(Cell.Num(sc.N, Styles.Int)); r.Add(Cell.Num(sc.MeanP, Styles.Pct)); r.Add(Cell.Num(sc.Obs, Styles.Pct));
                r.Add(Cell.Num(sc.Obs - sc.MeanP, Math.Abs(sc.Obs - sc.MeanP) >= 0.02 && sc.N >= 300 ? Styles.Bad : Styles.Pct)); r.Add(Cell.Num(sc.Brier, Styles.N2)); r.Add(Cell.Num(sc.ClimBrier, Styles.N2)); r.Add(ANum(sc.Skill, Styles.Pct));
                txt.AppendLine(string.Format(U.IC, "[{0}] line {1} {2}: n={3} pred {4} obs {5} skill {6:0.000}", s.Label, f.Stat, sc.Name, sc.N, U.Pct(sc.MeanP), U.Pct(sc.Obs), sc.Skill));
            }
            Section(sh, s.Label + ": reliability (predicted bin -> observed)", 9);
            var stats = new[] { "PTS", "REB", "AST", "3PM", "PRA", "DD" };
            Hdr(sh, new[] { "Bin" }.Concat(stats).ToArray());
            for (int b = 0; b < 10; b++)
            {
                double lo = b / 10.0, hi = (b + 1) / 10.0;
                var r = sh.Row(); r.Add(Cell.Str(string.Format(U.IC, "{0:0}-{1:0}%", 100 * lo, 100 * hi)));
                var parts = new List<string>();
                foreach (var st in stats)
                {
                    var l = rows.Where(p => p.Stat == st && p.P >= lo && (p.P < hi || (b == 9 && p.P <= 1))).ToList();
                    r.Add(l.Count < 20 ? Cell.Str(l.Count == 0 ? "" : "n<20") : Cell.Str(string.Format(U.IC, "{0} -> {1} (n={2})", U.Pct(l.Average(x => x.P)), U.Pct(l.Average(x => x.Y)), l.Count)));
                    if (l.Count >= 20) parts.Add(string.Format(U.IC, "{0} {1}->{2}", st, U.Pct(l.Average(x => x.P)), U.Pct(l.Average(x => x.Y))));
                }
                txt.AppendLine(string.Format(U.IC, "[{0}] reliability {1:0}-{2:0}%: {3}", s.Label, 100 * lo, 100 * hi, string.Join("; ", parts)));
            }
            Section(sh, s.Label + ": other player markets", 9);
            SegHeader(sh);
            foreach (var st in new[] { "DD", "TD", "Q3ALL", "FFG" })
            {
                var sc = Score(s, "Market", st == "Q3ALL" ? "3+ points every quarter" : st == "FFG" ? "First field goal" : st == "DD" ? "Double-double" : "Triple-double", rows.Where(p => p.Stat == st));
                if (sc == null) continue;
                SegRow(sh, sc);
                txt.AppendLine(string.Format(U.IC, "[{0}] {1}: n={2} pred {3} obs {4} Brier {5:0.0000} base {6:0.0000} skill {7:0.000}", s.Label, sc.Name, sc.N, U.Pct(sc.MeanP), U.Pct(sc.Obs), sc.Brier, sc.ClimBrier, sc.Skill));
            }
        }

        // plain-language findings derived from the tables (strongest/weakest segments, calibration gaps, failure causes)
        static void Verdict(Sheet sh, Src bt, Src live, StringBuilder txt)
        {
            var segs = PropSegments(bt);
            var overall = segs[0];
            var lines = new List<string>();
            lines.Add(string.Format(U.IC, "Walk-forward backtest: {0} games, {1:N0} graded player-prop thresholds. Overall skill vs base rates {2:0.0%} (predicted {3}, observed {4}).",
                bt.G.Count(g => !double.IsNaN(g.Value.Margin)), overall.N, overall.Skill, U.Pct(overall.MeanP), U.Pct(overall.Obs)));
            var big = segs.Where(x => x.N >= 300 && x.Group != "Overall" && !x.Group.Contains("hindsight")).ToList();
            lines.Add("Strongest slices (highest skill): " + string.Join("; ", big.OrderByDescending(x => x.Skill).Take(5).Select(x => string.Format(U.IC, "{0} / {1} {2:0.0%} (n={3})", x.Group, x.Name, x.Skill, x.N))));
            lines.Add("Weakest slices (lowest skill): " + string.Join("; ", big.OrderBy(x => x.Skill).Take(5).Select(x => string.Format(U.IC, "{0} / {1} {2:0.0%} (n={3})", x.Group, x.Name, x.Skill, x.N))));
            var gaps = segs.Where(x => x.N >= 300 && Math.Abs(x.Obs - x.MeanP) >= 0.015).OrderByDescending(x => Math.Abs(x.Obs - x.MeanP)).Take(6).ToList();
            if (gaps.Count > 0) lines.Add("Calibration gaps (observed minus predicted): " + string.Join("; ", gaps.Select(x => string.Format(U.IC, "{0} / {1} {2:+0.0;-0.0} pts (n={3})", x.Group, x.Name, 100 * (x.Obs - x.MeanP), x.N))));
            var conf = PropRows(bt).Where(p => p.P >= 0.8).ToList();
            if (conf.Count > 0)
            {
                var miss = conf.Where(p => p.Y < 1).ToList();
                double shortM = miss.Count(p => Cause(CtxOf(bt, p)) == "minutes short") / (double)Math.Max(1, miss.Count);
                double shortH = conf.Where(p => p.Y >= 1).Count(p => Cause(CtxOf(bt, p)) == "minutes short") / (double)Math.Max(1, conf.Count(p => p.Y >= 1));
                lines.Add(string.Format(U.IC, "Confident props (80%+): {0:N0} calls, hit {1}. {2} of the misses came with the player logging 6+ fewer minutes than projected, vs {3} of the hits: minutes are the main failure mode, not the shooting model.", conf.Count, U.Pct(conf.Average(p => p.Y)), U.Pct(shortM), U.Pct(shortH)));
            }
            foreach (var l in lines) txt.AppendLine("VERDICT: " + l);
            foreach (var l in lines) { var r = sh.Row(); r.Add(Cell.Str(l, Styles.Wrap)); sh.Merges.Add("A" + sh.Rows.Count + ":I" + sh.Rows.Count); }
            if (live != null)
            {
                var ls = PropSegments(live);
                var r = sh.Row(); r.Add(Cell.Str(string.Format(U.IC, "Live published slates ({0}): {1:N0} graded player-prop thresholds, skill {2:0.0%} (predicted {3}, observed {4}). Live runs face real injury uncertainty; the backtest uses who actually dressed.",
                    string.Join(", ", live.Preds.Select(p => p.Date).Distinct().OrderBy(d => d)), ls[0].N, ls[0].Skill, U.Pct(ls[0].MeanP), U.Pct(ls[0].Obs)), Styles.Wrap));
                sh.Merges.Add("A" + sh.Rows.Count + ":I" + sh.Rows.Count);
            }
        }

        public static string BuildAudit(string path, List<Pred> backtest, List<Pred> live, List<GameData> done)
        {
            Sheets = new List<Sheet>();
            var txt = new StringBuilder();
            var bt = MakeSrc("Backtest", backtest, done);
            var lv = live.Count > 0 ? MakeSrc("Live", live, done) : null;
            var dates = backtest.Select(p => p.Date).Distinct().OrderBy(d => d).ToList();
            var v = New("Verdict", "Model audit: where the simulation works and where it fails",
                string.Format(U.IC, "Walk-forward backtest {0} to {1} (each date simulated using only earlier games) plus the graded live slates. Skill = 1 - Brier / Brier of the base rate for the same line (0% = no better than always guessing the average; higher is better).", dates.FirstOrDefault(), dates.LastOrDefault()),
                24, 34, 9, 11, 11, 11, 11, 13, 13);
            Verdict(v, bt, lv, txt);
            var gm = New("Game markets", "Moneyline, spread, total and Q1-Q3 versus DraftKings", "Profit is flat $1 per side at the DraftKings price recorded with the prediction (closing line in the backtest).", 18, 36, 8, 11, 14, 11, 13, 10, 8);
            GameMarkets(gm, bt, txt); if (lv != null) GameMarkets(gm, lv, txt);
            var pp = New("Player props", "Player-prop accuracy by segment", "Every points/rebounds/assists/threes/PRA threshold the model priced, split by role, game script, rest, absences and sample size. Red = calibration gap of 2+ pts or skill under 10%; green = skill 25%+.", 30, 32, 9, 11, 11, 11, 10, 14, 14);
            Section(pp, "Backtest", 9); SegHeader(pp); foreach (var s in PropSegments(bt)) { SegRow(pp, s); txt.AppendLine(string.Format(U.IC, "[Backtest] seg {0} / {1}: n={2} pred {3} obs {4} Brier {5:0.0000} skill {6:0.000}", s.Group, s.Name, s.N, U.Pct(s.MeanP), U.Pct(s.Obs), s.Brier, s.Skill)); }
            if (lv != null) { Section(pp, "Live published slates", 9); SegHeader(pp); foreach (var s in PropSegments(lv)) { SegRow(pp, s); txt.AppendLine(string.Format(U.IC, "[Live] seg {0} / {1}: n={2} pred {3} obs {4} Brier {5:0.0000} skill {6:0.000}", s.Group, s.Name, s.N, U.Pct(s.MeanP), U.Pct(s.Obs), s.Brier, s.Skill)); } }
            var lr = New("Lines & reliability", "Calibration line by line", null, 10, 10, 22, 22, 22, 22, 22, 22, 12);
            LinesAndReliability(lr, bt, txt);
            var pj = New("Projections", "Projected vs actual box-score lines", null, 30, 32, 9, 10, 10, 10, 10, 10, 10);
            Projections(pj, bt, txt);
            var pk = New("Picks & parlays", "Headline picks and same-game parlays", "z = (hits - expected hits) / SD; beyond +/-1.5 is worth noticing, beyond +/-2 is unlikely to be luck.", 24, 9, 12, 12, 12, 13, 16, 12, 10);
            PicksAndParlays(pk, bt, txt); if (lv != null) PicksAndParlays(pk, lv, txt);
            var ms = New("Misses", "Where confident calls broke", null, 11, 14, 46, 9, 9, 9, 10, 11, 16);
            Misses(ms, bt, txt); if (lv != null) Misses(ms, lv, txt);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Xlsx.Save(path, Sheets);
            File.WriteAllText(Program.P(@"ledger\audit_latest.txt"), txt.ToString());
            return txt.ToString();
        }
    }
}
