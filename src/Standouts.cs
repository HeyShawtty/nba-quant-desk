using System;
using System.Collections.Generic;
using System.Linq;

namespace NbaQuant
{
    // Season-to-date form vs tonight's simulated projection, and the reasons behind the difference.
    class SeasonForm
    {
        public PStat Ps; public int Gp; public bool FromPrior;
        public double Mpg, Ppg, Rpg, Apg, Tpm, Tpa, TpPct, L3Pts, L3Reb, L3Ast, L3Tpm; public int DdGames;
        public double DPts, DReb, DAst, DTpm, Score; public string Lead, Why;
    }

    static partial class Report
    {
        public static SeasonForm FormOf(PStat ps)
        {
            var p = ps.P; var f = new SeasonForm { Ps = ps, Gp = p.Gp };
            if (p.Gp >= 2)
            {
                var bx = p.CsBox;
                f.Mpg = bx.Average(b => b.Min); f.Ppg = bx.Average(b => b.Pts); f.Rpg = bx.Average(b => b.Reb); f.Apg = bx.Average(b => b.Ast);
                f.Tpm = bx.Average(b => b.Tpm); f.Tpa = bx.Average(b => b.Tpa); f.TpPct = bx.Sum(b => b.Tpa) > 0 ? (double)bx.Sum(b => b.Tpm) / bx.Sum(b => b.Tpa) : double.NaN;
                var l3 = bx.Skip(Math.Max(0, bx.Count - 3)).ToList();
                f.L3Pts = l3.Average(b => b.Pts); f.L3Reb = l3.Average(b => b.Reb); f.L3Ast = l3.Average(b => b.Ast); f.L3Tpm = l3.Average(b => b.Tpm);
                f.DdGames = bx.Count(b => (b.Pts >= 10 ? 1 : 0) + (b.Reb >= 10 ? 1 : 0) + (b.Ast >= 10 ? 1 : 0) >= 2);
            }
            else if (p.HasPs && p.Ps.G > 0)
            {
                var b = p.Ps; double g = b.G; f.FromPrior = true;
                f.Mpg = b.MP / g; f.Ppg = b.PTS / g; f.Rpg = b.TRB / g; f.Apg = b.AST / g; f.Tpm = b.FG3 / g; f.Tpa = b.FG3A / g; f.TpPct = b.FG3A > 0 ? b.FG3 / b.FG3A : double.NaN;
                f.L3Pts = f.L3Reb = f.L3Ast = f.L3Tpm = double.NaN; f.DdGames = -1;
            }
            else return null;
            // standardized gap between tonight's projection and season form (Poisson-like scale)
            Func<double, double, double> z = (proj, avg) => (proj - avg) / Math.Sqrt(Math.Max(1.0, avg));
            f.DPts = ps.Pts.Mean - f.Ppg; f.DReb = ps.Reb.Mean - f.Rpg; f.DAst = ps.Ast.Mean - f.Apg; f.DTpm = ps.Tpm.Mean - f.Tpm;
            var zs = new[] { Tuple.Create("points", z(ps.Pts.Mean, f.Ppg)), Tuple.Create("rebounds", z(ps.Reb.Mean, f.Rpg)), Tuple.Create("assists", z(ps.Ast.Mean, f.Apg)), Tuple.Create("threes", z(ps.Tpm.Mean, f.Tpm) * 1.5) };
            var top = zs.OrderByDescending(x => Math.Abs(x.Item2)).First();
            f.Score = top.Item2; f.Lead = top.Item1;
            var why = new List<string>();
            double dm = ps.MeanMin - f.Mpg;
            if (Math.Abs(dm) >= 2.5)
            {
                var outs = Program.TeamPlayers[p.Team].Where(x => x.Status == "OUT" && Math.Max(x.CsMpg, x.PsMpg) >= 18).Select(x => x.Name).Take(3).ToList();
                why.Add(string.Format(U.IC, "minutes {0:0.0} vs {1:0.0} season{2}", ps.MeanMin, f.Mpg, dm > 0 && outs.Count > 0 ? " (" + string.Join(", ", outs) + " out)" : ""));
            }
            if (!string.IsNullOrEmpty(p.MatchupNote) && !p.MatchupNote.Contains("no notable")) why.Add(p.MatchupNote);
            double tm = ps.Home ? ps.G.BlendPtsH : ps.G.BlendPtsA;
            why.Add(string.Format(U.IC, "team projected {0:0.0} pts", tm));
            if (ps.G.PBlowout >= 0.3) why.Add(string.Format(U.IC, "blowout risk {0}", U.Pct(ps.G.PBlowout)));
            if (f.FromPrior) why.Add("fewer than 2 games this season: compared with last season");
            f.Why = string.Join("; ", why);
            return f;
        }

