using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NbaQuant
{
    // Projected box score per game: each player's average line over the simulations, with ranges and season form.
    static partial class Report
    {
        static void BoxScoresSheet()
        {
            var sh = New("Box Scores", "Projected box scores: average lines over " + Program.NSIM.ToString("N0", U.IC) + " simulations per game",
                "Player lines are averages over the simulations in which he plays (how the box score reads if he suits up); Play % is the share of simulations he logs minutes. * = projected starter (top-5 projected minutes). Team totals weight each line by Play %. Ranges are 10th-90th percentile nights.",
                26, 8, 7, 7, 7, 7, 7, 7, 11, 11, 11, 8, 30);
            var txt = new StringBuilder();
            foreach (var g in Program.Games)
            {
                var r = g.Res; double n = r.N;
                double ptsH = r.SumPtsH / n, ptsA = r.SumPtsA / n;
                string head = string.Format(U.IC, "{0} @ {1}: projected {0} {2:0.0} - {1} {3:0.0} | {1} win {4} | total {5:0.0} (P10 {6} to P90 {7}) | DK {1} {8:+0.0;-0.0}, total {9}",
                    g.Def.Away, g.Def.Home, ptsA, ptsH, U.Pct(g.WinH), g.MeanTotal, g.TotalPct[0], g.TotalPct[4], g.Def.DK.SprH, g.Def.DK.Total);
                Section(sh, head, 13);
                txt.AppendLine("=== " + head);
                foreach (bool home in new[] { false, true })
                {
                    string team = home ? g.Def.Home : g.Def.Away;
                    var ps = Analysis.Players.Where(p => p.G == g && p.Home == home && p.PPlays * p.MeanMin >= 3).OrderByDescending(p => p.PPlays * p.MeanMin).ToList();
                    Hdr(sh, team, "Play %", "MIN", "PTS", "REB", "AST", "3PM", "PRA", "PTS range", "REB range", "AST range", "P(DD)", "Season (GP): PTS / REB / AST");
                    int k = 0;
                    foreach (var p in ps)
                    {
                        k++;
                        var f = FormOf(p);
                        string season = f == null ? "-" : string.Format(U.IC, "{0:0.0} / {1:0.0} / {2:0.0} ({3})", f.Ppg, f.Rpg, f.Apg, f.FromPrior ? "last season" : f.Gp + " GP");
                        var row = sh.Row();
                        row.Add(Cell.Str(p.P.Name + (k <= 5 ? " *" : ""), k <= 5 ? Styles.Bold : Styles.Def)); row.Add(Cell.Num(p.PPlays, Styles.Pct)); row.Add(Cell.Num(p.MeanMin, Styles.N1));
                        foreach (var d in new[] { p.Pts, p.Reb, p.Ast, p.Tpm, p.Pra }) row.Add(Cell.Num(d.Mean, Styles.N1));
                        foreach (var d in new[] { p.Pts, p.Reb, p.Ast }) row.Add(Cell.Str(d.P10 + " - " + d.P90));
                        row.Add(Cell.Num(p.DD, Styles.Pct)); row.Add(Cell.Str(season));
                        txt.AppendLine(string.Format(U.IC, "{0}|{1}{2}|{3:0}%|{4:0.0}|{5:0.0}|{6:0.0}|{7:0.0}|{8:0.0}|{9}-{10}|{11:0}%|{12}", team, p.P.Name, k <= 5 ? " *" : "", 100 * p.PPlays, p.MeanMin, p.Pts.Mean, p.Reb.Mean, p.Ast.Mean, p.Tpm.Mean, p.Pts.P10, p.Pts.P90, 100 * p.DD, season));
                    }
                    double tPts = ps.Sum(p => p.PPlays * p.Pts.Mean), tReb = ps.Sum(p => p.PPlays * p.Reb.Mean), tAst = ps.Sum(p => p.PPlays * p.Ast.Mean), tTpm = ps.Sum(p => p.PPlays * p.Tpm.Mean);
                    var tr = sh.Row();
                    tr.Add(Cell.Str(team + " team (simulated)", Styles.Bold)); tr.Add(Cell.Str("")); tr.Add(Cell.Num(240, Styles.Int));
                    tr.Add(Cell.Num(home ? ptsH : ptsA, Styles.N1)); tr.Add(Cell.Num(tReb, Styles.N1)); tr.Add(Cell.Num(home ? r.SumAstH / n : r.SumAstA / n, Styles.N1)); tr.Add(Cell.Num(tTpm, Styles.N1));
                    var outs = Program.TeamPlayers[team].Where(x => x.Status == "OUT" && Math.Max(x.CsMpg, x.PsMpg) >= 12).Select(x => x.Name).ToList();
                    tr.Add(Cell.Str("")); tr.Add(Cell.Str("")); tr.Add(Cell.Str("")); tr.Add(Cell.Str("")); tr.Add(Cell.Str(""));
                    tr.Add(Cell.Str(outs.Count > 0 ? "Out: " + string.Join(", ", outs) : "No rotation players out", Styles.Wrap));
                    txt.AppendLine(string.Format(U.IC, "{0}|TEAM|{1:0.0} pts (players {2:0.0})|{3:0.0} reb|{4:0.0} ast|{5:0.0} 3pm|out: {6}", team, home ? ptsH : ptsA, tPts, tReb, home ? r.SumAstH / n : r.SumAstA / n, tTpm, outs.Count > 0 ? string.Join(", ", outs) : "none"));
                }
            }
            Note(sh, "Rebound totals exclude team rebounds, which a real box score also leaves off player lines. Projections assume the injury statuses on the Verification table; re-run after the final injury report.");
            File.WriteAllText(Program.P(@"data\box_scores.txt"), txt.ToString());
        }

        // Does this season's data bank back the call? The model fades hot starts and has season-long misses on some players,
        // so an erupt call on a player it keeps over-projecting (or a dud call on a breakout) is flagged as a disagreement.
        public static string BankCheck(PStat p, bool erupt)
        {
            var b = Bank.Player(p.P.Pid); int up = 0, down = 0;
            if (b != null)
            {
                if (b.ModelN >= 3 && b.ModelPtsBias <= -2) up++;        // model has been too low on him
                if (b.ModelN >= 3 && b.ModelPtsBias >= 2) down++;       // model has been too high on him
                foreach (var fl in b.Flags)
                {
                    if (fl.StartsWith("minutes rising") || fl.StartsWith("scoring up") || fl.StartsWith("moved into")) up++;
                    if (fl.StartsWith("minutes falling") || fl.StartsWith("scoring down") || fl.StartsWith("moved to the bench")) down++;
                }
                if (b.Gp >= 2 && p.MeanMin >= b.Mpg + 4) up++;
                if (b.Gp >= 2 && p.MeanMin <= b.Mpg - 4) down++;
            }
            int agree = erupt ? up : down, against = erupt ? down : up;
            return agree > 0 && against > 0 ? "mixed" : agree > 0 ? "bank agrees" : against > 0 ? "bank disagrees" : "no signal";
        }

        // Who could erupt and who could be a dud tonight, measured against each player's own 2026-27 scoring, with the
        // trends behind it. Lists carry the track-record factor once the backtest has graded enough of them.
        static void EruptDudSheet()
        {
            var sh = New("Erupt or Dud", "Who could erupt, who could be a dud (against this season's scoring)",
                "Erupt = scores 1.5x his 2026-27 average or 6 more, whichever is higher. Dud = half his average or less. Chances come from the 100,000 simulations (games he plays). Trends come from the season data bank. Both lists are graded every day.",
                24, 6, 6, 8, 8, 9, 9, 10, 10, 9, 70);
            var rot = Rotations().ToDictionary(x => x.Ps, x => x);
            var txt = new StringBuilder();
            Action<string, string, List<PStat>, bool> table = (title, tag, list, erupt) =>
            {
                var f = TrackAdjust.Of(tag);
                Section(sh, title, 11);
                if (f.Item2 > 0) Note(sh, string.Format(U.IC, "Track record so far: scaled by {0:0.00} ({1} backtest calls).", f.Item1, f.Item2));
                Hdr(sh, "Player", "Team", "Opp", "Season PPG", "Proj pts", "Most common", erupt ? "Erupt line" : "Dud line", erupt ? "P(erupt)" : "P(dud)", "Season MIN", "Tonight MIN", "Bank check: why");
                foreach (var p in list)
                {
                    double pr = TrackAdjust.Adj(tag, erupt ? p.PErupt : p.PDud);
                    var why = new List<string>(); RotRow rr; if (rot.TryGetValue(p, out rr)) why.AddRange(rr.Flags);
                    var fm = FormOf(p);
                    var r = sh.Row();
                    r.Add(Cell.Str(p.P.Name, Styles.Bold)); r.Add(Cell.Str(p.P.Team)); r.Add(Cell.Str(p.Opp)); r.Add(Cell.Num(p.SeasonPpg, Styles.N1)); r.Add(Cell.Num(p.Pts.Mean, Styles.N1));
                    int mode = 0; for (int v = 1; v < p.Pts.Hist.Length; v++) if (p.Pts.Hist[v] > p.Pts.Hist[mode]) mode = v;
                    r.Add(Cell.Num(mode, Styles.Int)); r.Add(Cell.Str((erupt ? p.EruptLine + "+" : p.DudLine + " or fewer"))); r.Add(Cell.Num(pr, Styles.PctB));
                    r.Add(fm == null ? Cell.Str("-") : Cell.Num(fm.Mpg, Styles.N1)); r.Add(Cell.Num(p.MeanMin, Styles.N1));
                    string chk = BankCheck(p, erupt);
                    r.Add(Cell.Str(chk.ToUpperInvariant() + (why.Count > 0 ? ": " : "") + string.Join("; ", why.Where(w => !w.StartsWith("model below season"))), Styles.Wrap));
                    txt.AppendLine(string.Format(U.IC, "{0}|{1}|{2}|vs {3}|season {4:0.0}|proj {5:0.0}|mode {6}|{7}|{8:0}%|min {9:0.0}->{10:0.0}|{11}|{12}", erupt ? "ERUPT" : "DUD", p.P.Name, p.P.Team, p.Opp, p.SeasonPpg, p.Pts.Mean, mode,
                        erupt ? p.EruptLine + "+" : "<=" + p.DudLine, 100 * pr, fm == null ? double.NaN : fm.Mpg, p.MeanMin, chk, string.Join("; ", why.Where(w => !w.StartsWith("model below season")))));
                }
            };
            table("Erupt candidates: best chance to blow past their season scoring", "erupt", Analysis.EruptList(12), true);
            table("Dud risks: 10+ ppg scorers with the best chance of half their usual output or less", "dud", Analysis.DudList(12), false);
            Note(sh, "A role player's 1.5x line is only a few points, so bench names reach the erupt list more easily; the Why column shows whether minutes or form back it up.");
            File.WriteAllText(Program.P(@"data\erupt.txt"), txt.ToString());
        }

        class RotRow
        {
            public PStat Ps; public bool Starter; public SeasonForm F; public double Per36, Per36Prior = double.NaN, MinNow;
            public List<string> Flags = new List<string>(); public int Up;
        }

        // every rotation player, starters and bench: season form, trend vs last season, tonight's line and prop ladder
        static List<RotRow> Rotations()
        {
            var res = new List<RotRow>();
            foreach (var g in Program.Games)
                foreach (bool home in new[] { false, true })
                {
                    var ps = Analysis.Players.Where(p => p.G == g && p.Home == home && p.PPlays * p.MeanMin >= 3).OrderByDescending(p => p.PPlays * p.MeanMin).ToList();
                    int k = 0;
                    foreach (var p in ps)
                    {
                        k++;
                        var pl = p.P; var row = new RotRow { Ps = p, Starter = k <= 5, F = FormOf(p) };
                        row.Per36 = pl.Gp > 0 && pl.CsMin > 0 ? 36.0 * pl.CsBox.Sum(b => b.Pts) / pl.CsMin : double.NaN;
                        if (pl.HasPs && pl.Ps.MP > 100) row.Per36Prior = 36.0 * pl.Ps.PTS / pl.Ps.MP;
                        var f = row.F;
                        if (f != null && !f.FromPrior && f.Gp >= 2)
                        {
                            if (!double.IsNaN(row.Per36Prior) && f.Mpg >= 12 && row.Per36 >= 1.2 * row.Per36Prior && f.Ppg >= 6) { row.Flags.Add(string.Format(U.IC, "breakout: {0:0.0} pts per 36 vs {1:0.0} last season", row.Per36, row.Per36Prior)); row.Up += 2; }
                            if (pl.HasPs && f.Mpg >= pl.PsMpg + 5) { row.Flags.Add(string.Format(U.IC, "bigger role: {0:0.0} mpg vs {1:0.0} last season", f.Mpg, pl.PsMpg)); row.Up += 1; }
                            if (!pl.HasPs && f.Ppg >= 8) { row.Flags.Add(string.Format(U.IC, "first NBA season producing {0:0.0} ppg", f.Ppg)); row.Up += 1; }
                            if (!double.IsNaN(f.L3Pts) && f.Gp >= 3 && f.L3Pts >= f.Ppg + 3) { row.Flags.Add(string.Format(U.IC, "hot: {0:0.0} pts over last 3", f.L3Pts)); row.Up += 1; }
                            if (p.MeanMin >= f.Mpg + 4) { row.Flags.Add(string.Format(U.IC, "more minutes tonight: {0:0.0} vs {1:0.0}", p.MeanMin, f.Mpg)); row.Up += 1; }
                            if (f.Gp >= 3 && p.Pts.Mean <= f.Ppg - 3) row.Flags.Add(string.Format(U.IC, "model below season scoring ({0:0.0} vs {1:0.0}): prior-season data still weighs on it", p.Pts.Mean, f.Ppg));
                        }
                        // trends from the season data bank (minutes direction, lineup changes, the model's record on him)
                        var bp = Bank.Player(pl.Pid);
                        if (bp != null)
                            foreach (var fl in bp.Flags.Where(fl => !fl.StartsWith("scoring up") || !row.Flags.Any(r0 => r0.StartsWith("hot"))))
                            {
                                row.Flags.Add(fl);
                                if (fl.StartsWith("minutes rising") || fl.StartsWith("moved into") || fl.StartsWith("scoring up") || fl.StartsWith("model has under")) row.Up += 1;
                            }
                        res.Add(row);
                    }
                }
            return res;
        }

        static void RotationsSheet()
        {
            var rows = Rotations();
            var sh = New("Rotations & Breakouts", "Every rotation player, starters and bench: season form, breakouts and tonight's projection",
                "Season = 2026-27 to date (last season if fewer than 2 games). Per 36 = points per 36 minutes. Flags: breakout (scoring rate up 20%+ on last season), bigger role (5+ more minutes), hot (last 3 games 3+ above season), more minutes tonight (injuries/rotation), model below season scoring. * = projected starter.",
                24, 6, 8, 5, 7, 7, 7, 7, 7, 8, 8, 7, 7, 7, 7, 7, 8, 8, 8, 8, 8, 8, 70);
            string[] hdr = { "Player", "Team", "Role", "GP", "Season MIN", "Season PTS", "Season REB", "Season AST", "Season 3PM", "PTS/36 now", "PTS/36 last yr", "Last-3 PTS",
                "Tonight MIN", "Tonight PTS", "Tonight REB", "Tonight AST", "P(10+ pts)", "P(15+ pts)", "P(20+ pts)", "P(5+ reb)", "P(4+ ast)", "P(2+ 3PM)", "Flags" };
            Action<RotRow> add = x =>
            {
                var p = x.Ps; var f = x.F; var r = sh.Row();
                r.Add(Cell.Str(p.P.Name + (x.Starter ? " *" : ""), x.Up >= 2 ? Styles.Good : x.Starter ? Styles.Bold : Styles.Def)); r.Add(Cell.Str(p.P.Team)); r.Add(Cell.Str(x.Starter ? "Starter" : "Bench"));
                r.Add(f == null ? Cell.Str("-") : f.FromPrior ? Cell.Str("prior") : Cell.Num(f.Gp, Styles.Int));
                foreach (var v in f == null ? new[] { double.NaN, double.NaN, double.NaN, double.NaN, double.NaN } : new[] { f.Mpg, f.Ppg, f.Rpg, f.Apg, f.Tpm }) r.Add(ANum(v, Styles.N1));
                r.Add(ANum(x.Per36, Styles.N1)); r.Add(ANum(x.Per36Prior, Styles.N1)); r.Add(f == null ? Cell.Str("-") : ANum(f.L3Pts, Styles.N1));
                r.Add(Cell.Num(p.MeanMin, Styles.N1)); r.Add(Cell.Num(p.Pts.Mean, Styles.N1)); r.Add(Cell.Num(p.Reb.Mean, Styles.N1)); r.Add(Cell.Num(p.Ast.Mean, Styles.N1));
                foreach (var v in new[] { p.Pts.PAtLeast(10), p.Pts.PAtLeast(15), p.Pts.PAtLeast(20), p.Reb.PAtLeast(5), p.Ast.PAtLeast(4), p.Tpm.PAtLeast(2) }) r.Add(Cell.Num(v, Styles.Pct));
                r.Add(Cell.Str(string.Join("; ", x.Flags), Styles.Wrap));
            };
            var bench = rows.Where(x => !x.Starter && (x.Up > 0 || x.Ps.Pts.PAtLeast(10) >= 0.4)).OrderByDescending(x => x.Up).ThenByDescending(x => x.Ps.Pra.Mean).Take(15).ToList();
            Section(sh, "Bench come-ups: non-starters with rising roles or real scoring upside tonight", hdr.Length); Hdr(sh, hdr);
            foreach (var x in bench) add(x);
            var risers = rows.Where(x => x.Starter && x.Up >= 2).OrderByDescending(x => x.Up).ThenByDescending(x => x.Ps.Pra.Mean).ToList();
            if (risers.Count > 0) { Section(sh, "Starters on the rise (two or more positive flags)", hdr.Length); Hdr(sh, hdr); foreach (var x in risers) add(x); }
            foreach (var g in Program.Games)
                foreach (bool home in new[] { false, true })
                {
                    string team = home ? g.Def.Home : g.Def.Away;
                    Section(sh, team + " full rotation (" + g.Def.Away + " @ " + g.Def.Home + ")", hdr.Length); Hdr(sh, hdr);
                    foreach (var x in rows.Where(x => x.Ps.G == g && x.Ps.Home == home)) add(x);
                }
            Note(sh, "Green name = two or more positive flags. The walk-forward audit tests whether the model under-rates hot starts (Model_Audit, 'Projection vs season scoring'); season samples are 3-5 games, so a single big night moves these averages a lot.");
            var txt = new StringBuilder();
            foreach (var x in bench) txt.AppendLine(string.Format(U.IC, "BENCH|{0}|{1}|{2:0.0} min|{3:0.0} pts {4:0.0} reb {5:0.0} ast|season {6}|P10+ {7:0}%|{8}", x.Ps.P.Team, x.Ps.P.Name, x.Ps.MeanMin, x.Ps.Pts.Mean, x.Ps.Reb.Mean, x.Ps.Ast.Mean,
                x.F == null ? "-" : string.Format(U.IC, "{0:0.0}/{1:0.0}/{2:0.0} in {3:0.0} min", x.F.Ppg, x.F.Rpg, x.F.Apg, x.F.Mpg), 100 * x.Ps.Pts.PAtLeast(10), string.Join("; ", x.Flags)));
            foreach (var x in risers) txt.AppendLine(string.Format(U.IC, "RISER|{0}|{1}|{2:0.0} pts|{3}", x.Ps.P.Team, x.Ps.P.Name, x.Ps.Pts.Mean, string.Join("; ", x.Flags)));
            foreach (var x in rows.Where(x => x.Flags.Any(fl => fl.StartsWith("model below")))) txt.AppendLine(string.Format(U.IC, "FADED|{0}|{1}|{2}", x.Ps.P.Team, x.Ps.P.Name, x.Flags.First(fl => fl.StartsWith("model below"))));
            File.WriteAllText(Program.P(@"data\rotations.txt"), txt.ToString());
        }
    }
}
