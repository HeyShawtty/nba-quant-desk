using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NbaQuant
{
    // Graded predictions from live (published) slates.
    static class Ledger
    {
        public static List<Pred> LoadAll()
        {
            var dir = Program.P("ledger");
            if (!Directory.Exists(dir)) return new List<Pred>();
            return Directory.GetFiles(dir, "graded_*.csv").OrderBy(f => f).SelectMany(Pred.Load).ToList();
        }

        public static string Summary()
        {
            var all = LoadAll();
            if (all.Count == 0) return "No graded live slates yet.";
            var dates = all.Select(p => p.Date).Distinct().Count();
            var ml = all.Where(p => p.Kind == "ML" && !p.Void && !double.IsNaN(p.Y)).ToList();
            var top = all.Where(p => p.Tag == "top3_ml" && !p.Void).ToList();
            var sgp = all.Where(p => p.Tag == "best_sgp" && !p.Void).ToList();
            string s = string.Format(U.IC, "Live track record: {0} graded slate(s).", dates);
            if (ml.Count > 0)
            {
                double b = ml.Average(x => (x.P - x.Y) * (x.P - x.Y));
                var mk = ml.Where(x => !double.IsNaN(x.PMarket)).ToList();
                s += string.Format(U.IC, " Moneyline Brier {0:0.000}{1}.", b, mk.Count == ml.Count ? string.Format(U.IC, " vs market {0:0.000}", mk.Average(x => (x.PMarket - x.Y) * (x.PMarket - x.Y))) : "");
            }
            if (top.Count > 0) s += string.Format(U.IC, " Top-3 ML picks {0}/{1}.", top.Count(x => x.Y >= 1), top.Count);
            if (sgp.Count > 0) s += string.Format(U.IC, " Best boost SGP {0}/{1}.", sgp.Count(x => x.Y >= 1), sgp.Count);
            return s;
        }

        public static string BiasNote()
        {
            var bt = Program.P(@"ledger\backtest_latest.csv");
            var src = File.Exists(bt) ? Pred.Load(bt) : LoadAll();
            var pl = src.Where(p => p.Kind == "PLAYER" && !p.Void && !double.IsNaN(p.Y) && (p.Stat == "PTS" || p.Stat == "REB" || p.Stat == "AST" || p.Stat == "3PM" || p.Stat == "PRA")).ToList();
            var means = src.Where(p => p.Kind == "MEAN" && p.Stat == "PTS" && !p.Void && !double.IsNaN(p.Actual)).ToList();
            if (pl.Count < 200) return "Player-prop calibration: not enough graded games yet to measure bias.";
            double mp = pl.Average(x => x.P), hr = pl.Average(x => x.Y);
            string s = string.Format(U.IC, "Player-prop calibration ({0}, {1:N0} threshold predictions): average predicted {2} vs observed {3}", File.Exists(bt) ? "walk-forward backtest" : "live ledger", pl.Count, U.Pct(mp), U.Pct(hr));
            if (means.Count > 0) s += string.Format(U.IC, "; projected points averaged {0:+0.00;-0.00} vs actual per player", means.Average(x => x.Mean - x.Actual));
            var stars = means.Where(x => x.Line >= 0.62).ToList();
            double starBias = stars.Count >= 30 ? stars.Average(x => x.Mean - x.Actual) : 0;
            if (stars.Count >= 30) s += string.Format(U.IC, " (high-usage players: {0:+0.00;-0.00}, n={1})", starBias, stars.Count);
            s += Math.Abs(hr - mp) > 0.02 || Math.Abs(starBias) > 1.0 ? ". The gap is large enough to matter: treat prop probabilities with caution and re-check after more games." : ". No material bias at this sample size, but the sample is still small.";
            return s;
        }
    }

    // Headline lists (top 5 of ~150 players by probability) carry a winner's-curse bias: the ranked picks hit less often than
    // their stated odds (25+ list: 42% vs 54% in the walk-forward backtest, 29% vs 50% live), while the same probabilities
    // are well calibrated across all players. Factor = (hits + 10) / (expected + 10) for the list's tag in the backtest,
    // shrunk toward 1 and applied to the displayed probability. The live record is the held-out check.
    static class TrackAdjust
    {
        static Dictionary<string, Tuple<double, int>> f;
        public static Tuple<double, int> Of(string tag)
        {
            if (f == null)
            {
                f = new Dictionary<string, Tuple<double, int>>();
                string bt = Program.P(@"ledger\backtest_latest.csv");
                if (File.Exists(bt))
                    foreach (var g in Pred.Load(bt).Where(p => !string.IsNullOrEmpty(p.Tag) && (p.Tag.StartsWith("top") || p.Tag == "erupt" || p.Tag == "dud") && !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.P)).GroupBy(p => p.Tag))
                    {
                        double hits = g.Sum(x => x.Y), exp = g.Sum(x => x.P);
                        f[g.Key] = Tuple.Create(Math.Max(0.5, Math.Min(1.2, (hits + 10) / (exp + 10))), g.Count());
                    }
            }
            Tuple<double, int> v; return f.TryGetValue(tag, out v) ? v : Tuple.Create(1.0, 0);
        }
        public static double Adj(string tag, double p) { return Math.Min(0.99, p * Of(tag).Item1); }
    }

    class GameRec { public string Date, Game; public double MktMargin, ModelMargin, MktNoVig, Blend; public int HomeWon; public double Margin, Total, MktTotal; }

    static class Walk
    {
        public static List<GameRec> GameRecs = new List<GameRec>();

        public static List<Pred> Run(DateTime from, DateTime to, int nsim, Action<string> log, bool sgp = false)
        {
            var all = new List<Pred>(); GameRecs = new List<GameRec>();
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            {
                Program.BuildSlate(d, false, true);
                if (Program.Games.Count == 0) continue;
                Program.Simulate(nsim, true, 8000);
                Analysis.Run(sgp);
                string ds = d.ToString("yyyy-MM-dd");
                var preds = Predictions.Collect(ds);
                var byId = Program.Games.ToDictionary(g => g.Def.EspnId, g => g.Info);
                Grader.Grade(preds, id => { GameData g; return id != null && byId.TryGetValue(id, out g) ? g : null; });
                all.AddRange(preds);
                foreach (var g in Program.Games.Where(x => x.HasMarket))
                    GameRecs.Add(new GameRec
                    {
                        Date = ds, Game = g.Def.Away + "@" + g.Def.Home, MktMargin = -g.Def.DK.SprH, ModelMargin = g.ModelMeanMargin, MktNoVig = Program.NoVigHome(g.Def.DK), Blend = g.WinH,
                        HomeWon = g.Info.HomePts > g.Info.AwayPts ? 1 : 0, Margin = g.Info.HomePts - g.Info.AwayPts, Total = g.Info.HomePts + g.Info.AwayPts, MktTotal = g.Def.DK.Total
                    });
                log(string.Format(U.IC, "  {0}: {1} games simulated and graded", ds, Program.Games.Count));
            }
            return all;
        }

        // Fits extra minutes per unit of usage on the earlier 60% of dates, halves it (shrinkage), and adopts it only
        // if it lowers minutes error on the later 40%. Residuals are relative to the slope already in use.
        static string FitMinutesSlope(List<Pred> preds)
        {
            var S = Program.S;
            var rows = preds.Where(p => p.Kind == "MEAN" && p.Stat == "MIN" && !p.Void && !double.IsNaN(p.Actual) && p.Mean >= 12).ToList();
            var dates = rows.Select(r => r.Date).Distinct().OrderBy(d => d).ToList();
            if (dates.Count < 4 || rows.Count < 300) return "Minutes-usage slope: not enough games to fit; kept " + S.MinUsageSlope.ToString("0.0", U.IC) + ".";
            string cut = dates[(int)(dates.Count * 0.6)];
            var train = rows.Where(r => string.CompareOrdinal(r.Date, cut) < 0).ToList();
            var test = rows.Where(r => string.CompareOrdinal(r.Date, cut) >= 0).ToList();
            double mx = train.Average(r => r.Line - 0.45), my = train.Average(r => r.Actual - r.Mean);
            double sxy = train.Sum(r => (r.Line - 0.45 - mx) * (r.Actual - r.Mean - my)), sxx = train.Sum(r => (r.Line - 0.45 - mx) * (r.Line - 0.45 - mx));
            double resid = sxx > 0 ? sxy / sxx : 0;
            double delta = 0.5 * resid;
            Func<double, double> mse = dd => test.Average(r => { double a = r.Mean + dd * (r.Line - 0.45); return (r.Actual - a) * (r.Actual - a); });
            double before = mse(0), after = mse(delta);
            string note = string.Format(U.IC, "Minutes-usage slope: residual slope {0:0.0} on {1} player-games before {2}; half-strength change {3:+0.0;-0.0} tested on {4} later player-games: minutes MSE {5:0.00} -> {6:0.00}.", resid, train.Count, cut, delta, test.Count, before, after);
            if (after < before) { S.MinUsageSlope = Math.Round(S.MinUsageSlope + delta, 1); note += " Adopted: slope now " + S.MinUsageSlope.ToString("0.0", U.IC) + "."; }
            else note += " Not adopted: slope stays " + S.MinUsageSlope.ToString("0.0", U.IC) + ".";
            return note;
        }

        // Logit-linear map for double-double probabilities: fit on the earlier 60% of dates, keep only if it helps the rest.
        static string FitDdCalibration(List<Pred> preds)
        {
            var S = Program.S;
            var rows = preds.Where(p => p.Kind == "PLAYER" && p.Stat == "DD" && !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.Mean)).ToList();
            var dates = rows.Select(r => r.Date).Distinct().OrderBy(d => d).ToList();
            if (dates.Count < 4 || rows.Count < 400) return "Double-double calibration: not enough games; kept current map.";
            string cut = dates[(int)(dates.Count * 0.6)];
            var train = rows.Where(r => string.CompareOrdinal(r.Date, cut) < 0).ToList(); var test = rows.Where(r => string.CompareOrdinal(r.Date, cut) >= 0).ToList();
            Func<double, double, double, double> map = (p, a, b) => { double q = Math.Min(0.999, Math.Max(0.001, p)); return 1 / (1 + Math.Exp(-(a + b * Math.Log(q / (1 - q))))); };
            double bestA = 0, bestB = 1, bestLl = double.MaxValue;
            // 2026-10-30: widened after the audit showed DD odds too squashed (25-40% calls hit ~20%, 55%+ calls hit ~80%)
            for (double a = -0.8; a <= 0.81; a += 0.1)
                for (double b = 0.6; b <= 1.81; b += 0.1)
                {
                    double ll = train.Sum(r => { double pp = map(r.Mean, a, b); return -(r.Y * Math.Log(pp) + (1 - r.Y) * Math.Log(1 - pp)); });
                    if (ll < bestLl) { bestLl = ll; bestA = a; bestB = b; }
                }
            double raw = test.Average(r => (r.Mean - r.Y) * (r.Mean - r.Y)), cur = test.Average(r => { double pp = map(r.Mean, S.DdLogitShift, S.DdLogitSlope); return (pp - r.Y) * (pp - r.Y); }), fit = test.Average(r => { double pp = map(r.Mean, bestA, bestB); return (pp - r.Y) * (pp - r.Y); });
            string note = string.Format(U.IC, "Double-double calibration: fitted shift {0:0.0}, slope {1:0.0} on {2} player-games; held-out Brier raw {3:0.00000}, current {4:0.00000}, fitted {5:0.00000}.", bestA, bestB, train.Count, raw, cur, fit);
            if (fit < cur) { S.DdLogitShift = Math.Round(bestA, 1); S.DdLogitSlope = Math.Round(bestB, 1); note += " Adopted."; } else note += " Kept current.";
            return note;
        }

        // Per-stat logit map for player thresholds (p -> logistic(a + b*logit p)), fitted on the earlier 60% of dates and kept only
        // for stats where it lowers Brier on the later 40%. The audit found 30-90% predictions running 2-3 pts high.
        public static string FitPropCalibration(List<Pred> preds)
        {
            var S = Program.S;
            var rows = preds.Where(p => p.Kind == "PLAYER" && !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.P) && p.P > 0 && p.P < 1).ToList();
            var dates = rows.Select(r => r.Date).Distinct().OrderBy(d => d).ToList();
            if (dates.Count < 4) return "Prop calibration: not enough dates.";
            string cut = dates[(int)(dates.Count * 0.6)];
            var maps = new List<string>(); var notes = new List<string>();
            foreach (var st in new[] { "PTS", "REB", "AST", "3PM", "PRA" })
            {
                // the rows hold calibrated P if a map was in use; undo it so the refit starts from raw simulation shares
                var cur = Analysis.PropMap(st);
                Func<double, double> raw = p => Analysis.Uncal(p, cur.Item1, cur.Item2);
                var train = rows.Where(r => r.Stat == st && string.CompareOrdinal(r.Date, cut) < 0).ToList();
                var test = rows.Where(r => r.Stat == st && string.CompareOrdinal(r.Date, cut) >= 0).ToList();
                if (train.Count < 500 || test.Count < 300) continue;
                double bestA = 0, bestB = 1, bestLl = double.MaxValue;
                for (double a = -0.4; a <= 0.401; a += 0.05)
                    for (double b = 0.7; b <= 1.201; b += 0.05)
                    {
                        double ll = train.Sum(r => { double pp = Math.Min(0.999, Math.Max(0.001, Analysis.Cal(raw(r.P), a, b))); return -(r.Y * Math.Log(pp) + (1 - r.Y) * Math.Log(1 - pp)); });
                        if (ll < bestLl) { bestLl = ll; bestA = a; bestB = b; }
                    }
                double b0 = test.Average(r => { double p = raw(r.P); return (p - r.Y) * (p - r.Y); });
                double b1 = test.Average(r => { double p = Analysis.Cal(raw(r.P), bestA, bestB); return (p - r.Y) * (p - r.Y); });
                bool keep = b1 < b0 * 0.998;
                notes.Add(string.Format(U.IC, "{0}: shift {1:+0.00;-0.00} slope {2:0.00}, held-out Brier {3:0.00000} -> {4:0.00000}{5}", st, bestA, bestB, b0, b1, keep ? " adopted" : " not adopted"));
                if (keep) maps.Add(string.Format(U.IC, "{0}:{1:0.00}:{2:0.00}", st, bestA, bestB));
            }
            S.PropCal = string.Join(";", maps);
            return "Player-threshold calibration (fit before " + cut + ", tested after): " + string.Join("; ", notes) + ".";
        }

        // Brier by market on all dates and on the earlier / later halves (a change should help on both to be trusted)
        public static string Metrics(List<Pred> preds)
        {
            var dates = preds.Select(p => p.Date).Distinct().OrderBy(d => d).ToList();
            string cut = dates.Count > 1 ? dates[dates.Count / 2] : "";
            Func<IEnumerable<Pred>, string> br = xs => { var l = xs.Where(p => !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.P)).ToList(); return l.Count == 0 ? "n/a" : l.Average(x => (x.P - x.Y) * (x.P - x.Y)).ToString("0.00000", U.IC); };
            var groups = new List<Tuple<string, Func<Pred, bool>>>
            {
                Tuple.Create("PROPS", (Func<Pred, bool>)(p => p.Kind == "PLAYER" && (p.Stat == "PTS" || p.Stat == "REB" || p.Stat == "AST" || p.Stat == "3PM" || p.Stat == "PRA"))),
                Tuple.Create("ML", (Func<Pred, bool>)(p => p.Kind == "ML")), Tuple.Create("SPREAD", (Func<Pred, bool>)(p => p.Kind == "SPREAD")), Tuple.Create("TOTAL", (Func<Pred, bool>)(p => p.Kind == "TOTAL")),
                Tuple.Create("TEAMQ3", (Func<Pred, bool>)(p => p.Kind == "TEAMQ3")), Tuple.Create("SGP", (Func<Pred, bool>)(p => p.Kind == "SGP")),
            };
            foreach (var st in new[] { "PTS", "REB", "AST", "3PM", "PRA", "DD", "Q3ALL", "FFG" }) { var s0 = st; groups.Add(Tuple.Create(st, (Func<Pred, bool>)(p => p.Kind == "PLAYER" && p.Stat == s0))); }
            return string.Join("\n", groups.Select(g => string.Format(U.IC, "  {0,-7} all {1}  early {2}  late {3}", g.Item1, br(preds.Where(g.Item2)), br(preds.Where(p => g.Item2(p) && string.CompareOrdinal(p.Date, cut) < 0)), br(preds.Where(p => g.Item2(p) && string.CompareOrdinal(p.Date, cut) >= 0)))));
        }

        public static string Validate(Action<string> log, string outName = "backtest_latest.csv")
        {
            var S = Program.S;
            var preds = Run(S.SeasonStart, DateTime.Now.Date.AddDays(-1), S.BacktestSims, log, true);
            Pred.Save(Program.P(@"ledger\" + outName), preds);
            log("Brier by market (all / earlier half / later half of dates):\n" + Metrics(preds));
            var hi = preds.Where(p => p.Kind == "MEAN" && !p.Void && !double.IsNaN(p.Actual) && p.Line >= 0.62).ToList();
            string s = string.Format(U.IC, "Validation backtest with current settings: player-prop Brier {0:0.00000}; high-usage minutes bias {1:+0.00;-0.00}, points bias {2:+0.00;-0.00}",
                PropBrier(preds), hi.Where(p => p.Stat == "MIN").Average(p => p.Actual - p.Mean), hi.Where(p => p.Stat == "PTS").Average(p => p.Actual - p.Mean));
            var rel = Grader.Reliability(preds.Where(p => p.Kind == "PLAYER"));
            s += "\nBy stat: " + string.Join("; ", new[] { "PTS", "REB", "AST", "3PM", "PRA", "DD" }.Select(st =>
            {
                var l = preds.Where(p => p.Kind == "PLAYER" && p.Stat == st && !p.Void && !double.IsNaN(p.Y)).ToList();
                return l.Count == 0 ? st + " n/a" : string.Format(U.IC, "{0} {1:0.00000} (pred {2:0.000} obs {3:0.000})", st, l.Average(x => (x.P - x.Y) * (x.P - x.Y)), l.Average(x => x.P), l.Average(x => x.Y));
            }));
            s += "\nReliability: " + string.Join("; ", rel.Where(b => b[2] > 0).Select(b => string.Format(U.IC, "{0:0.0}-{1:0.0}: {2:0.000}->{3:0.000}", b[0], b[1], b[3], b[4])));
            log(s);
            return s;
        }

        static double PropBrier(List<Pred> preds)
        {
            var l = preds.Where(p => p.Kind == "PLAYER" && !p.Void && !double.IsNaN(p.Y) && (p.Stat == "PTS" || p.Stat == "REB" || p.Stat == "AST" || p.Stat == "3PM" || p.Stat == "PRA")).ToList();
            return l.Count == 0 ? double.NaN : l.Average(x => (x.P - x.Y) * (x.P - x.Y));
        }

        public static string Recalibrate(Action<string> log)
        {
            var S = Program.S;
            DateTime from = S.SeasonStart, to = DateTime.Now.Date.AddDays(-1);
            double keepP = S.SigmaPlayer, keepM = S.SigmaMin;
            var grid = new List<Tuple<double, double, double, List<Pred>, List<GameRec>>>();
            // 2026-10-29 audit: SD 0.18 never won and 0.20 sat on the grid edge, so the grid moved up
            foreach (double sp in new[] { 0.0, 0.10 })
                foreach (double sm in new[] { 0.20, 0.28, 0.36 })
                {
                    S.SigmaPlayer = sp; S.SigmaMin = sm;
                    log(string.Format(U.IC, "Backtest with player shooting-night SD {0:0.00}, minutes SD {1:0.00} ...", sp, sm));
                    var preds = Run(from, to, S.BacktestSims, log);
                    double b = PropBrier(preds);
                    log(string.Format(U.IC, "  -> player-prop Brier {0:0.00000}", b));
                    grid.Add(Tuple.Create(sp, sm, b, preds, GameRecs));
                }
            var best = grid.OrderBy(x => x.Item3).First();
            var baseline = grid.FirstOrDefault(x => Math.Abs(x.Item1 - keepP) < 1e-9 && Math.Abs(x.Item2 - keepM) < 1e-9) ?? best;
            S.SigmaPlayer = best.Item1; S.SigmaMin = best.Item2;
            var preds0 = best.Item4; var recs = best.Item5;

            // market weight check (normal approximation around the two mean margins)
            double sd = Program.HistStats.ContainsKey("margin_resid_sd") ? Program.HistStats["margin_resid_sd"] : 14;
            var wRes = new List<Tuple<double, double, double>>();
            foreach (double w in new[] { 0.0, 0.25, 0.5, 0.75, 0.9, 1.0 })
            {
                double ll = 0, br = 0;
                foreach (var r in recs)
                {
                    double p = Report.Phi((w * r.MktMargin + (1 - w) * r.ModelMargin) / sd);
                    p = Math.Min(0.999, Math.Max(0.001, p));
                    ll += -(r.HomeWon * Math.Log(p) + (1 - r.HomeWon) * Math.Log(1 - p)); br += (p - r.HomeWon) * (p - r.HomeWon);
                }
                wRes.Add(Tuple.Create(w, ll / Math.Max(1, recs.Count), br / Math.Max(1, recs.Count)));
            }
            var wBest = wRes.OrderBy(x => x.Item2).First();
            var wCur = wRes.OrderBy(x => Math.Abs(x.Item1 - S.MarketWeight)).First();
            bool moveW = recs.Count >= 100 && wCur.Item2 - wBest.Item2 > 0.01;
            if (moveW) S.MarketWeight = wBest.Item1;

            string slopeNote = FitMinutesSlope(preds0);
            // opponent defensive profiles: only switched on (or strengthened) when they improve out-of-sample prop accuracy
            double keepMs = S.MatchupStrength, bestMs = keepMs, bestMsB = best.Item3;
            var msNotes = new List<string>();
            foreach (double ms in new[] { 0.0, 0.5, 1.0 })
            {
                if (Math.Abs(ms - keepMs) < 1e-9) { msNotes.Add(string.Format(U.IC, "{0:0.0}: {1:0.00000} (current)", ms, best.Item3)); continue; }
                S.MatchupStrength = ms;
                log(string.Format(U.IC, "Backtest with matchup strength {0:0.0} ...", ms));
                double b = PropBrier(Run(from, to, S.BacktestSims, log));
                msNotes.Add(string.Format(U.IC, "{0:0.0}: {1:0.00000}", ms, b));
                if (b < bestMsB * 0.998) { bestMsB = b; bestMs = ms; }
            }
            S.MatchupStrength = bestMs;
            // hot-start weighting (scoring inputs lean harder on this season for players outscoring last season):
            // adopted only when it lowers prop Brier on both the earlier and the later half of the dates
            {
                Func<List<Pred>, Tuple<double, double>> halves = ps =>
                {
                    var l = ps.Where(p => p.Kind == "PLAYER" && !p.Void && !double.IsNaN(p.Y) && (p.Stat == "PTS" || p.Stat == "REB" || p.Stat == "AST" || p.Stat == "3PM" || p.Stat == "PRA")).ToList();
                    var ds = l.Select(p => p.Date).Distinct().OrderBy(d => d).ToList(); string cut = ds[ds.Count / 2];
                    Func<IEnumerable<Pred>, double> b = xs => xs.Average(x => (x.P - x.Y) * (x.P - x.Y));
                    return Tuple.Create(b(l.Where(p => string.CompareOrdinal(p.Date, cut) < 0)), b(l.Where(p => string.CompareOrdinal(p.Date, cut) >= 0)));
                };
                // season-trend switches from the data bank: each alternative must beat the current setting on both halves
                var cur = halves(Run(from, to, S.BacktestSims, log));
                foreach (var alt in new[] { Tuple.Create("HotStartWeight", S.HotStartWeight > 1 ? 1.0 : 3.0), Tuple.Create("MinRecency", S.MinRecency > 0 ? 0.0 : 0.25),
                    Tuple.Create("RatePriorMin", S.RatePriorMin > 150 ? 150.0 : 300.0), Tuple.Create("GammaOverride", S.GammaOverride > 0 ? 0.0 : 1.0) })
                {
                    var fld = typeof(Settings).GetField(alt.Item1); double keepV = (double)fld.GetValue(S);
                    fld.SetValue(S, alt.Item2);
                    log(string.Format(U.IC, "Backtest with {0} = {1} ...", alt.Item1, alt.Item2));
                    var res = halves(Run(from, to, S.BacktestSims, log));
                    bool sw = res.Item1 < cur.Item1 && res.Item2 < cur.Item2;
                    if (sw) cur = res; else fld.SetValue(S, keepV);
                    slopeNote += string.Format(U.IC, "\n{0} {1} vs {2}: prop Brier earlier half {3:0.00000} vs {4:0.00000}, later half {5:0.00000} vs {6:0.00000}. Using {7}.", alt.Item1, keepV, alt.Item2, sw ? res.Item1 : cur.Item1, res.Item1, sw ? res.Item2 : cur.Item2, res.Item2, fld.GetValue(S));
                }

                // team form vs the market (how far each team has beaten its DraftKings spreads so far): judged on moneyline
                // Brier, which it targets, and adopted only if that improves on both halves without hurting player props
                Func<List<Pred>, Tuple<double, double>> mlHalves = ps =>
                {
                    var l = ps.Where(p => p.Kind == "ML" && !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.P)).ToList();
                    var ds = l.Select(p => p.Date).Distinct().OrderBy(x => x).ToList(); string cut = ds[ds.Count / 2];
                    Func<IEnumerable<Pred>, double> b = xs => xs.Average(x => (x.P - x.Y) * (x.P - x.Y));
                    return Tuple.Create(b(l.Where(p => string.CompareOrdinal(p.Date, cut) < 0)), b(l.Where(p => string.CompareOrdinal(p.Date, cut) >= 0)));
                };
                double keepW = S.TeamFormWeight, bestW = keepW;
                var basePreds = Run(from, to, S.BacktestSims, log);
                var baseMl = mlHalves(basePreds); var baseProp = halves(basePreds); var bestMl = baseMl;
                var formNotes = new List<string> { string.Format(U.IC, "{0}: {1:0.00000} / {2:0.00000}", keepW, baseMl.Item1, baseMl.Item2) };
                foreach (double w in new[] { 0.0, 0.5, 1.0 })
                {
                    if (Math.Abs(w - keepW) < 1e-9) continue;
                    S.TeamFormWeight = w;
                    log(string.Format(U.IC, "Backtest with team-form weight {0} ...", w));
                    var ps = Run(from, to, S.BacktestSims, log); var ml = mlHalves(ps); var pr = halves(ps);
                    formNotes.Add(string.Format(U.IC, "{0}: {1:0.00000} / {2:0.00000} (props {3:0.00000} / {4:0.00000})", w, ml.Item1, ml.Item2, pr.Item1, pr.Item2));
                    if (ml.Item1 < bestMl.Item1 && ml.Item2 < bestMl.Item2 && pr.Item1 <= baseProp.Item1 + 0.0002 && pr.Item2 <= baseProp.Item2 + 0.0002) { bestW = w; bestMl = ml; }
                }
                S.TeamFormWeight = bestW;
                slopeNote += "\nTeam form vs market, moneyline Brier earlier / later half: " + string.Join("; ", formNotes) + string.Format(U.IC, ". Using {0}.", bestW);
            }
            slopeNote += "\n" + FitDdCalibration(preds0);
            slopeNote += "\n" + FitPropCalibration(preds0);
            slopeNote += "\nMatchup strength (player-prop Brier): " + string.Join("; ", msNotes) + string.Format(U.IC, ". Using {0:0.0}.", bestMs);
            S.LastRecalibration = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + string.Format(U.IC, " on {0} games ({1} to {2})", recs.Count, from.ToString("MMM d", U.IC), to.ToString("MMM d", U.IC));
            S.Save(Program.P("settings.ini"));
            Pred.Save(Program.P(@"ledger\backtest_latest.csv"), preds0);

            var sb = new StringBuilder();
            sb.AppendLine(string.Format(U.IC, "Walk-forward backtest {0:yyyy-MM-dd} to {1:yyyy-MM-dd}: {2} games, {3:N0} sims per game. Player data, minutes and rates use only games before each date; availability uses who actually dressed; market = DraftKings closing line (ESPN).", from, to, recs.Count, S.BacktestSims));
            foreach (var x in grid) sb.AppendLine(string.Format(U.IC, "  player shooting-night SD {0:0.00}, minutes SD {1:0.00}: player-prop Brier {2:0.00000}{3}", x.Item1, x.Item2, x.Item3, x == best ? "  <- selected" : ""));
            sb.AppendLine(string.Format(U.IC, "Selected vs previous setting: Brier {0:0.00000} vs {1:0.00000} ({2:+0.0%;-0.0%} change).", best.Item3, baseline.Item3, (best.Item3 - baseline.Item3) / baseline.Item3));
            if (recs.Count > 0)
            {
                double bBlend = recs.Average(r => (r.Blend - r.HomeWon) * (r.Blend - r.HomeWon));
                var withMl = recs.Where(r => !double.IsNaN(r.MktNoVig)).ToList();
                sb.AppendLine(string.Format(U.IC, "Moneyline Brier: simulation {0:0.0000}; DraftKings closing no-vig {1:0.0000} (n={2}). Mean margin error: market {3:0.0}, model-only {4:0.0} pts. Mean total error vs closing total {5:+0.0;-0.0} pts.",
                    bBlend, withMl.Count > 0 ? withMl.Average(r => (r.MktNoVig - r.HomeWon) * (r.MktNoVig - r.HomeWon)) : double.NaN, withMl.Count,
                    recs.Average(r => Math.Abs(r.Margin - r.MktMargin)), recs.Average(r => Math.Abs(r.Margin - r.ModelMargin)), recs.Average(r => r.Total - r.MktTotal)));
                sb.AppendLine("Market weight check (log loss, normal approximation): " + string.Join("; ", wRes.Select(x => string.Format(U.IC, "w={0:0.00}: {1:0.0000}", x.Item1, x.Item2))) + (moveW ? string.Format(U.IC, ". Market weight moved to {0:0.00}.", S.MarketWeight) : ". Market weight kept (sample too small or difference too small to justify a change)."));
            }
            foreach (var st in new[] { "PTS", "REB", "AST", "3PM", "MIN" })
            {
                var l = preds0.Where(p => p.Kind == "MEAN" && p.Stat == st && !p.Void && !double.IsNaN(p.Actual)).ToList(); if (l.Count == 0) continue;
                var hi = l.Where(p => p.Line >= 0.62).ToList();
                sb.AppendLine(string.Format(U.IC, "Projected {0}: mean {1:0.00} vs actual {2:0.00} (bias {3:+0.00;-0.00}, MAE {4:0.00}, n={5}){6}", st, l.Average(x => x.Mean), l.Average(x => x.Actual), l.Average(x => x.Mean - x.Actual), l.Average(x => Math.Abs(x.Mean - x.Actual)), l.Count,
                    hi.Count >= 20 ? string.Format(U.IC, "; high-usage players bias {0:+0.00;-0.00} (n={1})", hi.Average(x => x.Mean - x.Actual), hi.Count) : ""));
            }
            sb.AppendLine(slopeNote);
            var rel = Grader.Reliability(preds0.Where(p => p.Kind == "PLAYER"));
            sb.AppendLine("Player-threshold reliability (predicted -> observed): " + string.Join("; ", rel.Where(b => b[2] > 0).Select(b => string.Format(U.IC, "{0:0.00}-{1:0.00}: {2:0.000}->{3:0.000} (n={4})", b[0], b[1], b[3], b[4], b[2]))));
            File.WriteAllText(Program.P(@"ledger\recalibration.txt"), sb.ToString());
            log(sb.ToString());
            return sb.ToString();
        }
    }
}