        public static List<SeasonForm> Forms() { return Analysis.Players.Where(p => p.MeanMin >= 18 && p.P.Status != "OUT").Select(FormOf).Where(f => f != null).ToList(); }

        static void StandoutsSheet()
        {
            var forms = Forms();
            var sh = New("Standouts", "Who stands out tonight: simulation vs season form, and why",
                "Each player's 100,000-game projection against tonight's opponent, compared with his 2026-27 averages (last season if he has fewer than 2 games). Gap = projection minus season average. Reasons list minutes changes, the opponent's defensive profile and game script.",
                22, 6, 6, 5, 7, 7, 7, 7, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 80);
            string[] hdr = { "Player", "Team", "Opp", "GP", "Season PTS", "Season REB", "Season AST", "Season 3PM", "Last-3 PTS", "Proj PTS", "Proj REB", "Proj AST", "Proj 3PM", "Gap PTS", "Gap REB", "Gap AST", "Gap 3PM", "P(DD)", "Why" };
            Action<string, IEnumerable<SeasonForm>> table = (title, list) =>
            {
                Section(sh, title, hdr.Length); Hdr(sh, hdr);
                foreach (var f in list)
                {
                    var r = sh.Row(); var ps = f.Ps;
                    r.Add(Cell.Str(ps.P.Name, Styles.Bold)); r.Add(Cell.Str(ps.P.Team)); r.Add(Cell.Str(ps.Opp)); r.Add(Cell.Num(f.Gp, Styles.Int));
                    r.Add(Cell.Num(f.Ppg, Styles.N1)); r.Add(Cell.Num(f.Rpg, Styles.N1)); r.Add(Cell.Num(f.Apg, Styles.N1)); r.Add(Cell.Num(f.Tpm, Styles.N1));
                    r.Add(double.IsNaN(f.L3Pts) ? Cell.Str("-") : Cell.Num(f.L3Pts, Styles.N1));
                    r.Add(Cell.Num(ps.Pts.Mean, Styles.N1)); r.Add(Cell.Num(ps.Reb.Mean, Styles.N1)); r.Add(Cell.Num(ps.Ast.Mean, Styles.N1)); r.Add(Cell.Num(ps.Tpm.Mean, Styles.N1));
                    foreach (var dv in new[] { f.DPts, f.DReb, f.DAst, f.DTpm }) r.Add(Cell.Num(dv, dv >= 1.0 ? Styles.Good : dv <= -1.0 ? Styles.Bad : Styles.N1));
                    r.Add(Cell.Num(ps.DD, Styles.Pct)); r.Add(Cell.Str(f.Why, Styles.Wrap));
                }
            };
            table("Rising tonight: projection above season form (largest standardized gap first)", forms.Where(f => f.Score > 0).OrderByDescending(f => f.Score).Take(15));
            table("Falling tonight: projection below season form", forms.Where(f => f.Score < 0).OrderBy(f => f.Score).Take(10));
            table("Top projected performers (by points + rebounds + assists)", forms.OrderByDescending(f => f.Ps.Pra.Mean).Take(15));
            Note(sh, "Gap colours: green = projection at least 1 above season average, red = at least 1 below. Season samples are 2-5 games: a big gap can mean the season average is noisy, not that tonight is special.");

            Section(sh, "Defensive profiles of tonight's teams (what opponents do against them; 1.00 = league average, shrunk toward average)", 13);
            Hdr(sh, "Team", "Games", "3PA share allowed", "3P% allowed", "2P% allowed", "FT rate allowed", "Turnovers forced", "Off. reb allowed", "Assist rate allowed", "Guard scoring", "Forward scoring", "Center scoring", "Opp pts/100 (raw)");
            foreach (var t in Program.Games.SelectMany(g => new[] { g.Def.Away, g.Def.Home }))
            {
                DefProfile d; if (!Program.Defense.TryGetValue(t, out d)) continue;
                var r = sh.Row();
                r.Add(Cell.Str(t, Styles.Bold)); r.Add(Cell.Num(d.G, Styles.Int));
                foreach (var v in new[] { d.ThreeShare, d.ThreePct, d.TwoPct, d.FtRate, d.TovRate, d.OrebAllowed, d.AstRate, d.PosPts["G"], d.PosPts["F"], d.PosPts["C"] })
                    r.Add(Cell.Num(v, v >= 1.04 ? Styles.Good : v <= 0.96 ? Styles.Bad : Styles.N2));
                r.Add(Cell.Num(d.RawPtsPer100, Styles.N1));
            }
            Note(sh, string.Format(U.IC, "Applied to player inputs at strength {0:0.00} (setting MatchupStrength, chosen by walk-forward backtest). Green = the defense gives up more of that than average (good for the offense), red = less.", Program.S.MatchupStrength));
        }

