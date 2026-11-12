using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NbaQuant
{
    // Trend learning: after every game, what the model projected for a player is compared with what he did, and the gap is
    // carried into his next projection. Rates are judged at the minutes he actually played (projection x actual minutes /
    // projected minutes), so a short night from a blowout or foul trouble does not read as a scoring slump.
    //   factor = (actual + K) / (expected + K)      K = "as projected" points (or assists) mixed in before his record counts
    // Walk-forward backtests feed it date by date (no look-ahead); live runs replay the latest walk-forward backtest and any
    // graded live slate after it. Each stored projection carries the factor it was made with, so learning always starts
    // from the un-learned baseline. Minutes use a separate rule (Settings.MinTrend, last three games).
    static class Learner
    {
        public class Row { public double ActPts, ExpPts, ActAst, ExpAst; public int N; public string Last = ""; }
        static Dictionary<string, Row> rows = new Dictionary<string, Row>();
        public static string Through = "";

        public static void Reset() { rows = new Dictionary<string, Row>(); Through = ""; }
        public static Row Of(string pid) { Row r; return pid != null && rows.TryGetValue(pid, out r) ? r : null; }
        public static int Count { get { return rows.Count; } }

        public static double Factor(double act, double exp, double k, double lo, double hi)
        {
            if (k <= 0 || exp <= 0) return 1;
            return Math.Max(lo, Math.Min(hi, (act + k) / (exp + k)));
        }

        // one graded date of projections (MEAN rows for MIN, PTS, AST with their actuals)
        public static void Add(IEnumerable<Pred> preds)
        {
            foreach (var grp in preds.Where(p => p.Kind == "MEAN" && !string.IsNullOrEmpty(p.Pid) && !p.Void && !double.IsNaN(p.Actual) && !double.IsNaN(p.Mean)).GroupBy(p => p.Date + "|" + p.Pid))
            {
                var mn = grp.FirstOrDefault(p => p.Stat == "MIN"); if (mn == null || mn.Mean < 5 || mn.Actual < 5) continue;
                double scale = Math.Max(0.3, Math.Min(2.0, mn.Actual / mn.Mean));
                Row r; if (!rows.TryGetValue(mn.Pid, out r)) rows[mn.Pid] = r = new Row();
                var pt = grp.FirstOrDefault(p => p.Stat == "PTS"); var asr = grp.FirstOrDefault(p => p.Stat == "AST");
                if (pt != null) { double f = double.IsNaN(pt.PMarket) || pt.PMarket <= 0 ? 1 : pt.PMarket; r.ActPts += pt.Actual; r.ExpPts += pt.Mean / f * scale; }
                if (asr != null) { double f = double.IsNaN(asr.PMarket) || asr.PMarket <= 0 ? 1 : asr.PMarket; r.ActAst += asr.Actual; r.ExpAst += asr.Mean / f * scale; }
                r.N++; if (string.CompareOrdinal(mn.Date, r.Last) > 0) r.Last = mn.Date;
                if (string.CompareOrdinal(mn.Date, Through) > 0) Through = mn.Date;
            }
        }

        // live runs: everything graded before the slate date
        public static void LoadLive(DateTime slate)
        {
            Reset();
            try
            {
                string ds = slate.ToString("yyyy-MM-dd"), bt = Program.P(@"ledger\backtest_latest.csv");
                string last = "";
                if (File.Exists(bt))
                {
                    var b = Pred.Load(bt).Where(p => p.Kind == "MEAN" && string.CompareOrdinal(p.Date, ds) < 0).ToList();
                    Add(b); last = Through;
                }
                var live = Ledger.LoadAll().Where(p => p.Kind == "MEAN" && string.CompareOrdinal(p.Date, ds) < 0 && string.CompareOrdinal(p.Date, last) > 0).ToList();
                if (live.Count > 0) Add(live);
            }
            catch (Exception e) { Program.Warnings.Add("Trend learning not loaded (" + e.Message + "); projections run without it."); Reset(); }
        }
    }
}
