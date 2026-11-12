using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NbaQuant
{
    // The last graded slate as website data: who the model had, who won, what happened inside each game (leads and
    // comebacks, quarter swings, three-point shooting against each team's season norm, whistles, technicals, flagrants,
    // ejections, players whose minutes were cut short) and which player calls hit or missed, with the reason.
    static partial class Report
    {
        public static string LatestGradedDate()
        {
            var dir = Program.P("ledger"); if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "graded_*.csv").Select(f => Path.GetFileNameWithoutExtension(f).Substring(7)).OrderBy(x => x, StringComparer.Ordinal).LastOrDefault();
        }

        static string Ord(int q) { return q == 1 ? "1st" : q == 2 ? "2nd" : q == 3 ? "3rd" : "4th"; }

        public static object ReviewJson()
        {
            string date = LatestGradedDate(); if (date == null) return null;
            var preds = Pred.Load(Program.P(@"ledger\graded_" + date + ".csv"));
            var ids = preds.Where(p => p.Kind == "ML").Select(p => p.GameId).Distinct().ToList();
            // each team's season three-point rate from every game in the bank
            var tp = new Dictionary<string, double[]>();
            foreach (var bg in Bank.Games) foreach (var b in bg.Box.Where(x => x.Played)) { double[] v; if (!tp.TryGetValue(b.Team, out v)) tp[b.Team] = v = new double[2]; v[0] += b.Tpm; v[1] += b.Tpa; }
            var games = new List<object>(); var allLines = new List<Tuple<RvLine, double, string>>();
            int favWon = 0, n = 0; double exp = 0, brier = 0, brierMkt = 0; int nMkt = 0;
            foreach (var id in ids)
            {
                var g = Bank.Games.FirstOrDefault(x => x.Id == id) ?? Store.Summary(id, true);
                if (g == null || !g.Completed) continue;
                var gp = preds.Where(p => p.GameId == id).ToList();
                var mlRows = gp.Where(p => p.Kind == "ML" && !double.IsNaN(p.P)).OrderByDescending(p => p.P).ToList(); if (mlRows.Count == 0) continue;
                var ml = mlRows[0]; bool won = ml.Y >= 1; n++; if (won) favWon++; exp += ml.P; brier += (ml.P - ml.Y) * (ml.P - ml.Y);
                if (!double.IsNaN(ml.PMarket)) { brierMkt += (ml.PMarket - ml.Y) * (ml.PMarket - ml.Y); nMkt++; }
                int margin = g.HomePts - g.AwayPts, total = g.HomePts + g.AwayPts; bool homeWon = margin > 0;
                string winner = homeWon ? g.Home : g.Away, loser = homeWon ? g.Away : g.Home;
                var mm = gp.FirstOrDefault(p => p.Kind == "MEAN" && p.Stat == "MARGIN"); var mt = gp.FirstOrDefault(p => p.Kind == "MEAN" && p.Stat == "TOTAL");
                var spH = gp.FirstOrDefault(p => p.Kind == "SPREAD" && p.Team == g.Home); var to = gp.FirstOrDefault(p => p.Kind == "TOTAL" && p.Stat == "OVER");

                var story = new List<string>();
                int loserLead = homeWon ? g.AwayLead : g.HomeLead, winnerLead = homeWon ? g.HomeLead : g.AwayLead;
                if (loserLead >= 15) story.Add(winner + " came back from " + loserLead + " down");
                else if (loserLead <= 1 && winnerLead > 0) story.Add(loser + (loserLead == 0 ? " never led" : " led by 1 at most"));
                if (winnerLead >= 20 && Math.Abs(margin) <= 12) story.Add(winner + " led by " + winnerLead + " and " + loser + " closed the gap late");
                int bq = -1, bd = 0; for (int q = 0; q < 4; q++) { int d = g.HomeQ[q] - g.AwayQ[q]; if (Math.Abs(d) > Math.Abs(bd)) { bd = d; bq = q; } }
                if (bq >= 0 && Math.Abs(bd) >= 12) story.Add(string.Format(U.IC, "{0} won the {1} quarter {2}-{3}", bd > 0 ? g.Home : g.Away, Ord(bq + 1), bd > 0 ? g.HomeQ[bq] : g.AwayQ[bq], bd > 0 ? g.AwayQ[bq] : g.HomeQ[bq]));
                if (g.OT > 0) story.Add("went to overtime");
                var teamStats = new List<object>();
                foreach (var t in new[] { g.Away, g.Home })
                {
                    var bx = g.Box.Where(b => b.Team == t && b.Played).ToList();
                    int tpm = bx.Sum(b => b.Tpm), tpa = bx.Sum(b => b.Tpa), fta = bx.Sum(b => b.Fta), ftm = bx.Sum(b => b.Ftm), pf = bx.Sum(b => b.Pf), tov = bx.Sum(b => b.Tov), reb = bx.Sum(b => b.Reb);
                    double[] v; double seasonPct = tp.TryGetValue(t, out v) && v[1] > 0 ? v[0] / v[1] : 0.36;
                    double swing = (tpm - tpa * seasonPct) * 3;
                    if (Math.Abs(swing) >= 9) story.Add(string.Format(U.IC, "{0} shot {1}-{2} from three ({3:0}%, season {4:0}%): about {5:+0;-0} points against their norm", t, tpm, tpa, tpa > 0 ? 100.0 * tpm / tpa : 0, 100 * seasonPct, swing));
                    teamStats.Add(O("team", t, "tpm", tpm, "tpa", tpa, "seasonTp", R3(seasonPct), "ftm", ftm, "fta", fta, "pf", pf, "tov", tov, "reb", reb, "lead", t == g.Home ? g.HomeLead : g.AwayLead, "tech", t == g.Home ? g.HomeTech : g.AwayTech));
                }
                Func<string, Func<BoxLine, int>, int> tsum = (t, f) => g.Box.Where(b => b.Team == t && b.Played).Sum(f);
                int ftaH = tsum(g.Home, b => b.Fta), ftaA = tsum(g.Away, b => b.Fta), pfH = tsum(g.Home, b => b.Pf), pfA = tsum(g.Away, b => b.Pf), toH = tsum(g.Home, b => b.Tov), toA = tsum(g.Away, b => b.Tov);
                if (Math.Abs(ftaH - ftaA) >= 10) story.Add(string.Format(U.IC, "free throws {0} {1}, {2} {3} (fouls called: {4} {5}, {6} {7})", g.Away, ftaA, g.Home, ftaH, g.Away, pfA, g.Home, pfH));
                if (Math.Abs(toH - toA) >= 7) story.Add(string.Format(U.IC, "turnovers {0} {1}, {2} {3}", g.Away, toA, g.Home, toH));
                var ejected = new HashSet<string>(g.Events.Where(e => e.Type == "ejection").Select(e => e.Pid));
                var events = g.Events.Where(e => e.Type != "technical" || !e.Text.ToLowerInvariant().Contains("double")).Select(e => (object)O("type", e.Type, "q", e.Period, "clock", e.Clock, "text", e.Text, "team", e.Team, "away", e.ScoreA, "home", e.ScoreH)).ToList();
                foreach (var e in g.Events.Where(e => e.Type == "ejection")) story.Add(string.Format(U.IC, "{0} (Q{1} {2})", e.Text, e.Period, e.Clock));

                // players: projections (MEAN rows) joined to the final box
                var lines = new Dictionary<string, RvLine>();
                foreach (var p in gp.Where(p => p.Kind == "MEAN" && !string.IsNullOrEmpty(p.Pid)))
                {
                    RvLine x; if (!lines.TryGetValue(p.Pid, out x)) lines[p.Pid] = x = new RvLine { Pid = p.Pid, Name = p.Player, Team = p.Team };
                    if (p.Stat == "MIN") x.PMin = p.Mean; else if (p.Stat == "PTS") x.PPts = p.Mean; else if (p.Stat == "REB") x.PReb = p.Mean; else if (p.Stat == "AST") x.PAst = p.Mean; else if (p.Stat == "3PM") x.P3 = p.Mean;
                }
                foreach (var b in g.Box)
                {
                    RvLine x;
                    if (lines.TryGetValue(b.Pid, out x)) x.B = b;
                    else if (b.Played && b.Min >= 10) lines[b.Pid] = new RvLine { Pid = b.Pid, Name = b.Name, Team = b.Team, B = b };
                }
                Func<RvLine, double> tmg = x => x.Team == g.Home ? margin : -margin;
                Func<RvLine, string> cause = x =>
                {
                    if (ejected.Contains(x.Pid)) return string.Format(U.IC, "ejected after {0:0} minutes", x.B != null ? x.B.Min : 0);
                    string c = RvCause(x, tmg(x));
                    if (x.Projected && x.B != null && x.B.Played && x.PMin >= 20 && x.B.Min <= 0.5 * x.PMin && x.B.Pf < 5 && Math.Abs(margin) < 15) c = string.Format(U.IC, "left early or held out ({0:0} min vs {1:0} projected)", x.B.Min, x.PMin);
                    return c;
                };
                foreach (var x in lines.Values.Where(x => x.Projected && x.PMin >= 22 && x.B != null && x.B.Played && x.B.Min <= 0.6 * x.PMin))
                    story.Add(string.Format(U.IC, "{0} played {1:0} of a projected {2:0} minutes: {3}", x.Name, x.B.Min, x.PMin, cause(x)));
                foreach (var x in lines.Values.Where(x => x.Projected && x.PMin >= 18 && (x.B == null || !x.B.Played))) story.Add(string.Format(U.IC, "{0} was projected for {1:0} minutes and did not play", x.Name, x.PMin));
                foreach (var x in lines.Values) allLines.Add(Tuple.Create(x, tmg(x), g.Away + " @ " + g.Home));
                var box = lines.Values.Where(x => (x.Projected && x.PMin >= 14) || (x.B != null && x.B.Played && x.B.Min >= 14))
                    .OrderBy(x => x.Team == g.Home).ThenByDescending(x => x.B != null && x.B.Played ? x.B.Min : -1)
                    .Select(x =>
                    {
                        bool pl = x.B != null && x.B.Played;
                        return (object)O("name", x.Name, "team", x.Team, "pMin", x.Projected ? (object)R1(x.PMin) : null, "min", pl ? (object)Math.Round(x.B.Min) : null,
                            "pPts", x.Projected ? (object)R1(x.PPts) : null, "pts", pl ? (object)x.B.Pts : null, "pReb", x.Projected ? (object)R1(x.PReb) : null, "reb", pl ? (object)x.B.Reb : null,
                            "pAst", x.Projected ? (object)R1(x.PAst) : null, "ast", pl ? (object)x.B.Ast : null, "p3", x.Projected ? (object)R1(x.P3) : null, "tpm", pl ? (object)x.B.Tpm : null,
                            "fg", pl ? x.B.Fgm + "-" + x.B.Fga : "", "pf", pl ? x.B.Pf : 0, "cause", cause(x));
                    }).ToList();

                // player threshold calls by confidence, favorites and tickets
                var props = gp.Where(p => p.Kind == "PLAYER" && PropStats.Contains(p.Stat) && !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.P)).ToList();
                var hi = props.Where(p => p.P >= 0.8).ToList(); var mid = props.Where(p => p.P >= 0.6 && p.P < 0.8).ToList();
                var favs = gp.Where(p => p.Kind == "FAV" && !p.Void && !double.IsNaN(p.Y)).ToList();
                var hiMiss = hi.Where(p => p.Y < 1).OrderByDescending(p => p.P).Take(6).Select(p => (object)O("sel", p.Selection, "p", R3(p.P), "actual", double.IsNaN(p.Actual) ? null : (object)p.Actual)).ToList();
                var tickets = gp.Where(p => p.Kind == "SGP" && !p.Void && !double.IsNaN(p.Y) && (p.Tag == "sgp4_safest" || p.Tag == "best_sgp" || p.Tag == "ticket")).Select(p =>
                {
                    var missed = new List<string>();
                    if (!string.IsNullOrEmpty(p.Legs))
                    {
                        var codes = p.Legs.Split(';'); var descs = p.Selection.Split(new[] { " + " }, StringSplitOptions.None);
                        for (int i = 0; i < codes.Length; i++) { var f = codes[i].Split('|'); if (f.Length < 4) continue; double y = Grader.Resolve(f[0], f[0], f[1], f[2], U.D(f[3]), g); if (!(y >= 1)) missed.Add(i < descs.Length ? descs[i] : codes[i]); }
                    }
                    return (object)O("tag", p.Tag, "sel", p.Selection, "p", R3(p.P), "hit", p.Y >= 1, "missed", missed);
                }).ToList();

                games.Add(O("id", id, "away", g.Away, "home", g.Home, "awayPts", g.AwayPts, "homePts", g.HomePts, "ot", g.OT, "awayQ", g.AwayQ, "homeQ", g.HomeQ,
                    "fav", ml.Team, "p", R3(ml.P), "dk", double.IsNaN(ml.PMarket) ? null : (object)R3(ml.PMarket), "won", won,
                    "marginModel", mm != null ? (object)R1(mm.Mean) : null, "marginDk", spH != null ? (object)(-spH.Line) : null, "margin", margin,
                    "totalModel", mt != null ? (object)R1(mt.Mean) : null, "totalDk", to != null ? (object)to.Line : null, "total", total,
                    "officials", g.Officials, "teams", teamStats, "story", story, "events", events, "box", box,
                    "hiN", hi.Count, "hiHit", hi.Count(p => p.Y >= 1), "midN", mid.Count, "midHit", mid.Count(p => p.Y >= 1), "hiMiss", hiMiss,
                    "favN", favs.Count, "favHit", favs.Count(p => p.Y >= 1),
                    "favList", favs.Where(p => p.Tag == "fav_bold").OrderByDescending(p => p.P).Take(10).Select(p => (object)O("sel", p.Selection, "p", R3(p.P), "hit", p.Y >= 1, "actual", double.IsNaN(p.Actual) ? null : (object)p.Actual)).ToList(),
                    "tickets", tickets));
            }
            if (n == 0) return null;
            var played = allLines.Where(t => t.Item1.Projected && t.Item1.B != null && t.Item1.B.Played).ToList();
            var main = played.Where(t => t.Item1.PMin >= 15).ToList();
            Func<Tuple<RvLine, double, string>, object> surprise = t =>
            {
                var x = t.Item1;
                return O("name", x.Name, "team", x.Team, "game", t.Item3, "pPts", R1(x.PPts), "pts", x.B.Pts, "pMin", R1(x.PMin), "min", Math.Round(x.B.Min), "fg", x.B.Fgm + "-" + x.B.Fga, "tp", x.B.Tpm + "-" + x.B.Tpa, "cause", RvCause(x, t.Item2));
            };
            var allProps = preds.Where(p => p.Kind == "PLAYER" && PropStats.Contains(p.Stat) && !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.P)).ToList();
            Func<double, double, object> band = (lo, hi2) => { var b = allProps.Where(p => p.P >= lo && p.P < hi2).ToList(); return O("lo", lo, "hi", hi2, "n", b.Count, "hit", b.Count(p => p.Y >= 1), "exp", R1(b.Sum(p => p.P))); };
            var byStat = PropStats.Select(st => { var b = allProps.Where(p => p.Stat == st && p.P >= 0.5).ToList(); return (object)O("stat", st, "n", b.Count, "hit", b.Count(p => p.Y >= 1), "exp", R1(b.Sum(p => p.P))); }).ToList();
            var safest = preds.Where(p => p.Tag == "sgp4_safest" && !p.Void && !double.IsNaN(p.Y)).ToList();
            var favAll = preds.Where(p => p.Kind == "FAV" && !p.Void && !double.IsNaN(p.Y)).ToList();
            return O("date", date, "label", DateTime.ParseExact(date, "yyyy-MM-dd", U.IC).ToString("dddd, MMMM d", U.IC), "games", games, "n", n, "favWon", favWon, "exp", R1(exp),
                "brier", R3(brier / n), "brierDk", nMkt > 0 ? (object)R3(brierMkt / nMkt) : null,
                "bands", new List<object> { band(0.9, 1.01), band(0.8, 0.9), band(0.7, 0.8), band(0.6, 0.7), band(0.5, 0.6) }, "byStat", byStat,
                "ptsMae", main.Count > 0 ? R1(main.Average(t => Math.Abs(t.Item1.DPts))) : 0, "minMae", main.Count > 0 ? R1(main.Average(t => Math.Abs(t.Item1.B.Min - t.Item1.PMin))) : 0,
                "minBias", main.Count > 0 ? R1(main.Average(t => t.Item1.B.Min - t.Item1.PMin)) : 0, "mainN", main.Count,
                "safestN", safest.Count, "safestHit", safest.Count(p => p.Y >= 1), "safestExp", R1(safest.Sum(p => p.P)),
                "favN", favAll.Count, "favHit", favAll.Count(p => p.Y >= 1), "favExp", R1(favAll.Sum(p => p.P)),
                "over", played.OrderByDescending(t => t.Item1.DPts).Take(8).Select(surprise).ToList(), "under", played.OrderBy(t => t.Item1.DPts).Take(8).Select(surprise).ToList());
        }
    }
}