        static void DdThreesSheet()
        {
            var sh = New("DD & 3PT Props", "Double-double and three-pointer props",
                "Probabilities are conditional on the player playing (books void DNPs). Season columns are 2026-27 to date; matchup columns are tonight's opponent's shrunk defensive profile.",
                22, 6, 6, 8, 8, 8, 8, 9, 10, 10, 12, 9, 60);
            Section(sh, "Double-doubles (ranked by probability)", 13);
            Hdr(sh, "Player", "Team", "Opp", "Proj min", "Proj PTS", "Proj REB", "Proj AST", "P(DD)", "Fair odds", "P(TD)", "Season DD games", "Confidence", "Matchup / context");
            foreach (var ps in Analysis.Players.Where(p => p.MeanMin >= 15 && p.P.Status != "OUT").OrderByDescending(p => p.DD).Take(25))
            {
                var r = sh.Row(); int rn = sh.Rows.Count; var f = FormOf(ps);
                r.Add(Cell.Str(ps.P.Name + (ps.P.Status == "Q" ? " (Q)" : ""), Styles.Bold)); r.Add(Cell.Str(ps.P.Team)); r.Add(Cell.Str(ps.Opp)); r.Add(Cell.Num(ps.MeanMin, Styles.N1));
                r.Add(Cell.Num(ps.Pts.Mean, Styles.N1)); r.Add(Cell.Num(ps.Reb.Mean, Styles.N1)); r.Add(Cell.Num(ps.Ast.Mean, Styles.N1));
                r.Add(Cell.Num(ps.DD, Styles.PctB)); r.Add(FairOddsFx(R(7, rn), Math.Max(0.001, ps.DD))); r.Add(Cell.Num(ps.TD, Styles.Pct));
                r.Add(Cell.Str(f == null || f.DdGames < 0 ? "-" : f.DdGames + " of " + f.Gp)); r.Add(Cell.Num(ps.Conf, Styles.Int));
                r.Add(Cell.Str(ps.P.MatchupNote, Styles.Wrap));
            }
            Section(sh, "Three-pointers made (ranked by projected makes)", 13);
            Hdr(sh, "Player", "Team", "Opp", "Season 3PM/g", "Season 3PA/g", "Season 3P%", "Proj 3PM", "P(1+)", "P(2+)", "P(3+)", "Fair odds 3+", "P(4+)", "Matchup / context");
            foreach (var ps in Analysis.Players.Where(p => p.MeanMin >= 15 && p.P.Status != "OUT").OrderByDescending(p => p.Tpm.Mean).Take(30))
            {
                var r = sh.Row(); int rn = sh.Rows.Count; var f = FormOf(ps);
                r.Add(Cell.Str(ps.P.Name + (ps.P.Status == "Q" ? " (Q)" : ""), Styles.Bold)); r.Add(Cell.Str(ps.P.Team)); r.Add(Cell.Str(ps.Opp));
                r.Add(f == null ? Cell.Str("-") : Cell.Num(f.Tpm, Styles.N1)); r.Add(f == null ? Cell.Str("-") : Cell.Num(f.Tpa, Styles.N1));
                r.Add(f == null || double.IsNaN(f.TpPct) ? Cell.Str("-") : Cell.Num(f.TpPct, Styles.Pct));
                r.Add(Cell.Num(ps.Tpm.Mean, Styles.N1)); r.Add(Cell.Num(ps.Tpm.PAtLeast(1), Styles.Pct)); r.Add(Cell.Num(ps.Tpm.PAtLeast(2), Styles.Pct));
                r.Add(Cell.Num(ps.Tpm.PAtLeast(3), Styles.PctB)); r.Add(FairOddsFx(R(9, rn), Math.Max(0.001, ps.Tpm.PAtLeast(3)))); r.Add(Cell.Num(ps.Tpm.PAtLeast(4), Styles.Pct));
                r.Add(Cell.Str((f != null && f.FromPrior ? "season stats are 2025-26; " : "") + ps.P.MatchupNote, Styles.Wrap));
            }
        }
    }
}
