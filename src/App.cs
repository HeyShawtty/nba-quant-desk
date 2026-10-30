using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NbaQuant
{
    static class AppOps
    {
        static readonly object Gate = new object();
        public static string ReportPath(DateTime d) { return Program.P(@"reports\NBA_Quant_Report_" + d.ToString("yyyy-MM-dd") + ".xlsx"); }
        public static string PredPath(DateTime d) { return Program.P(@"predictions\pred_" + d.ToString("yyyy-MM-dd") + ".csv"); }
        public static string GradedPath(DateTime d) { return Program.P(@"ledger\graded_" + d.ToString("yyyy-MM-dd") + ".csv"); }
        public static string ReviewPath(DateTime d) { return Program.P(@"reports\Game_Review_" + d.ToString("yyyy-MM-dd") + ".xlsx"); }

        // First date (today or later) that still has games to play.
        public static DateTime NextSlateDate()
        {
            for (int i = 0; i < 10; i++)
            {
                var d = DateTime.Now.Date.AddDays(i);
                if (Store.Events(d).Any(e => !e.Completed)) return d;
            }
            return DateTime.Now.Date;
        }

        public static string RunSlate(DateTime d, int sims)
        {
            lock (Gate)
            {
                var log = Program.Log;
                log("Building slate for " + d.ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture) + " ...");
                // refresh the 2026-27 data bank (every completed game before this slate) and its league-wide workbook
                try { log(Bank.Build(d.AddDays(-1))); Report.BuildBankWorkbook(Program.P(@"reports\Data_Bank_2026-27.xlsx")); }
                catch (Exception e) { log("Data bank not refreshed: " + e.Message); }
                Program.BuildSlate(d, true);
                if (Program.Games.Count == 0) { log("No games left to price for that date (all started or finished)."); return null; }
                if (Program.SkippedStarted.Count > 0)
                {
                    Program.Verification.Add(new[] { "Games already started", "EXCLUDED", string.Join("; ", Program.SkippedStarted) + ". Their earlier pre-tip predictions stay on record for grading." });
                    log("Skipping games already under way: " + string.Join(", ", Program.SkippedStarted));
                }
                int props = 0;
                foreach (var g in Program.Games.Where(x => !string.IsNullOrEmpty(x.Def.AnId)))
                {
                    g.Props = Store.AnProps(g.Def.AnId, Program.P(@"data\cache\an\props_" + g.Def.AnId + ".json"));
                    props += g.Props.Count;
                }
                log(props > 0 ? props + " book prop lines captured." : "No player-prop lines posted yet on the props feed.");
                log(string.Format(CultureInfo.InvariantCulture, "Simulating {0} games x {1:N0} ...", Program.Games.Count, sims));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Program.Simulate(sims, true);
                log("Simulation done in " + sw.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s. Analysing ...");
                Analysis.Run(true);
                string path = ReportPath(d), pp = PredPath(d);
                // keep the earlier run: archive its files, and keep its predictions for games this run did not price
                var fresh = Predictions.Collect(d.ToString("yyyy-MM-dd"));
                if (File.Exists(pp))
                {
                    string stamp = File.GetLastWriteTime(pp).ToString("HHmm");
                    var old = Pred.Load(pp);
                    File.Copy(pp, pp.Replace(".csv", "_" + stamp + ".csv"), true);
                    if (File.Exists(path)) File.Copy(path, path.Replace(".xlsx", "_" + stamp + ".xlsx"), true);
                    var ids = new HashSet<string>(Program.Games.Select(g => g.Def.EspnId));
                    bool partial = Program.SkippedStarted.Count > 0;
                    var merged = fresh.Where(x => !(partial && x.Kind == "LEADER")).ToList();
                    merged.AddRange(old.Where(x => x.Kind != "LEADER" && !ids.Contains(x.GameId)));
                    if (partial) merged.AddRange(old.Where(x => x.Kind == "LEADER"));
                    fresh = merged;
                    log("Earlier run archived (" + stamp + "); " + old.Select(x => x.GameId).Where(id => !string.IsNullOrEmpty(id)).Distinct().Count(id => !ids.Contains(id)) + " started game(s) keep their earlier predictions.");
                }
                Report.Build(path);
                try { log("Website data: " + Report.ExportSite(Program.P(@"site\data.js"), d)); } catch (Exception e) { log("Website data not written: " + e.Message); }
                Pred.Save(pp, fresh);
                log("Predictions saved for grading: " + pp);
                foreach (var w in Program.Warnings) log("WARNING: " + w);
                return path;
            }
        }

        public static string Grade(DateTime d)
        {
            lock (Gate)
            {
                string pp = PredPath(d);
                if (!File.Exists(pp)) return "No saved predictions for " + d.ToString("yyyy-MM-dd") + ".";
                var preds = Pred.Load(pp);
                Grader.Grade(preds, id => string.IsNullOrEmpty(id) ? null : Store.Summary(id, true));
                int open = preds.Where(p => p.Kind == "ML").Count(p => p.Void);
                if (open > 0) return "Some games are not final yet; grade again later.";
                Pred.Save(GradedPath(d), preds);
                var sb = new StringBuilder("Graded " + d.ToString("yyyy-MM-dd") + ":\n");
                try { sb.AppendLine(Report.BuildReview(d.ToString("yyyy-MM-dd"), ReviewPath(d))); sb.AppendLine("Game-by-game review: " + ReviewPath(d)); }
                catch (Exception e) { sb.AppendLine("Review not built: " + e.Message); }
                foreach (var s in Grader.Summarize(preds).Where(x => !x.Group.StartsWith("PLAYER ")))
                    sb.AppendLine(string.Format(U.IC, "  {0,-24} n={1,5}  predicted {2,6}  hit {3,6}  Brier {4:0.000}{5}", s.Group, s.N, U.Pct(s.MeanP), U.Pct(s.HitRate), s.Brier, double.IsNaN(s.BrierMkt) ? "" : string.Format(U.IC, " (market {0:0.000})", s.BrierMkt)));
                foreach (var p in preds.Where(x => x.Tag != null && (x.Tag.StartsWith("top") || x.Tag == "best_sgp")))
                    sb.AppendLine(string.Format(U.IC, "  [{0}] {1} ({2}) -> {3}", p.Tag, p.Selection, U.Pct(p.P), p.Void ? "VOID" : p.Y >= 1 ? "HIT" : "MISS"));
                return sb.ToString();
            }
        }

        public static string Recalibrate() { lock (Gate) return Walk.Recalibrate(Program.Log); }

        // nbaquant-cli audit : where the model works and fails (walk-forward backtest + graded live slates)
        public static string Audit(string btName = "backtest_latest.csv")
        {
            lock (Gate)
            {
                string bt = Program.P(@"ledger\" + btName);
                if (!File.Exists(bt)) return "No backtest yet: run validate or recalibrate first.";
                var done = Store.CompletedBefore(DateTime.Now.Date.AddDays(1), Program.S.SeasonStart);
                string path = Program.P(@"reports\Model_Audit_" + DateTime.Now.ToString("yyyy-MM-dd") + ".xlsx");
                Report.BuildAudit(path, Pred.Load(bt), Ledger.LoadAll(), done);
                Program.Log("Saved audit: " + path);
                return path;
            }
        }

        // nbaquant-cli live DATE AWAY@HOME : simulate the rest of an in-progress game from the live score and box
        public static void Live(DateTime d, string matchup, int sims)
        {
            var log = Program.Log;
            var parts = matchup.ToUpperInvariant().Split('@');
            var ev = Store.Events(d).FirstOrDefault(e => e.Away == parts[0] && e.Home == parts[1]);
            if (ev == null) { log("Game not found: " + matchup); return; }
            var pre = Espn.ParseSummary(Net.Get(Espn.Base + "summary?event=" + ev.Id));
            foreach (var b in pre.Box.Where(b => b.Dnp && !b.Reason.ToUpperInvariant().Contains("COACH"))) Program.ForceOut.Add(b.Pid);
            Program.ForceIncludeId = ev.Id;
            string ds = d.ToString("yyyyMMdd");
            Store.OddsFile = Program.P(@"data\cache\an\odds_" + ds + "_pretip.json");
            Program.BuildSlate(d, true, true);
            Store.OddsFile = null;
            Program.Games = Program.Games.Where(g => g.Def.EspnId == ev.Id).ToList();
            var gm = Program.Games[0];
            var preLine = gm.Def.DK;
            var liveGame = Store.AnOdds(d).FirstOrDefault(a => a.HomeName == ev.HomeName && a.AwayName == ev.AwayName);
            var liveDk = liveGame == null ? null : liveGame.Books.FirstOrDefault(b => b.Book == "DraftKings" && b.Total > 0);
            if (liveDk != null) gm.Def.DK = liveDk;
            var now = Program.RunLive(gm, sims);
            log(string.Format(U.IC, "LIVE {0} {1} - {2} {3} | {4} | inactive: {5}", gm.Def.Away, now.AwayPts, now.HomePts, gm.Def.Home, now.StatusText,
                string.Join(", ", Program.TeamPlayers[gm.Def.Home].Concat(Program.TeamPlayers[gm.Def.Away]).Where(p => p.Status == "OUT" && Math.Max(p.CsMpg, p.PsMpg) >= 15).Select(p => p.Name))));
            log(string.Format(U.IC, "Pre-tip DK line used for team strength: {0} {1:+0.0;-0.0}, total {2}. Current DK line: {3}", gm.Def.Home, preLine.SprH, preLine.Total,
                liveDk == null ? "not available" : string.Format(U.IC, "{0} {1:+0.0;-0.0} ({2}), total {3} (o {4} / u {5}), ML {6} {7} / {8} {9}", gm.Def.Home, liveDk.SprH, U.Am(liveDk.SprHOdds), liveDk.Total, U.Am(liveDk.OverOdds), U.Am(liveDk.UnderOdds), gm.Def.Home, U.Am(liveDk.MlH), gm.Def.Away, U.Am(liveDk.MlA))));
            log(string.Format(U.IC, "Sim from here ({0:N0} runs): {1} win {2}, {3} win {4}; final total mean {5:0.0} (P10 {6}, P90 {7}); P(over {8}) {9}; {1} covers {10:+0.0;-0.0}: {11}",
                sims, gm.Def.Home, U.Pct(gm.WinH), gm.Def.Away, U.Pct(1 - gm.WinH), gm.MeanTotal, gm.TotalPct[0], gm.TotalPct[4], gm.Def.DK.Total, U.Pct(gm.POver), gm.Def.DK.SprH, U.Pct(gm.PCoverH)));
            if (liveDk != null && liveDk.HasMl)
                foreach (var side in new[] { Tuple.Create(gm.Def.Home, gm.WinH, liveDk.MlH), Tuple.Create(gm.Def.Away, 1 - gm.WinH, liveDk.MlA) })
                    log(string.Format(U.IC, "  {0} ML {1}: model {2}, EV {3:+0.0%;-0.0%}", side.Item1, U.Am(side.Item3), U.Pct(side.Item2), U.EvPerDollar(side.Item2, side.Item3)));
            Analysis.Run(true);
            foreach (var x in Analysis.Players.Where(p => p.MeanMin >= 15).OrderByDescending(p => p.Pra.Mean).Take(12))
            {
                var b = now.Box.FirstOrDefault(bb => bb.Pid == x.P.Pid);
                log(string.Format(U.IC, "  {0,-22} {1,-3} now {2,2}p {3,2}r {4,2}a {5}3s -> final mean {6:0.0}p {7:0.0}r {8:0.0}a {9:0.0}3s | P(20+p) {10} P(DD) {11} P(3+ 3s) {12}",
                    x.P.Name, x.P.Team, b == null ? 0 : b.Pts, b == null ? 0 : b.Reb, b == null ? 0 : b.Ast, b == null ? 0 : b.Tpm, x.Pts.Mean, x.Reb.Mean, x.Ast.Mean, x.Tpm.Mean, U.Pct(x.Pts.PAtLeast(20)), U.Pct(x.DD), U.Pct(x.Tpm.PAtLeast(3))));
            }
            foreach (var b in new[] { Tuple.Create("safest", 0.30, 1.0), Tuple.Create("boost-range", 0.19, 0.26), Tuple.Create("bigger payout", 0.09, 0.14) })
            {
                log("=== 4-leg " + b.Item1 + " ===");
                foreach (var c in Analysis.Best4(gm, b.Item2, b.Item3, 3))
                    log(string.Format(U.IC, "  {0:0.0}% (fair {1}, lift {2:0.00}): {3}", 100 * c.Joint, U.Am(U.FairAmerican(c.Joint)), c.Lift, string.Join(" + ", c.Legs.Select(l => l.Desc + " [" + U.Pct(l.P) + "]"))));
            }
        }

        // nbaquant-cli sgp4 DATE AWAY@HOME : four-leg parlays for one game in three risk bands, saved for grading
        public static void FourLeg(DateTime d, string matchup, int sims)
        {
            var log = Program.Log;
            Program.BuildSlate(d, true);
            var parts = matchup.ToUpperInvariant().Split('@');
            Program.Games = Program.Games.Where(g => g.Def.Away == parts[0] && g.Def.Home == parts[1]).ToList();
            if (Program.Games.Count == 0) { log("Game not found or already started: " + matchup); return; }
            var gm = Program.Games[0];
            log(string.Format(U.IC, "{0}: DK spread {1} total {2} ML {3}/{4}. Out: {5}. Questionable: {6}", matchup, gm.Def.DK.SprH, gm.Def.DK.Total, U.Am(gm.Def.DK.MlH), U.Am(gm.Def.DK.MlA),
                string.Join(", ", gm.H.Pl.Concat(gm.A.Pl).Count() > 0 ? Program.TeamPlayers[gm.Def.Home].Concat(Program.TeamPlayers[gm.Def.Away]).Where(p => p.Status == "OUT" && Math.Max(p.CsMpg, p.PsMpg) >= 15).Select(p => p.Name) : new string[0]),
                string.Join(", ", gm.H.Pl.Concat(gm.A.Pl).Where(p => p.Status == "Q").Select(p => p.Name))));
            Program.Simulate(sims, false);
            Analysis.Run(false);
            var bands = new[] { Tuple.Create("safest", 0.30, 1.0), Tuple.Create("boost-range", 0.19, 0.26), Tuple.Create("bigger payout", 0.09, 0.14) };
            var preds = new List<Pred>();
            foreach (var b in bands)
            {
                log("=== " + b.Item1 + " (joint " + U.Pct(b.Item2) + "-" + U.Pct(Math.Min(1, b.Item3)) + ") ===");
                foreach (var c in Analysis.Best4(gm, b.Item2, b.Item3, 3))
                {
                    log(string.Format(U.IC, "  {0:0.0}% (fair {1}, lift {2:0.00}): {3}", 100 * c.Joint, U.Am(U.FairAmerican(c.Joint)), c.Lift, string.Join(" + ", c.Legs.Select(l => l.Desc + " [" + U.Pct(l.P) + "]"))));
                    preds.Add(new Pred { Date = d.ToString("yyyy-MM-dd"), GameId = gm.Def.EspnId, Game = gm.Def.Away + " @ " + gm.Def.Home, Kind = "SGP", P = c.Joint, Mean = c.Product, Legs = string.Join(";", c.Legs.Select(Predictions.LegCode)), Tag = "sgp4_" + b.Item1.Replace(" ", "_").Replace("-", "_"), Selection = string.Join(" + ", c.Legs.Select(l => l.Desc)) });
                }
            }
            string pp = PredPath(d);
            var all = File.Exists(pp) ? Pred.Load(pp).Where(x => !(x.Tag != null && x.Tag.StartsWith("sgp4_") && x.GameId == gm.Def.EspnId)).ToList() : new List<Pred>();
            all.AddRange(preds);
            Pred.Save(pp, all);
            log("Saved " + preds.Count + " four-leg tickets to " + pp + " for grading.");
            var ps = Analysis.Players.Where(x => x.MeanMin >= 20).OrderByDescending(x => x.Pra.Mean).Take(8);
            foreach (var x in ps) log(string.Format(U.IC, "  {0,-22} {1} min {2:0.0} | pts {3:0.0} reb {4:0.0} ast {5:0.0} 3pm {6:0.0} | P(20+) {7} P(DD) {8} P(3+ 3s) {9}", x.P.Name, x.P.Team, x.MeanMin, x.Pts.Mean, x.Reb.Mean, x.Ast.Mean, x.Tpm.Mean, U.Pct(x.Pts.PAtLeast(20)), U.Pct(x.DD), U.Pct(x.Tpm.PAtLeast(3))));
            log(string.Format(U.IC, "  Game: {0} win {1}, {2} win {3}, mean total {4:0.0}, P(over {5}) {6}", gm.Def.Away, U.Pct(1 - gm.WinH), gm.Def.Home, U.Pct(gm.WinH), gm.MeanTotal, gm.Def.DK.Total, U.Pct(gm.POver)));
        }
    }

    static class EntryPoint
    {
        [STAThread]
        static int Main(string[] args)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            Directory.SetCurrentDirectory(root);
            try
            {
                if (args.Length == 0 || args[0] == "gui" || args[0] == "--selftest")
                {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    System.Windows.Forms.Application.Run(args.Length >= 3 && args[0] == "--selftest" ? new MainForm(root, args[1], args[2]) : new MainForm(root));
                    return 0;
                }
                Program.Init(root);
                string cmd = args[0].ToLowerInvariant();
                Func<int, DateTime> date = i => args.Length > i && args[i] != "next" ? DateTime.ParseExact(args[i], "yyyy-MM-dd", CultureInfo.InvariantCulture) : AppOps.NextSlateDate();
                int sims = Program.S.Sims;
                int si = Array.IndexOf(args, "--sims"); if (si >= 0 && si + 1 < args.Length) sims = int.Parse(args[si + 1]);
                // --set Key=Value overrides a setting for this run only (used to backtest alternatives)
                var overrides = new List<string>();
                for (int i = 0; i + 1 < args.Length; i++)
                    if (args[i] == "--set") { var kv = args[i + 1].Split('='); if (kv.Length == 2 && Program.S.Set(kv[0], kv[1])) { Console.WriteLine("override " + args[i + 1]); overrides.Add(kv[0] + kv[1]); } }
                switch (cmd)
                {
                    case "run": AppOps.RunSlate(date(1), sims); break;
                    case "grade": Console.WriteLine(AppOps.Grade(args.Length > 1 ? date(1) : DateTime.Now.Date.AddDays(-1))); break;
                    case "recalibrate": AppOps.Recalibrate(); break;
                    case "validate": Walk.Validate(Program.Log, overrides.Count == 0 ? "backtest_latest.csv" : "backtest_" + string.Join("_", overrides).Replace(".", "p") + ".csv"); break;
                    case "injuries":
                        Console.WriteLine(InjuryReport.Apply(date(1), Program.Log)); break;
                    case "site":
                        {   // website data only (no report or prediction files); --odds replays a saved odds snapshot
                            var d = date(1);
                            int oi = Array.IndexOf(args, "--odds"); if (oi >= 0 && oi + 1 < args.Length) Store.OddsFile = Program.P(args[oi + 1]);
                            Console.WriteLine(Bank.Build(d.AddDays(-1)));
                            Program.BuildSlate(d, true); Program.Simulate(sims, true); Analysis.Run(true);
                            Console.WriteLine("Website data: " + Report.ExportSite(Program.P(@"site\data.js"), d)); break;
                        }
                    case "bank":
                        Console.WriteLine(Bank.Build(DateTime.Now.Date.AddDays(-1))); Report.BuildBankWorkbook(Program.P(@"reports\Data_Bank_2026-27.xlsx")); break;
                    case "review":
                        {   // game-by-game right/wrong review of a graded slate
                            var d = date(1); Console.WriteLine(Report.BuildReview(d.ToString("yyyy-MM-dd"), AppOps.ReviewPath(d))); break;
                        }
                    case "fitcal":
                        {   // refit the player-threshold calibration on the saved backtest (held-out test) and save it
                            string note = Walk.FitPropCalibration(Pred.Load(Program.P(@"ledger\" + (args.Length > 1 && args[1].EndsWith(".csv") ? args[1] : "backtest_latest.csv"))));
                            Program.S.Save(Program.P("settings.ini")); Console.WriteLine(note + "\nPropCal=" + Program.S.PropCal); break;
                        }
                    case "audit": AppOps.Audit(args.Length > 1 && args[1].EndsWith(".csv") ? args[1] : "backtest_latest.csv"); break;
                    case "sgp4": AppOps.FourLeg(date(1), args[2], sims); break;
                    case "live": AppOps.Live(date(1), args[2], sims); break;
                    case "next": Console.WriteLine(AppOps.NextSlateDate().ToString("yyyy-MM-dd")); break;
                    default: Console.WriteLine("usage: nbaquant-cli run [yyyy-MM-dd|next] [--sims N] | grade [yyyy-MM-dd] | recalibrate | next"); return 1;
                }
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERROR: " + e);
                try { File.AppendAllText(Path.Combine(root, "error.log"), DateTime.Now + " " + e + Environment.NewLine); } catch { }
                if (args.Length == 0) System.Windows.Forms.MessageBox.Show(e.Message, "NBA Quant Desk");
                return 2;
            }
        }
    }
}
