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
                if (Program.SkippedBanned.Count > 0) log("Banned, not simulated or priced (information only): " + string.Join(", ", Program.SkippedBanned));
                if (Program.Games.Count == 0) { log("No games left to price for that date (all started, finished or banned)."); return null; }
                if (Program.SkippedStarted.Count > 0)
                {
                    Program.Verification.Add(new[] { "Games already started", "EXCLUDED", string.Join("; ", Program.SkippedStarted) + ". Their earlier pre-tip predictions stay on record for grading." });
                    log("Skipping games already under way: " + string.Join(", ", Program.SkippedStarted));
                }
                if (Program.SkippedBanned.Count > 0)
                    Program.Verification.Add(new[] { "Banned teams", "EXCLUDED", string.Join("; ", Program.SkippedBanned) + ": not simulated or priced while " + string.Join(", ", Ban.List().Select(b => b.Team)) + " banned. Their data is still collected." });
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
                    // a game banned after an earlier run priced it loses those predictions too
                    merged.AddRange(old.Where(x => x.Kind != "LEADER" && !ids.Contains(x.GameId) && !Program.BannedIds.Contains(x.GameId)));
                    if (partial) merged.AddRange(old.Where(x => x.Kind == "LEADER"));
                    fresh = merged;
                    log("Earlier run archived (" + stamp + "); " + old.Select(x => x.GameId).Where(id => !string.IsNullOrEmpty(id)).Distinct().Count(id => !ids.Contains(id)) + " started game(s) keep their earlier predictions.");
                }
                try { Safest.Update(d, fresh, Report.InjuryReportUsed(d)); log("Top 3 safest board: " + Safest.FilePath(d)); }
                catch (Exception e) { log("Top 3 safest board not updated: " + e.Message); }
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
                foreach (var p in preds.Where(x => x.Tag != null && (x.Tag.StartsWith("top") || x.Tag.StartsWith("safe3_") || x.Tag == "best_sgp")))
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
            if (parts.Length == 2 && Ban.Involves(parts[0], parts[1])) { log(matchup + ": " + Ban.Who(parts[0], parts[1]) + " banned, not simulated (information only)."); return; }
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

        // nbaquant-cli stress DATE AWAY@HOME : re-simulate one game under changed assumptions to see how solid the pick is
        public static void Stress(DateTime d, string matchup, int sims)
        {
            var log = Program.Log; var S = Program.S;
            var parts = matchup.ToUpperInvariant().Split('@');
            if (parts.Length == 2 && Ban.Involves(parts[0], parts[1])) { log(matchup + ": " + Ban.Who(parts[0], parts[1]) + " banned, not simulated (information only)."); return; }
            var ev = Store.Events(d).FirstOrDefault(e => e.Away == parts[0] && e.Home == parts[1]);
            if (ev == null) { log("Game not found: " + matchup); return; }
            Program.ForceIncludeId = ev.Id;
            Func<GameCtx> build = () =>
            {
                Program.BuildSlate(d, true, true);
                Program.Games = Program.Games.Where(g => g.Def.EspnId == ev.Id).ToList();
                if (Program.Games.Count == 0) return null;
                Program.Simulate(sims, true); return Program.Games[0];
            };
            var rows = new List<string>();
            Action<string, GameCtx> line = (name, g) =>
            {
                if (g == null) { rows.Add(name + ": game not available"); return; }
                rows.Add(string.Format(U.IC, "{0,-46} {1} win {2,6} | margin {3} {4:+0.0;-0.0} | total {5:0.0}, over {6} {7} | {1} covers {8:+0.0;-0.0}: {9}",
                    name, g.Def.Away, U.Pct(1 - g.WinH), g.Def.Home, g.MeanMargin, g.MeanTotal, g.Def.DK.Total, U.Pct(g.POver), -g.Def.DK.SprH, U.Pct(1 - g.PCoverH)));
            };
            var baseG = build(); line("Base (current model)", baseG);
            if (baseG == null) { foreach (var r in rows) log(r); return; }
            log(string.Format(U.IC, "{0}: DraftKings {1} {2} ML {3}/{4}, total {5}. Model alone {6} {7}; DraftKings no-vig {6} {8}.", matchup, baseG.Def.Home, baseG.Def.DK.SprH, U.Am(baseG.Def.DK.MlA), U.Am(baseG.Def.DK.MlH), baseG.Def.DK.Total,
                baseG.Def.Away, U.Pct(1 - baseG.ModelWinH), U.Pct(1 - Program.NoVigHome(baseG.Def.DK))));
            Analysis.Run(false);
            var keyAway = Analysis.Players.Where(p => !p.Home).OrderByDescending(p => p.Pra.Mean * p.PPlays).FirstOrDefault();
            var keyHome = Analysis.Players.Where(p => p.Home).OrderByDescending(p => p.Pra.Mean * p.PPlays).FirstOrDefault();
            var qs = baseG.H.Pl.Concat(baseG.A.Pl).Where(p => p.Status == "Q").ToList();
            double mw = S.MarketWeight, smin = S.SigmaMin, spl = S.SigmaPlayer, tf = S.TeamFormWeight;
            S.MarketWeight = 0; line("Model alone (no market)", build()); S.MarketWeight = mw;
            S.MarketWeight = 0.5; line("Half model, half market", build()); S.MarketWeight = mw;
            S.MarketWeight = 1; line("Market only", build()); S.MarketWeight = mw;
            S.TeamFormWeight = 0.5; line("Team form on (half strength)", build()); S.TeamFormWeight = tf;
            S.SigmaMin = 0.20; S.SigmaPlayer = 0; line("Less randomness (minutes 0.20, shooting 0)", build()); S.SigmaMin = smin; S.SigmaPlayer = spl;
            S.SigmaMin = 0.45; S.SigmaPlayer = 0.18; line("More randomness (minutes 0.45, shooting 0.18)", build()); S.SigmaMin = smin; S.SigmaPlayer = spl;
            if (keyAway != null) { Program.ForceOut.Add(keyAway.P.Pid); line(keyAway.P.Name + " (" + keyAway.P.Team + ") out", build()); Program.ForceOut.Remove(keyAway.P.Pid); }
            if (keyHome != null) { Program.ForceOut.Add(keyHome.P.Pid); line(keyHome.P.Name + " (" + keyHome.P.Team + ") out", build()); Program.ForceOut.Remove(keyHome.P.Pid); }
            foreach (var q in qs.Take(3)) { Program.ForceOut.Add(q.Pid); line(q.Name + " (" + q.Team + ", questionable) out", build()); Program.ForceOut.Remove(q.Pid); }
            log("=== STRESS TEST " + matchup + " (" + sims.ToString("N0", U.IC) + " sims per scenario) ===");
            foreach (var r in rows) log(r);
            var wins = rows.Select(r => System.Text.RegularExpressions.Regex.Match(r, " win +([0-9.]+)%")).Where(m => m.Success).Select(m => double.Parse(m.Groups[1].Value, U.IC)).ToList();
            if (wins.Count > 0) log(string.Format(U.IC, "{0} win range across scenarios: {1:0.0}% to {2:0.0}%; pick holds in {3} of {4}.", baseG.Def.Away, wins.Min(), wins.Max(), wins.Count(w => (w >= 50) == (1 - baseG.WinH >= 0.5)), wins.Count));
        }

        // nbaquant-cli sgp4 DATE AWAY@HOME : four-leg parlays for one game in three risk bands, saved for grading
        public static void FourLeg(DateTime d, string matchup, int sims)
        {
            var log = Program.Log;
            var parts = matchup.ToUpperInvariant().Split('@');
            if (parts.Length == 2 && Ban.Involves(parts[0], parts[1])) { log(matchup + ": " + Ban.Who(parts[0], parts[1]) + " banned, no tickets (information only)."); return; }
            Program.BuildSlate(d, true);
            Program.Games =Program.Games.Where(g => g.Def.Away == parts[0] && g.Def.Home == parts[1]).ToList();
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

        // nbaquant-cli ticket DATE : price your own same-game parlays listed in overrides\tickets_DATE.txt, one line each:
        //   AWAY@HOME | name | leg; leg; ...     legs as the reports word them ("Luka Doncic 30+ pts", "LAL moneyline", "LAL spread", "Over")
        //   AWAY@HOME | vs | Player A; Player B  head-to-head: how often each one has more points, rebounds, assists and PRA
        // Results go to ledger\tickets_DATE.txt and to the predictions file (tag "ticket"), so they are graded with the slate.
        public static void Ticket(DateTime d, int sims)
        {
            var log = Program.Log;
            string src = Program.P(@"overrides\tickets_" + d.ToString("yyyy-MM-dd") + ".txt");
            if (!File.Exists(src)) { log("No tickets file: " + src); return; }
            var lines = File.ReadAllLines(src).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith("#"))
                .Select(x => x.Split('|').Select(f => f.Trim()).ToArray()).Where(f => f.Length >= 2).ToList();
            var want = new HashSet<string>(lines.Select(f => f[0].ToUpperInvariant()));
            foreach (var mu in want.Where(m => { var t = m.Split('@'); return t.Length == 2 && Ban.Involves(t[0], t[1]); }).ToList())
            { log(mu + ": banned, ticket not priced (information only)."); want.Remove(mu); }
            if (want.Count == 0) { log("No ticket games left to price."); return; }
            Program.BuildSlate(d, true);
            Program.Games = Program.Games.Where(g => want.Contains(g.Def.Away + "@" + g.Def.Home)).ToList();
            if (Program.Games.Count == 0) { log("None of the ticket games are still to be played."); return; }
            Program.Simulate(sims, false);
            Analysis.Run(false);
            var outp = new List<string> { string.Format(U.IC, "Custom tickets for {0}, priced {1} ET ({2:N0} simulations)", d.ToString("yyyy-MM-dd"), DateTime.Now.ToString("HH:mm"), sims) };
            var preds = new List<Pred>();
            foreach (var gm in Program.Games)
            {
                string mu = gm.Def.Away + "@" + gm.Def.Home;
                var legs = Analysis.BuildLegs(gm, true);
                Func<string, Leg> find = sp =>
                {
                    var l = legs.FirstOrDefault(x => string.Equals(x.Desc, sp, StringComparison.OrdinalIgnoreCase));
                    if (l != null) return l;
                    string lo = sp.ToLowerInvariant();
                    if (lo == "over" || lo == "under") return legs.FirstOrDefault(x => x.Kind == "TOT" && x.Team == sp.ToUpperInvariant());
                    if (lo.EndsWith(" spread")) return legs.FirstOrDefault(x => x.Kind == "SPR" && x.Team == sp.Substring(0, sp.Length - 7).Trim().ToUpperInvariant());
                    return null;
                };
                outp.Add("");
                outp.Add(string.Format(U.IC, "{0}: {1} win {2}, {3} win {4} | DraftKings {3} {5:+0.0;-0.0}, total {6}, ML {1} {7} / {3} {8}",
                    mu, gm.Def.Away, U.Pct(1 - gm.WinH), gm.Def.Home, U.Pct(gm.WinH), gm.Def.DK.SprH, gm.Def.DK.Total, U.Am(gm.Def.DK.MlA), U.Am(gm.Def.DK.MlH)));
                foreach (var f in lines.Where(f => f[0].ToUpperInvariant() == mu))
                {
                    string name = f.Length >= 3 ? f[1] : "";
                    var specs = f[f.Length - 1].Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    if (name.ToLowerInvariant() == "vs") { outp.AddRange(HeadToHead(gm, specs)); continue; }
                    var ls = specs.Select(find).ToList();
                    var missing = specs.Where((sp, i) => ls[i] == null).ToList();
                    if (missing.Count > 0 || ls.Count == 0) { outp.Add("  " + name + ": not priced, unknown leg(s): " + string.Join(", ", missing)); continue; }
                    double jp = Analysis.JointP(gm.Res.N, ls.ToArray()), prod = ls.Aggregate(1.0, (x, l) => x * l.P);
                    outp.Add(string.Format(U.IC, "  {0}: {1:0.0}% (fair {2}, {3:0.00}x the legs multiplied) = {4}", name == "" ? "ticket" : name, 100 * jp, U.Am(U.FairAmerican(jp)), jp / prod,
                        string.Join(" + ", ls.Select(l => l.Desc + " [" + U.Pct(l.P) + "]"))));
                    preds.Add(new Pred { Date = d.ToString("yyyy-MM-dd"), GameId = gm.Def.EspnId, Game = gm.Def.Away + " @ " + gm.Def.Home, Kind = "SGP", P = jp, Mean = prod,
                        Legs = string.Join(";", ls.Select(Predictions.LegCode)), Tag = "ticket", Selection = (name == "" ? "" : name + ": ") + string.Join(" + ", ls.Select(l => l.Desc)) });
                }
            }
            foreach (var o in outp) log(o);
            File.WriteAllLines(Program.P(@"ledger\tickets_" + d.ToString("yyyy-MM-dd") + ".txt"), outp, new UTF8Encoding(false));
            string pp = PredPath(d);
            var ids = new HashSet<string>(Program.Games.Select(g => g.Def.EspnId));
            var all = File.Exists(pp) ? Pred.Load(pp).Where(x => !(x.Tag == "ticket" && ids.Contains(x.GameId))).ToList() : new List<Pred>();
            all.AddRange(preds);
            Pred.Save(pp, all);
            log("Saved " + preds.Count + " custom tickets to " + pp + " for grading.");
        }

        // who has the bigger night, in the simulations where both players take the floor
        static List<string> HeadToHead(GameCtx gm, List<string> names)
        {
            Func<string, PStat> get = nm => Analysis.Players.FirstOrDefault(p => p.G == gm && string.Equals(p.P.Name, nm, StringComparison.OrdinalIgnoreCase));
            var a = names.Count >= 2 ? get(names[0]) : null; var b = names.Count >= 2 ? get(names[1]) : null;
            if (a == null || b == null) return new List<string> { "  head-to-head: player not found (" + string.Join(", ", names) + ")" };
            var r = gm.Res; int n = r.N, both = 0;
            var stats = new[] { "points", "rebounds", "assists", "PRA" };
            var aw = new int[4]; var bw = new int[4]; var am = new double[4]; var bm = new double[4];
            for (int s = 0; s < n; s++)
            {
                if (!r.Played[a.Gi][s] || !r.Played[b.Gi][s]) continue;
                both++;
                double[] va = { r.Pts[a.Gi][s], r.Reb[a.Gi][s], r.Ast[a.Gi][s], 0 }, vb = { r.Pts[b.Gi][s], r.Reb[b.Gi][s], r.Ast[b.Gi][s], 0 };
                va[3] = va[0] + va[1] + va[2]; vb[3] = vb[0] + vb[1] + vb[2];
                for (int k = 0; k < 4; k++) { if (va[k] > vb[k]) aw[k]++; else if (vb[k] > va[k]) bw[k]++; am[k] += va[k]; bm[k] += vb[k]; }
            }
            var res = new List<string> { string.Format(U.IC, "  Head-to-head {0} vs {1} ({2:N0} simulations with both playing):", a.P.Name, b.P.Name, both) };
            for (int k = 0; k < 4 && both > 0; k++)
                res.Add(string.Format(U.IC, "    {0,-8} {1} more {2}, {3} more {4}, tie {5} | average {6:0.0} vs {7:0.0}", stats[k], a.P.Name, U.Pct((double)aw[k] / both),
                    b.P.Name, U.Pct((double)bw[k] / both), U.Pct(1 - (double)(aw[k] + bw[k]) / both), am[k] / both, bm[k] / both));
            return res;
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
                    case "ticket": AppOps.Ticket(date(1), sims); break;
                    case "stress": AppOps.Stress(date(1), args[2], sims); break;
                    case "live": AppOps.Live(date(1), args[2], sims); break;
                    case "next": Console.WriteLine(AppOps.NextSlateDate().ToString("yyyy-MM-dd")); break;
                    // banned teams: "ban CLE: reason", "unban CLE", "banned" (the watch report)
                    case "ban": Console.WriteLine(Ban.Add(string.Join(" ", args.Skip(1)))); break;
                    case "unban": Console.WriteLine(Ban.Remove(string.Join(" ", args.Skip(1)))); break;
                    case "banned": Console.WriteLine(Ban.Watch()); break;
                    default: Console.WriteLine("usage: nbaquant-cli run [yyyy-MM-dd|next] [--sims N] | grade [yyyy-MM-dd] | recalibrate | next | ban TEAM[: reason] | unban TEAM | banned"); return 1;
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
