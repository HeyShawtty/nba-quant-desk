using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NbaQuant
{
    static partial class Report
    {
        const string OfficialInjuryPage = "https://official.nba.com/nba-injury-report-2026-27-season/";

        public static double Phi(double x)
        {
            double t = 1 / (1 + 0.3275911 * Math.Abs(x) / Math.Sqrt(2));
            double y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x / 2);
            return x >= 0 ? 0.5 * (1 + y) : 0.5 * (1 - y);
        }

        static string WhyFit(Combo c)
        {
            var kinds = c.Legs.Select(l => l.Kind).ToList();
            var parts = new List<string>();
            var teamLeg = c.Legs.FirstOrDefault(l => l.Kind == "ML" || l.Kind == "SPR");
            if (teamLeg != null)
            {
                string side = teamLeg.Team;
                if (c.Legs.Any(l => l.Ps != null && l.Ps.P.Team == side)) parts.Add(side + " result and its own players' production rise together");
                if (c.Legs.Any(l => l.Ps != null && l.Ps.P.Team != side)) parts.Add("opponent production legs offset part of the script risk");
            }
            if (kinds.Contains("TOT")) parts.Add(c.Legs.First(l => l.Kind == "TOT").Team == "OVER" ? "a high total lifts the scoring legs" : "a low total pairs with defensive/role legs");
            var pls = c.Legs.Where(l => l.Ps != null).GroupBy(l => l.Ps).Where(g => g.Count() > 1).ToList();
            if (pls.Any()) parts.Add("same-player legs share minutes and usage (" + pls.First().Key.P.Name + ")");
            if (parts.Count == 0) parts.Add("legs come from the same game environment (pace, minutes)");
            return string.Join("; ", parts) + string.Format(U.IC, ". Simulated joint rate is {0:0.00}x the independent product.", c.Lift);
        }

        // ======================= SUMMARY =======================
        static void Summary()
        {
            var games = Program.Games;
            var sh = New("Summary", "NBA Quant Desk | " + Program.SlateDate.ToString("dddd d MMM yyyy", U.IC) + " slate (" + games.Count + " games)",
                "Built " + Program.RunTime.ToString("ddd d MMM yyyy h:mm tt", U.IC) + " local time. " + Program.NSIM.ToString("N0", U.IC) + " executed possession-level simulations per game. Research output only: probabilities are estimates, nothing here is a lock.",
                34, 14, 13, 13, 13, 13, 70);
            Section(sh, "1. Verification: what was confirmed vs. projected", 7);
            Hdr(sh, "Item", "Status", "", "", "", "", "Detail / source");
            Action<string, string, string> v = (a, b, c) => { var r = sh.Row(); r.Add(Cell.Str(a, Styles.Bold)); r.Add(Cell.Str(b)); r.Add(null); r.Add(null); r.Add(null); r.Add(null); r.Add(Cell.Str(c, Styles.Wrap)); };
            v("Slate", "CONFIRMED", string.Join("; ", games.Select(g => GameLabel(g) + " " + g.Def.Tip)) + ". Source: ESPN scoreboard API.");
            v("Injuries", "ESPN feed", "Statuses from ESPN's game injury lists (they track the NBA official report but can lag it). Check " + OfficialInjuryPage + " before betting; put corrections in the overrides file and re-run.");
            foreach (var x in Program.Verification) v(x[0], x[1], x[2]);
            v("Starting lineups", "PROJECTED", "Not released until ~30 min before tip. Starters inferred from 2026-27 starts and projected minutes.");
            var b2b = games.SelectMany(g => new[] { g.Def.HomeB2B ? g.Def.Home : null, g.Def.AwayB2B ? g.Def.Away : null }).Where(x => x != null).ToList();
            v("Rest / schedule", "CONFIRMED", b2b.Count > 0 ? "Second night of a back-to-back: " + string.Join(", ", b2b) + "." : "No team is on a back-to-back.");
            int withDk = games.Count(g => g.HasMarket && g.MarketSource.StartsWith("DraftKings"));
            v("Game odds", withDk == games.Count ? "CONFIRMED snapshot" : "PARTIAL", withDk + " of " + games.Count + " games have DraftKings lines (Action Network feed, snapshot at run time). " + string.Join(" ", games.Where(g => !g.MarketSource.StartsWith("DraftKings")).Select(g => GameLabel(g) + ": " + (g.HasMarket ? g.MarketSource : "no line") + ".")));
            int props = games.Sum(g => g.Props.Count);
            v("Player props", props > 0 ? "CONFIRMED snapshot" : "NOT AVAILABLE", props > 0 ? props + " book prop lines captured from the Action Network props feed; see the Props vs Book tab." : "No prop lines were posted on the Action Network feed at run time (books post on game day). Props are given as fair odds only; re-run closer to tip to price them.");
            v("SGP prices & boost terms", "NOT VERIFIED", "DraftKings SGP pricing and boost terms cannot be read by this app. Enter the displayed price in the SGP & Boost tab (yellow cells).");
            var tr = Ledger.Summary();
            v("Model status", "CALIBRATED / UNPROVEN", "Parameters last recalibrated: " + (Program.S.LastRecalibration == "" ? "never" : Program.S.LastRecalibration) + ". " + tr + " Treat edges below ~5% as noise.");
            foreach (var w in Program.Warnings) v("Warning", "CHECK", w);

            var ml = MlRows();
            Section(sh, "A. Top 3 most likely moneyline winners (likelihood, NOT value)", 7);
            Hdr(sh, "Team (vs opponent)", "Win prob", "DK ML", "Fair ML", "EV @ DK", "Confidence", "Key matchup advantage | Most important risk");
            foreach (var r in ml.Take(3))
            {
                var row = sh.Row(); int rn = sh.Rows.Count;
                row.Add(Cell.Str(r.Team + (r.Home ? " vs " : " @ ") + r.Opp, Styles.Bold));
                row.Add(Cell.Num(r.P, Styles.PctB));
                if (HasOdds(r.DkMl))
                {
                    row.Add(Cell.Num(r.DkMl, Styles.OddsBlue)); row.Add(FairOddsFx(R(1, rn), r.P));
                    row.Add(Cell.Fx(R(1, rn) + "*IF(" + R(2, rn) + ">0," + R(2, rn) + "/100,100/-" + R(2, rn) + ")-(1-" + R(1, rn) + ")", U.EvPerDollar(r.P, r.DkMl), Styles.Pct));
                }
                else { row.Add(Cell.Str("n/a")); row.Add(FairOddsFx(R(1, rn), r.P)); row.Add(Cell.Str("n/a")); }
                row.Add(Cell.Num(r.Conf, Styles.Int)); row.Add(Cell.Str(Notes.MlNote(r), Styles.Wrap));
            }
            Note(sh, "The most likely winner is not the best bet: short favorites need very high true win rates to be profitable.");
            var value = ml.Where(r => HasOdds(r.BestMl)).OrderByDescending(r => U.EvPerDollar(r.P, r.BestMl)).Take(3).ToList();
            if (value.Count > 0)
            {
                Section(sh, "A2. Three best moneyline prices by model EV (value, NOT likelihood)", 7);
                Hdr(sh, "Team (vs opponent)", "Win prob", "Best ML", "Book", "EV @ best", "EV if margin SD=12", "Read");
                foreach (var r in value)
                {
                    double mu = r.Home ? r.G.MeanMargin : -r.G.MeanMargin, ev12 = U.EvPerDollar(Phi(mu / 12), r.BestMl), ev = U.EvPerDollar(r.P, r.BestMl);
                    var row = sh.Row();
                    row.Add(Cell.Str(r.Team + (r.Home ? " vs " : " @ ") + r.Opp, Styles.Bold)); row.Add(Cell.Num(r.P, Styles.Pct)); row.Add(Cell.Num(r.BestMl, Styles.OddsBlue)); row.Add(Cell.Str(r.BestBook));
                    row.Add(Cell.Num(ev, Styles.PctB)); row.Add(Cell.Num(ev12, Styles.Pct));
                    row.Add(Cell.Str(ev > 0.03 && ev12 > 0 ? "Survives the tighter-variance check; still unvalidated" : ev > 0 ? "Positive only under the model's variance assumption: not robust" : "Negative EV", Styles.Wrap));
                }
            }

            Section(sh, "B. Top 5 most likely player props (standard thresholds; capped at 95% so the list isn't trivial)", 7);
            Hdr(sh, "Player", "Market", "Threshold", "Hit prob", "Fair odds", "Confidence", "Sportsbook odds / EV");
            var edges = PropEdges();
            foreach (var t in AllThresholds().Where(x => x.P <= 0.95 && x.Ps.MeanMin >= 24 && x.Ps.P.Status != "Q").OrderByDescending(x => x.P).Take(5))
            {
                var row = sh.Row(); int rn = sh.Rows.Count;
                row.Add(Cell.Str(PName(t.Ps), Styles.Bold)); row.Add(Cell.Str(t.Market)); row.Add(Cell.Str(t.Line + "+"));
                row.Add(Cell.Num(t.P, Styles.PctB)); row.Add(FairOddsFx(R(3, rn), t.P)); row.Add(Cell.Num(t.Ps.Conf, Styles.Int));
                row.Add(Cell.Str(edges.Count > 0 ? "Posted lines are compared in the Props vs Book tab." : "No posted price captured. Fair odds shown.", Styles.Wrap));
            }
            if (edges.Count > 0)
            {
                Section(sh, "B2. Largest model edges vs posted prop lines (unvalidated)", 7);
                Hdr(sh, "Player", "Market", "Line / book", "P(over)", "Side", "EV/$1", "Note");
                foreach (var e in edges.Select(x => new { e = x, side = (double.IsNaN(x.EvUnder) || x.EvOver >= x.EvUnder) ? "Over" : "Under" }).Select(x => new { x.e, x.side, ev = x.side == "Over" ? x.e.EvOver : x.e.EvUnder }).Where(x => !double.IsNaN(x.ev)).OrderByDescending(x => x.ev).Take(5))
                {
                    var row = sh.Row();
                    row.Add(Cell.Str(PName(e.e.Ps), Styles.Bold)); row.Add(Cell.Str(e.e.L.Stat)); row.Add(Cell.Str(e.e.L.Line.ToString(U.IC) + " " + e.e.L.Book));
                    row.Add(Cell.Num(e.e.POver, Styles.Pct)); row.Add(Cell.Str(e.side + " " + U.Am(e.side == "Over" ? e.e.L.OverOdds : e.e.L.UnderOdds))); row.Add(Cell.Num(e.ev, Styles.PctB));
                    row.Add(Cell.Str("Prop calibration is still being measured; see Track Record.", Styles.Wrap));
                }
            }

            Section(sh, "C. Top 3 teams to score 100+ through three quarters", 7);
            Hdr(sh, "Team", "Exp. Q1-Q3 pts", "P(100+)", "Fair odds", "P(95+)", "P(105+)", "Note");
            foreach (var t in Analysis.TeamQs.OrderByDescending(t => t.P100).Take(3))
            {
                var row = sh.Row(); int rn = sh.Rows.Count;
                row.Add(Cell.Str(t.Team + (t.Home ? " vs " : " @ ") + t.Opp, Styles.Bold)); row.Add(Cell.Num(t.Mean, Styles.N1)); row.Add(Cell.Num(t.P100, Styles.PctB));
                row.Add(FairOddsFx(R(2, rn), t.P100)); row.Add(Cell.Num(t.P95, Styles.Pct)); row.Add(Cell.Num(t.P105, Styles.Pct));
                row.Add(Cell.Str(Notes.Q3Note(t), Styles.Wrap));
            }

            Section(sh, "D. Top 5 players to score 25+ points", 7);
            Hdr(sh, "Player", "P(25+)", "Fair odds", "Mean pts", "Median", "Confidence", "Context");
            foreach (var p in Analysis.Players.OrderByDescending(p => p.Pts.PAtLeast(25)).Take(5))
            {
                var row = sh.Row(); int rn = sh.Rows.Count; double pr = p.Pts.PAtLeast(25);
                row.Add(Cell.Str(PName(p) + " vs " + p.Opp, Styles.Bold)); row.Add(Cell.Num(pr, Styles.PctB)); row.Add(FairOddsFx(R(1, rn), pr));
                row.Add(Cell.Num(p.Pts.Mean, Styles.N1)); row.Add(Cell.Num(p.Pts.P50, Styles.Int)); row.Add(Cell.Num(p.Conf, Styles.Int));
                row.Add(Cell.Str(string.Format(U.IC, "{0:0.0} proj. min; 2026-27 {1:0.0} ppg in {2} games; prior season {3}", p.MeanMin, p.P.Gp > 0 ? p.P.CsPts / p.P.Gp : 0, p.P.Gp, p.P.HasPs ? (p.P.Ps.PTS / Math.Max(1, p.P.Ps.G)).ToString("0.0", U.IC) + " ppg" + (p.P.PsStale ? " (2024-25)" : "") : "n/a"), Styles.Wrap));
            }

            var forms = Forms();
            Section(sh, "D2. Who stands out tonight (simulation vs season form, against tonight's defense)", 7);
            Hdr(sh, "Player", "Biggest gap", "Season avg", "Projection", "P(DD)", "P(3+ threes)", "Why");
            foreach (var f in forms.Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(6))
            {
                var r = sh.Row(); var ps = f.Ps;
                double sv = f.Lead == "points" ? f.Ppg : f.Lead == "rebounds" ? f.Rpg : f.Lead == "assists" ? f.Apg : f.Tpm;
                double pv = f.Lead == "points" ? ps.Pts.Mean : f.Lead == "rebounds" ? ps.Reb.Mean : f.Lead == "assists" ? ps.Ast.Mean : ps.Tpm.Mean;
                r.Add(Cell.Str(PName(ps) + " vs " + ps.Opp, Styles.Bold)); r.Add(Cell.Str(f.Lead)); r.Add(Cell.Num(sv, Styles.N1)); r.Add(Cell.Num(pv, Styles.N1));
                r.Add(Cell.Num(ps.DD, Styles.Pct)); r.Add(Cell.Num(ps.Tpm.PAtLeast(3), Styles.Pct)); r.Add(Cell.Str(f.Why, Styles.Wrap));
            }
            Note(sh, "Full rising/falling lists and each team's defensive profile are on the Standouts tab.");

            Section(sh, "D3. Double-double and three-point props", 7);
            Hdr(sh, "Player", "Market", "Probability", "Fair odds", "Season", "Projection", "Matchup");
            foreach (var ps in Analysis.Players.Where(p => p.MeanMin >= 20 && p.P.Status != "Q" && p.DD <= 0.95).OrderByDescending(p => p.DD).Take(5))
            {
                var r = sh.Row(); int rn = sh.Rows.Count; var f = FormOf(ps);
                r.Add(Cell.Str(PName(ps) + " vs " + ps.Opp, Styles.Bold)); r.Add(Cell.Str("Double-double")); r.Add(Cell.Num(ps.DD, Styles.PctB)); r.Add(FairOddsFx(R(2, rn), ps.DD));
                r.Add(Cell.Str(f == null ? "-" : f.DdGames >= 0 ? f.DdGames + " DD in " + f.Gp + " games" : string.Format(U.IC, "{0:0.0} pts / {1:0.0} reb / {2:0.0} ast (2025-26)", f.Ppg, f.Rpg, f.Apg)));
                r.Add(Cell.Str(string.Format(U.IC, "{0:0.0} pts / {1:0.0} reb / {2:0.0} ast", ps.Pts.Mean, ps.Reb.Mean, ps.Ast.Mean))); r.Add(Cell.Str(ps.P.MatchupNote, Styles.Wrap));
            }
            foreach (var ps in Analysis.Players.Where(p => p.MeanMin >= 20 && p.P.Status != "Q").OrderByDescending(p => p.Tpm.PAtLeast(3)).Take(5))
            {
                var r = sh.Row(); int rn = sh.Rows.Count; var f = FormOf(ps); double pr = ps.Tpm.PAtLeast(3);
                r.Add(Cell.Str(PName(ps) + " vs " + ps.Opp, Styles.Bold)); r.Add(Cell.Str("3+ threes")); r.Add(Cell.Num(pr, Styles.PctB)); r.Add(FairOddsFx(R(2, rn), pr));
                r.Add(Cell.Str(f == null ? "-" : string.Format(U.IC, "{0:0.0} made / {1:0.0} att per game{2}", f.Tpm, f.Tpa, f.FromPrior ? " (2025-26)" : "")));
                r.Add(Cell.Str(string.Format(U.IC, "{0:0.0} made; P(2+) {1}, P(4+) {2}", ps.Tpm.Mean, U.Pct(ps.Tpm.PAtLeast(2)), U.Pct(ps.Tpm.PAtLeast(4))))); r.Add(Cell.Str(ps.P.MatchupNote, Styles.Wrap));
            }
            Note(sh, "No posted prices were captured for these markets; fair odds are the break-even prices. Full tables are on the DD & 3PT Props tab.");

            var best = Notes.BestSgp();
            Section(sh, "E. Best three-leg SGP for a $5, 50% profit-boost ticket (min +300 unboosted)", 7);
            if (best != null)
            {
                Hdr(sh, "Selections", "Joint prob", "Fair odds", "Boosted break-even", "EV if DK shows +300 (boosted)", "Lift vs independent", "Why the legs fit / what to check");
                var row = sh.Row(); int rn = sh.Rows.Count;
                row.Add(Cell.Str(Notes.GameOf(best) + ": " + string.Join(" + ", best.Legs.Select(l => l.Desc)), Styles.Wrap));
                row.Add(Cell.Num(best.Joint, Styles.PctB)); row.Add(FairOddsFx(R(1, rn), best.Joint));
                row.Add(Cell.Fx("1/(1+3*1.5)", 1 / 5.5, Styles.Pct));
                row.Add(Cell.Fx(R(1, rn) + "*(300*1.5/100)-(1-" + R(1, rn) + ")", best.Joint * 4.5 - (1 - best.Joint), Styles.Pct));
                row.Add(Cell.Num(best.Lift, Styles.N2));
                row.Add(Cell.Str(WhyFit(best) + " Unboosted DK odds: NOT captured - must display +300 or longer to qualify. Boosted odds = unboosted profit x 1.5 (+300 -> +450).", Styles.Wrap));
            }
            else Note(sh, "No three-leg combination met the boost criteria.");
            Note(sh, "EV is conditional on DraftKings offering +300 or longer and the full 50% boost applying. If the app shows less than +300, use the next candidate in the SGP & Boost tab.");

            Section(sh, "F. Contrarian / value research (none validated)", 7);
            foreach (var s in Notes.Contrarian()) { var r = sh.Row(); r.Add(Cell.Str(s, Styles.Wrap)); sh.Merges.Add("A" + sh.Rows.Count + ":G" + sh.Rows.Count); }

            Section(sh, "G. Final recommendation", 7);
            foreach (var s in Notes.Final()) { var r = sh.Row(); r.Add(Cell.Str(s, Styles.Wrap)); sh.Merges.Add("A" + sh.Rows.Count + ":G" + sh.Rows.Count); }
        }

        // ======================= MONEYLINE =======================
        static void Moneyline()
        {
            var sh = New("Moneyline", "Moneyline ranking | all teams", "Win prob = simulation with team scoring set to " + (Program.WMKT * 100).ToString("0", U.IC) + "% DraftKings-implied + " + ((1 - Program.WMKT) * 100).ToString("0", U.IC) + "% independent player model. EV per $1 = p x net payout - (1 - p). Blue = sportsbook inputs; black = formulas.",
                6, 7, 7, 6, 11, 11, 11, 10, 10, 11, 11, 10, 11, 11, 12, 12, 11, 60);
            Hdr(sh, "Rank", "Team", "Opp", "H/A", "Win prob (blend)", "Model-only", "DK no-vig", "Fair ML", "DK ML", "DK implied", "EV/$1 @ DK", "Best ML", "Best book", "EV/$1 @ best", "EV @ DK if margin SD=12", "EV @ DK if margin SD=13", "Confidence", "Confidence notes");
            sh.FreezeRow = sh.Rows.Count;
            int rank = 0;
            foreach (var r in MlRows())
            {
                rank++;
                var row = sh.Row(); int rn = sh.Rows.Count;
                var g = r.G; double mu = r.Home ? g.MeanMargin : -g.MeanMargin;
                row.Add(Cell.Num(rank, Styles.Int)); row.Add(Cell.Str(r.Team, Styles.Bold)); row.Add(Cell.Str(r.Opp)); row.Add(Cell.Str(r.Home ? "Home" : "Away"));
                row.Add(Cell.Num(r.P, Styles.PctB)); row.Add(double.IsNaN(r.PModel) ? Cell.Str("-") : Cell.Num(r.PModel, Styles.Pct)); row.Add(double.IsNaN(r.PMkt) ? Cell.Str("-") : Cell.Num(r.PMkt, Styles.Pct));
                row.Add(FairOddsFx(R(4, rn), r.P));
                if (HasOdds(r.DkMl))
                {
                    row.Add(Cell.Num(r.DkMl, Styles.OddsBlue));
                    row.Add(Cell.Fx("IF(" + R(8, rn) + ">0,100/(" + R(8, rn) + "+100),-" + R(8, rn) + "/(-" + R(8, rn) + "+100))", U.ImpliedProb(r.DkMl), Styles.Pct));
                    row.Add(Cell.Fx(R(4, rn) + "*IF(" + R(8, rn) + ">0," + R(8, rn) + "/100,100/-" + R(8, rn) + ")-(1-" + R(4, rn) + ")", U.EvPerDollar(r.P, r.DkMl), Styles.Pct));
                }
                else { row.Add(Cell.Str("n/a")); row.Add(Cell.Str("")); row.Add(Cell.Str("")); }
                if (HasOdds(r.BestMl))
                {
                    row.Add(Cell.Num(r.BestMl, Styles.OddsBlue)); row.Add(Cell.Str(r.BestBook));
                    row.Add(Cell.Fx(R(4, rn) + "*IF(" + R(11, rn) + ">0," + R(11, rn) + "/100,100/-" + R(11, rn) + ")-(1-" + R(4, rn) + ")", U.EvPerDollar(r.P, r.BestMl), Styles.Pct));
                }
                else { row.Add(Cell.Str("n/a")); row.Add(Cell.Str("")); row.Add(Cell.Str("")); }
                if (HasOdds(r.DkMl)) { row.Add(Cell.Num(U.EvPerDollar(Phi(mu / 12.0), r.DkMl), Styles.Pct)); row.Add(Cell.Num(U.EvPerDollar(Phi(mu / 13.0), r.DkMl), Styles.Pct)); }
                else { row.Add(Cell.Str("")); row.Add(Cell.Str("")); }
                row.Add(Cell.Num(r.Conf, Styles.Int)); row.Add(Cell.Str(r.ConfNote, Styles.Wrap));
            }
            Note(sh, "Sensitivity columns re-price each side with a normal margin distribution at the simulated mean but a tighter spread (SD 12 or 13 instead of the simulated ~14). An edge that flips sign there is an assumption, not an edge.");

            Section(sh, "Spreads and totals vs DraftKings", 10);
            Hdr(sh, "Game", "Market", "DK line", "DK odds", "Model prob", "Fair odds", "EV/$1", "DK implied", "Sim mean", "Note");
            foreach (var g in Program.Games.Where(x => x.HasMarket))
            {
                var d = g.Def.DK;
                var items = new[] {
                    Tuple.Create(g.Def.Home + " spread", (d.SprH > 0 ? "+" : "") + d.SprH.ToString(U.IC), d.SprHOdds, g.PCoverH, g.MeanMargin, "home margin"),
                    Tuple.Create(g.Def.Away + " spread", (-d.SprH > 0 ? "+" : "") + (-d.SprH).ToString(U.IC), d.SprAOdds, 1 - g.PCoverH, -g.MeanMargin, "away margin"),
                    Tuple.Create("Over", d.Total.ToString(U.IC), d.OverOdds, g.POver, g.MeanTotal, "total"),
                    Tuple.Create("Under", d.Total.ToString(U.IC), d.UnderOdds, 1 - g.POver, g.MeanTotal, "total") };
                foreach (var it in items)
                {
                    var row = sh.Row(); int rn = sh.Rows.Count;
                    row.Add(Cell.Str(GameLabel(g))); row.Add(Cell.Str(it.Item1)); row.Add(Cell.Str(it.Item2));
                    row.Add(Cell.Num(HasOdds(it.Item3) ? it.Item3 : -110, Styles.OddsBlue));
                    row.Add(Cell.Num(it.Item4, Styles.Pct)); row.Add(FairOddsFx(R(4, rn), it.Item4));
                    row.Add(Cell.Fx(R(4, rn) + "*IF(" + R(3, rn) + ">0," + R(3, rn) + "/100,100/-" + R(3, rn) + ")-(1-" + R(4, rn) + ")", U.EvPerDollar(it.Item4, HasOdds(it.Item3) ? it.Item3 : -110), Styles.Pct));
                    row.Add(Cell.Fx("IF(" + R(3, rn) + ">0,100/(" + R(3, rn) + "+100),-" + R(3, rn) + "/(-" + R(3, rn) + "+100))", U.ImpliedProb(HasOdds(it.Item3) ? it.Item3 : -110), Styles.Pct));
                    row.Add(Cell.Num(it.Item5, Styles.N1)); row.Add(Cell.Str("sim mean " + it.Item6 + (HasOdds(it.Item3) ? "" : "; odds assumed -110")));
                }
            }
            Note(sh, "Because most of each team's expected score comes from the DraftKings spread/total, spread and total probabilities sit near 50% by construction; deviations come from the independent model and are not validated edges.");
        }

        // ======================= GAME MODEL =======================
        static void GameModel()
        {
            var gs = Program.Games;
            var widths = new List<double> { 44 }; widths.AddRange(gs.Select(g => 16.0));
            var sh = New("Game Model", "Game environment | " + Program.NSIM.ToString("N0", U.IC) + " simulations per game", "Rows are metrics, columns are games. Model-only = independent player/BPM model; Blend = market + model (used for every reported probability).", widths.ToArray());
            Hdr(sh, new[] { "Metric" }.Concat(gs.Select(g => GameLabel(g))).ToArray());
            sh.FreezeRow = sh.Rows.Count;
            Action<string, Func<GameCtx, double>, int> m = (label, f, st) => { var r = sh.Row(); r.Add(Cell.Str(label)); foreach (var g in gs) { double v = f(g); r.Add(double.IsNaN(v) ? Cell.Str("-") : Cell.Num(v, st)); } };
            Action<string, Func<GameCtx, string>> ms = (label, f) => { var r = sh.Row(); r.Add(Cell.Str(label)); foreach (var g in gs) r.Add(Cell.Str(f(g), Styles.Wrap)); };
            ms("Tip-off", g => g.Def.Tip);
            ms("Rest days (away / home)", g => g.Def.AwayRest + " / " + g.Def.HomeRest + (g.Def.HomeB2B ? " (home B2B)" : "") + (g.Def.AwayB2B ? " (away B2B)" : ""));
            ms("Market source", g => g.HasMarket ? g.MarketSource : "none");
            Section(sh, "Market", gs.Count + 1);
            m("DK home spread", g => g.HasMarket ? g.Def.DK.SprH : double.NaN, Styles.N1); m("DK total", g => g.HasMarket ? g.Def.DK.Total : double.NaN, Styles.N1);
            m("DK home ML", g => g.Def.DK.HasMl ? g.Def.DK.MlH : double.NaN, Styles.OddsBlue); m("DK away ML", g => g.Def.DK.HasMl ? g.Def.DK.MlA : double.NaN, Styles.OddsBlue);
            m("DK no-vig home win %", g => Program.NoVigHome(g.Def.DK), Styles.Pct);
            m("Consensus home spread", g => g.Def.Consensus != null ? g.Def.Consensus.SprH : double.NaN, Styles.N1);
            m("Consensus total", g => g.Def.Consensus != null ? g.Def.Consensus.Total : double.NaN, Styles.N1);
            m("Market-implied home pts", g => g.MktPtsH, Styles.N1); m("Market-implied away pts", g => g.MktPtsA, Styles.N1);
            Section(sh, "Independent model inputs", gs.Count + 1);
            m("Home pace (poss/48, blended)", g => g.PaceH, Styles.N1); m("Away pace", g => g.PaceA, Styles.N1);
            m("Home offense (sum OBPM x min/48)", g => g.OffH, Styles.N1); m("Home defense (sum DBPM x min/48)", g => g.DefH, Styles.N1);
            m("Away offense", g => g.OffA, Styles.N1); m("Away defense", g => g.DefA, Styles.N1);
            m("Model possessions", g => g.ModelPoss, Styles.N1); m("Model home pts", g => g.ModelPtsH, Styles.N1); m("Model away pts", g => g.ModelPtsA, Styles.N1);
            m("Model home margin", g => g.ModelPtsH - g.ModelPtsA, Styles.N1); m("Model-only home win %", g => g.ModelWinH, Styles.Pct);
            m("Home offensive-rebound prob per miss", g => g.H.POreb, Styles.Pct); m("Away offensive-rebound prob per miss", g => g.A.POreb, Styles.Pct);
            m("P(home wins opening tip) (assumption)", g => g.P.PTipHome, Styles.Pct);
            Section(sh, "Blended simulation (reported)", gs.Count + 1);
            m("Target home pts", g => g.BlendPtsH, Styles.N1); m("Target away pts", g => g.BlendPtsA, Styles.N1); m("Possessions per team", g => g.BlendPoss, Styles.N1);
            m("Sim mean home margin", g => g.MeanMargin, Styles.N1); m("Sim mean total", g => g.MeanTotal, Styles.N1);
            m("Sim margin SD", g => g.SdMargin, Styles.N1); m("Sim total SD", g => g.SdTotal, Styles.N1);
            m("Home win %", g => g.WinH, Styles.PctB); m("Away win %", g => 1 - g.WinH, Styles.PctB);
            m("Home covers DK spread %", g => g.PCoverH, Styles.Pct); m("Over DK total %", g => g.POver, Styles.Pct);
            m("Overtime %", g => g.POT, Styles.Pct); m("Blowout (margin 20+) %", g => g.PBlowout, Styles.Pct); m("Competitive (margin 5 or less) %", g => g.PClose, Styles.Pct);
            m("Total P10", g => g.TotalPct[0], Styles.Int); m("Total P25", g => g.TotalPct[1], Styles.Int); m("Total median", g => g.TotalPct[2], Styles.Int);
            m("Total P75", g => g.TotalPct[3], Styles.Int); m("Total P90", g => g.TotalPct[4], Styles.Int);
            ms("Lineup scenarios (home)", g => string.Join(" | ", g.H.Scen.Select(s => s.Label + " " + U.Pct(s.Prob))));
            ms("Lineup scenarios (away)", g => string.Join(" | ", g.A.Scen.Select(s => s.Label + " " + U.Pct(s.Prob))));
        }

        // ======================= PROPS VS BOOK =======================
        static void PropsVsBook()
        {
            var edges = PropEdges();
            if (edges.Count == 0) return;
            var sh = New("Props vs Book", "Posted player props vs simulation", "Lines from the Action Network props feed at run time. P(over) counts simulations above the line (pushes removed on whole-number lines). Unvalidated: compare against the Track Record before trusting edges.",
                24, 7, 8, 12, 10, 10, 10, 10, 11, 11, 9);
            Hdr(sh, "Player", "Team", "Stat", "Book", "Line", "Over odds", "Under odds", "Model P(over)", "EV over", "EV under", "Model mean");
            sh.FreezeRow = sh.Rows.Count;
            foreach (var e in edges.OrderBy(x => x.Ps.P.Name).ThenBy(x => x.L.Stat))
            {
                var row = sh.Row(); int rn = sh.Rows.Count;
                var d = e.L.Stat == "PTS" ? e.Ps.Pts : e.L.Stat == "REB" ? e.Ps.Reb : e.L.Stat == "AST" ? e.Ps.Ast : e.L.Stat == "3PM" ? e.Ps.Tpm : e.Ps.Pra;
                row.Add(Cell.Str(e.Ps.P.Name, Styles.Bold)); row.Add(Cell.Str(e.Ps.P.Team)); row.Add(Cell.Str(e.L.Stat)); row.Add(Cell.Str(e.L.Book)); row.Add(Cell.Num(e.L.Line, Styles.N1));
                row.Add(HasOdds(e.L.OverOdds) ? Cell.Num(e.L.OverOdds, Styles.OddsBlue) : Cell.Str("")); row.Add(HasOdds(e.L.UnderOdds) ? Cell.Num(e.L.UnderOdds, Styles.OddsBlue) : Cell.Str(""));
                row.Add(Cell.Num(e.POver, Styles.Pct));
                row.Add(HasOdds(e.L.OverOdds) ? Cell.Fx(R(7, rn) + "*IF(" + R(5, rn) + ">0," + R(5, rn) + "/100,100/-" + R(5, rn) + ")-(1-" + R(7, rn) + ")", e.EvOver, Styles.Pct) : Cell.Str(""));
                row.Add(HasOdds(e.L.UnderOdds) ? Cell.Fx("(1-" + R(7, rn) + ")*IF(" + R(6, rn) + ">0," + R(6, rn) + "/100,100/-" + R(6, rn) + ")-" + R(7, rn), e.EvUnder, Styles.Pct) : Cell.Str(""));
                row.Add(Cell.Num(d.Mean, Styles.N1));
            }
        }

        // ======================= Q1-Q3 =======================
        static void ThreeQuarters()
        {
            var sh = New("Q1-Q3 Team Pts", "Team to score 100+ through three quarters", "Quarter-by-quarter possessions with 2026-27 quarter scoring shares; Q4 blowout benching does not affect Q1-Q3.", 6, 16, 10, 14, 11, 11, 11, 11, 11, 14);
            Hdr(sh, "Rank", "Team", "Opp", "Exp. Q1-Q3 pts", "P(90+)", "P(95+)", "P(100+)", "P(105+)", "Fair odds 100+", "Exp. full-game pts");
            int k = 0;
            foreach (var t in Analysis.TeamQs.OrderByDescending(t => t.P100))
            {
                k++; var row = sh.Row(); int rn = sh.Rows.Count;
                row.Add(Cell.Num(k, Styles.Int)); row.Add(Cell.Str(t.Team + (t.Home ? " (home)" : " (away)"), Styles.Bold)); row.Add(Cell.Str(t.Opp));
                row.Add(Cell.Num(t.Mean, Styles.N1)); row.Add(Cell.Num(t.P90, Styles.Pct)); row.Add(Cell.Num(t.P95, Styles.Pct)); row.Add(Cell.Num(t.P100, Styles.PctB)); row.Add(Cell.Num(t.P105, Styles.Pct));
                row.Add(FairOddsFx(R(6, rn), t.P100)); row.Add(Cell.Num(t.MeanFull, Styles.N1));
            }
        }

        // ======================= PLAYER DISTRIBUTIONS =======================
        static void PlayerDistributions()
        {
            var sh = New("Player Distributions", "Player outcome distributions", "Players projected for 10+ minutes. Player box scores add up to the simulated team score in every simulation (same possessions, same game script).",
                8, 24, 7, 9, 9, 7, 8, 7, 7, 7, 7, 7, 7, 7, 7, 10, 10);
            Hdr(sh, "Team", "Player", "Opp", "Status", "Proj. min", "Stat", "Mean", "P10", "P25", "Median", "P75", "P90", "P95", "P99", "Confidence", "2026-27 avg", "Prior-season avg");
            sh.FreezeRow = sh.Rows.Count;
            foreach (var g in Program.Games)
                foreach (var p in Analysis.Players.Where(x => x.G == g && x.MeanMin >= 10).OrderBy(x => x.Home ? 1 : 0).ThenByDescending(x => x.MeanMin))
                {
                    var pl = p.P; double gp = Math.Max(1, pl.Gp), pg = pl.HasPs ? Math.Max(1, pl.Ps.G) : 1;
                    var stats = new[] {
                        Tuple.Create("PTS", p.Pts, pl.CsPts / gp, pl.HasPs ? pl.Ps.PTS / pg : double.NaN),
                        Tuple.Create("REB", p.Reb, (pl.CsOreb + pl.CsDreb) / gp, pl.HasPs ? pl.Ps.TRB / pg : double.NaN),
                        Tuple.Create("AST", p.Ast, pl.CsAst / gp, pl.HasPs ? pl.Ps.AST / pg : double.NaN),
                        Tuple.Create("3PM", p.Tpm, pl.Cs3m / gp, pl.HasPs ? pl.Ps.FG3 / pg : double.NaN),
                        Tuple.Create("PRA", p.Pra, (pl.CsPts + pl.CsOreb + pl.CsDreb + pl.CsAst) / gp, pl.HasPs ? (pl.Ps.PTS + pl.Ps.TRB + pl.Ps.AST) / pg : double.NaN) };
                    foreach (var s in stats)
                    {
                        var row = sh.Row(); var d = s.Item2;
                        row.Add(Cell.Str(pl.Team)); row.Add(Cell.Str(pl.Name, Styles.Bold)); row.Add(Cell.Str(p.Opp)); row.Add(Cell.Str(pl.Status == "Q" ? "QUESTIONABLE" : "Available"));
                        row.Add(Cell.Num(p.MeanMin, Styles.N1)); row.Add(Cell.Str(s.Item1)); row.Add(Cell.Num(d.Mean, Styles.N1));
                        foreach (var q in new[] { d.P10, d.P25, d.P50, d.P75, d.P90, d.P95, d.P99 }) row.Add(Cell.Num(q, Styles.Int));
                        row.Add(Cell.Num(p.Conf, Styles.Int));
                        row.Add(pl.Gp > 0 ? Cell.Num(s.Item3, Styles.N1) : Cell.Str("-")); row.Add(double.IsNaN(s.Item4) ? Cell.Str("-") : Cell.Num(s.Item4, Styles.N1));
                    }
                }
        }

        // ======================= PLAYER THRESHOLDS =======================
        static void PlayerThresholds()
        {
            var cols = new List<string> { "Team", "Player", "Opp", "Proj. min" };
            cols.AddRange(Analysis.PtsT.Select(t => "PTS " + t + "+")); cols.AddRange(Analysis.RebT.Select(t => "REB " + t + "+"));
            cols.AddRange(Analysis.AstT.Select(t => "AST " + t + "+")); cols.AddRange(Analysis.TpmT.Select(t => "3PM " + t + "+"));
            cols.AddRange(Analysis.PraT.Select(t => "PRA " + t + "+")); cols.Add("Double-double"); cols.Add("Triple-double"); cols.Add("Confidence"); cols.Add("Confidence notes");
            var widths = new List<double> { 6, 22, 6, 8 }; widths.AddRange(Enumerable.Repeat(7.5, cols.Count - 6)); widths.Add(9); widths.Add(50);
            var sh = New("Player Thresholds", "Hit probabilities at standard thresholds", "Probability of reaching each threshold (inclusive). Confidence (1-100) reflects data and availability quality, not the hit probability.", widths.ToArray());
            Hdr(sh, cols.ToArray());
            sh.FreezeRow = sh.Rows.Count;
            foreach (var p in Analysis.Players.Where(x => x.MeanMin >= 15).OrderByDescending(x => x.Pra.Mean))
            {
                var row = sh.Row();
                row.Add(Cell.Str(p.P.Team)); row.Add(Cell.Str(p.P.Name + (p.P.Status == "Q" ? " (Q)" : ""), Styles.Bold)); row.Add(Cell.Str(p.Opp)); row.Add(Cell.Num(p.MeanMin, Styles.N1));
                foreach (int t in Analysis.PtsT) row.Add(Cell.Num(p.Pts.PAtLeast(t), Styles.Pct));
                foreach (int t in Analysis.RebT) row.Add(Cell.Num(p.Reb.PAtLeast(t), Styles.Pct));
                foreach (int t in Analysis.AstT) row.Add(Cell.Num(p.Ast.PAtLeast(t), Styles.Pct));
                foreach (int t in Analysis.TpmT) row.Add(Cell.Num(p.Tpm.PAtLeast(t), Styles.Pct));
                foreach (int t in Analysis.PraT) row.Add(Cell.Num(p.Pra.PAtLeast(t), Styles.Pct));
                row.Add(Cell.Num(p.DD, Styles.Pct)); row.Add(Cell.Num(p.TD, Styles.Pct)); row.Add(Cell.Num(p.Conf, Styles.Int)); row.Add(Cell.Str(p.ConfNote));
            }
            Section(sh, "Ranked: top 10 players by likelihood at each headline threshold", 8);
            var heads = new[] { Tuple.Create("Points 25+", (Func<PStat, double>)(x => x.Pts.PAtLeast(25))), Tuple.Create("Rebounds 10+", (Func<PStat, double>)(x => x.Reb.PAtLeast(10))),
                Tuple.Create("Assists 6+", (Func<PStat, double>)(x => x.Ast.PAtLeast(6))), Tuple.Create("Threes 3+", (Func<PStat, double>)(x => x.Tpm.PAtLeast(3))),
                Tuple.Create("PRA 35+", (Func<PStat, double>)(x => x.Pra.PAtLeast(35))), Tuple.Create("Double-double", (Func<PStat, double>)(x => x.DD)), Tuple.Create("Triple-double", (Func<PStat, double>)(x => x.TD)) };
            Hdr(sh, "Rank", "Player", "Team", "Category", "Probability", "Fair odds");
            foreach (var h in heads)
            {
                int k = 0;
                foreach (var p in Analysis.Players.Where(x => x.MeanMin >= 15).OrderByDescending(h.Item2).Take(10))
                {
                    k++; var row = sh.Row(); int rn = sh.Rows.Count; double v = h.Item2(p);
                    row.Add(Cell.Num(k, Styles.Int)); row.Add(Cell.Str(p.P.Name, Styles.Bold)); row.Add(Cell.Str(p.P.Team)); row.Add(Cell.Str(h.Item1));
                    row.Add(Cell.Num(v, Styles.Pct)); row.Add(FairOddsFx(R(4, rn), v));
                }
            }
        }

        // ======================= EVERY QUARTER =======================
        static void EveryQuarter()
        {
            var sh = New("Every Quarter 3+", "Player to score 3+ points in every quarter", "Joint probability across all four regulation quarters, from simulated quarter-level minutes (starter/bench rotation templates, Q4 blowout benching, close-game starter extension).",
                6, 24, 7, 7, 9, 9, 12, 11, 10, 50);
            Hdr(sh, "Rank", "Player", "Team", "Opp", "Proj. min", "Mean pts", "P(3+ all 4 Qs)", "Fair odds", "Confidence", "Note");
            int k = 0;
            foreach (var p in Analysis.Players.Where(x => x.MeanMin >= 15).OrderByDescending(x => x.Q3All).Take(25))
            {
                k++; var row = sh.Row(); int rn = sh.Rows.Count;
                row.Add(Cell.Num(k, Styles.Int)); row.Add(Cell.Str(p.P.Name, Styles.Bold)); row.Add(Cell.Str(p.P.Team)); row.Add(Cell.Str(p.Opp));
                row.Add(Cell.Num(p.MeanMin, Styles.N1)); row.Add(Cell.Num(p.Pts.Mean, Styles.N1)); row.Add(Cell.Num(p.Q3All, Styles.PctB)); row.Add(FairOddsFx(R(6, rn), p.Q3All));
                row.Add(Cell.Num(p.Conf, Styles.Int));
                row.Add(Cell.Str(p.G.PBlowout > 0.3 ? "High blowout risk in this game trims Q4 minutes" : ""));
            }
            Note(sh, "A 30-point scorer can still fail this market: one quiet quarter (foul trouble, a short rest stint, garbage time) loses it.");
        }

        // ======================= FIRST BASKET =======================
        static void FirstBasket()
        {
            var sh = New("First Basket", "First field goal scorer", "Simulated: opening tip, alternating possessions, shot selection by usage among the starting five, makes/misses, offensive rebounds and turnovers until the first made field goal (free throws excluded).",
                6, 24, 8, 16, 12, 11, 40);
            Section(sh, "Game-level", 7);
            Hdr(sh, "", "Game", "", "P(home wins tip)", "P(tip winner scores 1st FG)", "P(home scores 1st FG)", "Note");
            foreach (var g in Program.Games)
            {
                var r = g.Res; int same = 0, homeFirst = 0, n = r.N;
                for (int s = 0; s < n; s++)
                {
                    if (r.FirstFG[s] < 0) continue;
                    bool hf = r.FirstFG[s] < r.NH; if (hf) homeFirst++;
                    if (hf == (r.TipHome[s] == 1)) same++;
                }
                var row = sh.Row();
                row.Add(null); row.Add(Cell.Str(GameLabel(g), Styles.Bold)); row.Add(null); row.Add(Cell.Num(g.P.PTipHome, Styles.Pct)); row.Add(Cell.Num((double)same / n, Styles.Pct)); row.Add(Cell.Num((double)homeFirst / n, Styles.Pct));
                row.Add(Cell.Str("Tip assumption: 50% +3.5 pts per inch of height edge between tallest projected starters"));
            }
            Note(sh, string.Format(U.IC, "Reality check: in {1} completed 2026-27 games, the team that won the opening tip scored the first field goal {0} times ({2:0}%).", (int)Math.Round(Program.EmpTipTeamFirstFG * Program.EmpTipN), Program.EmpTipN, 100 * Program.EmpTipTeamFirstFG));
            Section(sh, "Players ranked across the slate", 7);
            Hdr(sh, "Rank", "Player", "Team", "Game", "P(first FG)", "Fair odds", "Basis");
            int k = 0;
            foreach (var p in Analysis.Players.OrderByDescending(x => x.FirstFG).Take(25))
            {
                k++; var row = sh.Row(); int rn = sh.Rows.Count;
                row.Add(Cell.Num(k, Styles.Int)); row.Add(Cell.Str(p.P.Name, Styles.Bold)); row.Add(Cell.Str(p.P.Team)); row.Add(Cell.Str(GameLabel(p.G)));
                row.Add(Cell.Num(p.FirstFG, k <= 5 ? Styles.PctB : Styles.Pct)); row.Add(FairOddsFx(R(4, rn), p.FirstFG));
                row.Add(Cell.Str((p.P.Status == "Q" ? "Questionable; " : "") + "projected starter-share of opening possessions x shot efficiency"));
            }
        }

        // ======================= SLATE LEADERS =======================
        static void SlateLeaders()
        {
            var sh = New("Slate Leaders", "Highest statistical performance across the slate", "All games simulated jointly (simulation i of each game combined). Ties split as dead heats. Check each sportsbook's settlement rules.",
                10, 26, 8, 14, 12, 34);
            foreach (var grp in Analysis.Leaders.GroupBy(l => l.Category))
            {
                Section(sh, grp.Key, 6);
                Hdr(sh, "Rank", "Player / team", "Team", "Probability", "Fair odds", "");
                int k = 0;
                foreach (var l in grp.OrderByDescending(x => x.P).Take(10))
                {
                    k++; var row = sh.Row(); int rn = sh.Rows.Count;
                    row.Add(Cell.Num(k, Styles.Int)); row.Add(Cell.Str(l.Ps != null ? l.Ps.P.Name : l.Team, Styles.Bold)); row.Add(Cell.Str(l.Team));
                    row.Add(Cell.Num(l.P, Styles.Pct)); row.Add(FairOddsFx(R(3, rn), l.P));
                }
            }
        }

        // ======================= SGP =======================
        static void Sgp()
        {
            var sh = New("SGP & Boost", "Same-game parlays from the joint simulation + DraftKings profit-boost calculator",
                "Joint probabilities count simulations where every leg hit (never a product of legs). Yellow cells are inputs: type the price DraftKings displays to get boosted EV.",
                12, 12, 70, 10, 10, 8, 10, 11, 18, 11, 11, 11, 11, 13, 12, 60);
            var r1 = sh.Row(); r1.Add(Cell.Str("Stake ($)", Styles.Bold)); r1.Add(Cell.Num(5, Styles.InputMoney));
            int stakeRow = sh.Rows.Count;
            var r2 = sh.Row(); r2.Add(Cell.Str("Profit boost", Styles.Bold)); r2.Add(Cell.Num(0.5, Styles.PctBlue)); r2.Add(Cell.Str("Boost multiplies PROFIT only: +300 at 50% -> +450.", Styles.Sub));
            int boostRow = sh.Rows.Count;
            var r3 = sh.Row(); r3.Add(Cell.Str("Minimum unboosted odds", Styles.Bold)); r3.Add(Cell.Num(300, Styles.InputOdds)); r3.Add(Cell.Str("Promotion terms not verified here; confirm in the app.", Styles.Sub));
            int minRow = sh.Rows.Count;
            string S = RA(1, stakeRow), B = RA(1, boostRow), MN = RA(1, minRow);
            Hdr(sh, "Game", "Type", "Legs", "Joint prob", "Product of legs", "Lift", "Fair odds", "Min DK odds for +EV w/ boost", "DK displayed odds (enter)", "Qualifies?", "Boosted odds", "Boosted break-even", "EV/$1 boosted", "EV on stake ($)", "EV/$1 if DK = min odds", "Why the legs fit");
            sh.FreezeRow = sh.Rows.Count;
            foreach (var g in Program.Games)
            {
                var list = new List<Tuple<string, Combo>>();
                foreach (var c in Analysis.BoostSgp[g]) list.Add(Tuple.Create("Boost candidate", c));
                if (Analysis.BestHitSgp[g] != null) list.Add(Tuple.Create("Highest hit prob", Analysis.BestHitSgp[g]));
                if (Analysis.LiftSgp[g] != null) list.Add(Tuple.Create("Most correlated", Analysis.LiftSgp[g]));
                if (Analysis.RiskySgp[g] != null) list.Add(Tuple.Create("Higher-risk 4-leg", Analysis.RiskySgp[g]));
                foreach (var it in list)
                {
                    var c = it.Item2; var row = sh.Row(); int rn = sh.Rows.Count;
                    string D = R(3, rn), H = R(8, rn), J = R(10, rn), L = R(12, rn);
                    row.Add(Cell.Str(GameLabel(g))); row.Add(Cell.Str(it.Item1, it.Item1 == "Boost candidate" ? Styles.Bold : Styles.Def));
                    row.Add(Cell.Str(string.Join(" + ", c.Legs.Select(l => l.Desc + " (" + U.Pct(l.P) + ")")), Styles.Wrap));
                    row.Add(Cell.Num(c.Joint, Styles.PctB)); row.Add(Cell.Num(c.Product, Styles.Pct)); row.Add(Cell.Num(c.Lift, Styles.N2));
                    row.Add(FairOddsFx(D, c.Joint));
                    row.Add(Cell.Fx("100*((1/" + D + ")-1)/(1+" + B + ")", 100 * ((1 / c.Joint) - 1) / 1.5, Styles.Odds));
                    row.Add(Cell.Empty(Styles.InputOdds));
                    row.Add(Cell.FxS("IF(" + H + "=\"\",\"enter DK odds\",IF(" + H + ">=" + MN + ",\"YES\",\"NO - below minimum\"))", "enter DK odds"));
                    row.Add(Cell.FxS("IF(" + H + "=\"\",\"\",IF(" + H + ">0," + H + "*(1+" + B + "),\"n/a\"))", "", Styles.Odds));
                    row.Add(Cell.FxS("IF(" + J + "=\"\",\"\",100/(" + J + "+100))", "", Styles.Pct));
                    row.Add(Cell.FxS("IF(" + J + "=\"\",\"\"," + D + "*" + J + "/100-(1-" + D + "))", "", Styles.Pct));
                    row.Add(Cell.FxS("IF(" + L + "=\"\",\"\"," + L + "*" + S + ")", "", Styles.Money));
                    row.Add(Cell.Fx(D + "*(" + MN + "*(1+" + B + ")/100)-(1-" + D + ")", c.Joint * 4.5 - (1 - c.Joint), Styles.Pct));
                    row.Add(Cell.Str(WhyFit(c), Styles.Wrap));
                }
            }
            Note(sh, "Rules used: no two legs on the same player+stat, PRA never combined with its own components, never moneyline + spread together, game legs at DraftKings' posted spread/total. Player legs exclude questionable players.");
            Note(sh, "Under a full 50% profit boost at +300, any ticket whose true probability exceeds 18.2% is +EV. DraftKings applies its own correlation adjustment, so a high 'Lift' usually means a shorter price, not a hidden edge.");
        }

        // ======================= MINUTES & INJURIES =======================
        static void Minutes()
        {
            var sh = New("Minutes & Injuries", "Availability, minutes and per-minute rates", "Status from ESPN injury lists plus any overrides file. Projected minutes are scenario-weighted after redistributing minutes of absent players.",
                6, 24, 5, 13, 7, 46, 6, 7, 6, 8, 8, 7, 9, 8, 8, 8, 8, 8, 8, 8, 18);
            Hdr(sh, "Team", "Player", "Pos", "Status", "P(play)", "Injury / report note", "GP 26-27", "DNP-CD", "Starts", "MPG 26-27", "MPG prior", "Same team?", "Proj. min (expected)", "Proj. starter", "Usage/min", "2P% proj", "3P% proj", "FT% proj", "OBPM blend", "DBPM blend", "Prior-season source");
            sh.FreezeRow = sh.Rows.Count;
            foreach (var g in Program.Games)
                foreach (var st in new[] { g.A, g.H })
                {
                    string abbr = st.Abbr;
                    foreach (var p in Program.TeamPlayers[abbr].OrderByDescending(x => x.Status == "OUT" ? -1 : x.ExpMin))
                    {
                        int idx = Array.IndexOf(st.Pl, p);
                        double em = idx >= 0 ? Program.ExpectedMin(st, idx) : 0;
                        if (p.Status != "OUT" && em < 1 && p.Gp == 0 && p.DnpCd == 0 && p.InjNote == "") continue;
                        var row = sh.Row();
                        row.Add(Cell.Str(abbr)); row.Add(Cell.Str(p.Name, Styles.Bold)); row.Add(Cell.Str(p.Pos));
                        row.Add(Cell.Str(p.Status == "OUT" ? "OUT" : p.Status == "Q" ? "QUESTIONABLE" : "Available", p.Status == "OUT" ? Styles.Bold : Styles.Def));
                        row.Add(Cell.Num(p.Status == "OUT" ? 0 : p.PPlay, Styles.Pct)); row.Add(Cell.Str(p.InjNote, Styles.Wrap));
                        row.Add(Cell.Num(p.Gp, Styles.Int)); row.Add(Cell.Num(p.DnpCd, Styles.Int)); row.Add(Cell.Num(p.Starts, Styles.Int));
                        row.Add(Cell.Num(p.CsMpg, Styles.N1)); row.Add(Cell.Num(p.PsMpg, Styles.N1)); row.Add(Cell.Str(p.HasPs ? (p.SameTeam ? "Yes" : "No") : "-"));
                        row.Add(Cell.Num(em, Styles.N1)); row.Add(Cell.Str(idx >= 0 && st.Scen[0].Starter[idx] ? "Starter" : ""));
                        row.Add(Cell.Num(p.Usage, Styles.N2)); row.Add(Cell.Num(p.P2, Styles.Pct)); row.Add(Cell.Num(p.P3, Styles.Pct)); row.Add(Cell.Num(p.Pft, Styles.Pct));
                        row.Add(Cell.Num(p.Obpm, Styles.N1)); row.Add(Cell.Num(p.Dbpm, Styles.N1));
                        row.Add(Cell.Str(!p.HasPs ? "none (rookie/new)" : p.PsStale ? "2024-25 (missed 2025-26)" : "2025-26"));
                    }
                }
            Note(sh, "Rates are per-minute, shrunk toward position averages: 2026-27 minutes weighted 1.5x, 2025-26 minutes capped at 1,200 (x0.8 if the player changed teams), plus 150 minutes of position prior.");
        }

        // ======================= TRACK RECORD =======================
        static void TrackRecord()
        {
            var graded = Ledger.LoadAll();
            var sh = New("Track Record", "Graded predictions to date (live slates only)", "Every prediction the app publishes is resolved from ESPN box scores and play-by-play. Brier score: lower is better (0.25 = coin flip at 50%).",
                30, 9, 10, 10, 10, 10, 12, 12);
            if (graded.Count == 0) { Note(sh, "No graded slates yet. Use 'Grade' after games finish."); return; }
            Hdr(sh, "Group", "N", "Mean predicted", "Hit rate", "Brier", "Log loss", "Market Brier", "Flat $1 profit");
            foreach (var s in Grader.Summarize(graded))
            {
                var r = sh.Row();
                r.Add(Cell.Str(s.Group)); r.Add(Cell.Num(s.N, Styles.Int)); r.Add(Cell.Num(s.MeanP, Styles.Pct)); r.Add(Cell.Num(s.HitRate, Styles.Pct));
                r.Add(Cell.Num(s.Brier, Styles.N2)); r.Add(Cell.Num(s.LogLoss, Styles.N2)); r.Add(double.IsNaN(s.BrierMkt) ? Cell.Str("") : Cell.Num(s.BrierMkt, Styles.N2)); r.Add(double.IsNaN(s.Profit) ? Cell.Str("") : Cell.Num(s.Profit, Styles.N2));
            }
            Section(sh, "Reliability: predicted vs observed (all binary predictions)", 8);
            Hdr(sh, "Predicted bin", "N", "Mean predicted", "Observed", "", "", "", "");
            foreach (var b in Grader.Reliability(graded))
            {
                var r = sh.Row();
                r.Add(Cell.Str(U.Pct(b[0]) + " - " + U.Pct(b[1]))); r.Add(Cell.Num(b[2], Styles.Int)); r.Add(double.IsNaN(b[3]) ? Cell.Str("") : Cell.Num(b[3], Styles.Pct)); r.Add(double.IsNaN(b[4]) ? Cell.Str("") : Cell.Num(b[4], Styles.Pct));
            }
            Section(sh, "Headline picks", 8);
            Hdr(sh, "Date", "Tag", "Selection", "Prob", "Result", "Actual", "", "");
            foreach (var p in graded.Where(x => !string.IsNullOrEmpty(x.Tag) && (x.Tag.StartsWith("top") || x.Tag == "best_sgp")).OrderBy(x => x.Date).ThenBy(x => x.Tag))
            {
                var r = sh.Row();
                r.Add(Cell.Str(p.Date)); r.Add(Cell.Str(p.Tag)); r.Add(Cell.Str(p.Selection)); r.Add(Cell.Num(p.P, Styles.Pct));
                r.Add(Cell.Str(p.Void ? "VOID" : p.Y >= 1 ? "HIT" : "MISS", p.Y >= 1 ? Styles.Bold : Styles.Def)); r.Add(double.IsNaN(p.Actual) ? Cell.Str("") : Cell.Num(p.Actual, Styles.N1));
            }
        }

        // ======================= METHOD =======================
        static void Method()
        {
            var sh = New("Method & Calibration", "How the numbers were produced", null, 40, 16, 16, 16, 16, 60);
            Section(sh, "Parameters in use", 6);
            Hdr(sh, "Quantity", "Value", "", "", "", "Source / meaning");
            var H = Program.HistStats; var S = Program.S;
            Action<string, double, int, string> m = (a, v, st, s) => { var r = sh.Row(); r.Add(Cell.Str(a)); r.Add(double.IsNaN(v) ? Cell.Str("-") : Cell.Num(v, st)); r.Add(null); r.Add(null); r.Add(null); r.Add(Cell.Str(s, Styles.Wrap)); };
            if (H.ContainsKey("margin_resid_sd"))
            {
                m("2025-26 games with closing lines", H["n_games"], Styles.Int, "Action Network consensus closing lines + final scores");
                m("Margin residual SD vs closing spread", H["margin_resid_sd"], Styles.N2, "Target for simulated margin dispersion");
                m("Total residual SD vs closing total", H["total_resid_sd"], Styles.N2, "Target for simulated total dispersion");
                m("Home-court advantage (pts)", Program.HCA, Styles.N2, "Average 2025-26 home margin");
                m("Closing no-vig moneyline Brier 2025-26", H["ml_brier"], Styles.N2, "Market baseline the model must beat to claim an edge");
            }
            m("Market weight in blend", Program.WMKT, Styles.Pct, "Share of each team's expected score taken from the DraftKings spread/total");
            m("Score-effect strength (alpha)", S.Alpha, Styles.N2, "Leading team's make probability x (1 - alpha x lead/100)");
            m("Team shooting-shock SD", S.SigmaEff, Styles.N2, "Per-game team efficiency noise");
            m("Pace-shock SD (possessions)", S.SigmaPace, Styles.N2, "Per-game possession noise");
            m("Minutes noise SD (log scale)", S.SigmaMin, Styles.N2, "Per-player minutes variation around projection");
            m("Player shooting-night SD", S.SigmaPlayer, Styles.N2, "Per-player hot/cold multiplier on make probability (fitted by walk-forward backtest)");
            m("Usage concentration exponent (gamma)", Program.Gamma, Styles.N1, Program.GammaTeamGames >= 30 ? string.Format(U.IC, "Fitted on {0} 2026-27 team-games (R2 {1:0.000} vs {2:0.000} proportional)", Program.GammaTeamGames, Program.GammaR2, Program.GammaR2Prop) : "Default (too few games to fit)");
            m("League pace used", Program.LgPace, Styles.N1, "Basketball-Reference 2025-26 average");
            m("League offensive rating used", Program.LgORtg, Styles.N1, "2025-26 ORtg scaled by 2026-27 scoring to date");
            m("Back-to-back penalty (net pts)", Program.B2BNet, Styles.N1, "Assumption from published rest studies");
            string recal = Program.P(@"ledger\recalibration.txt");
            if (File.Exists(recal))
            {
                Section(sh, "Last recalibration (walk-forward backtest)", 6);
                foreach (var line in File.ReadAllLines(recal)) { var r = sh.Row(); r.Add(Cell.Str(line, Styles.Wrap)); sh.Merges.Add("A" + sh.Rows.Count + ":F" + sh.Rows.Count); }
            }
            Section(sh, "Model pipeline", 6);
            foreach (var s in new[] {
                "1. Rosters: ESPN team rosters. Availability: ESPN injury lists + optional overrides file. Questionable players enter as lineup scenarios (each simulation draws who plays).",
                "2. Minutes: 2026-27 minutes blended with prior season, times rotation probability from coach's-decision DNPs; team minutes renormalized to 240 (excess trimmed mostly from bench; absent players' minutes redistributed with caps).",
                "3. Per-minute rates (2PA, 3PA, FT trips, TOV, OREB, DREB, AST) and shooting % with shrinkage toward position averages.",
                "4. Independent team strength: blended OBPM/DBPM x projected minutes; pace; home court; back-to-back.",
                "5. Blend: team expected points = market weight x DraftKings-implied + remainder x independent model.",
                "6. Simulation: quarter-by-quarter alternating possessions; opening tip; on-court shares from rotation templates; shooter by usage; 2s/3s/FT trips/turnovers; assists; rebounds; score effects; Q4 blowout benching; overtime; player hot/cold nights.",
                "7. Outputs are counts over simulations; SGPs are joint counts within one game's simulations; slate leaders pair simulation i across games.",
                "8. Learning loop: every published prediction is saved, graded after the games, and the walk-forward backtest refits variance parameters on all completed games (no look-ahead in player data).",
                "Not modeled: referees, defender assignments, foul trouble beyond minutes noise, intentional end-game fouling, coach-specific rotations, live line movement." })
            { var r = sh.Row(); r.Add(Cell.Str(s, Styles.Wrap)); sh.Merges.Add("A" + sh.Rows.Count + ":F" + sh.Rows.Count); }
            Section(sh, "Sources", 6);
            foreach (var s in new[] {
                "ESPN APIs: scoreboard, game summaries (box scores, play-by-play, injuries, DraftKings lines), team rosters - site.api.espn.com/apis/site/v2/sports/basketball/nba/...",
                "NBA official injury report (manual check): " + OfficialInjuryPage,
                "Basketball-Reference: season totals and advanced tables, team ratings - basketball-reference.com/leagues/",
                "Action Network odds and props API: api.actionnetwork.com/web/v2/scoreboard/nba and /web/v2/games/{id}/props" })
            { var r = sh.Row(); r.Add(Cell.Str(s, Styles.Wrap)); sh.Merges.Add("A" + sh.Rows.Count + ":F" + sh.Rows.Count); }
        }

        // ======================= TRACKING =======================
        static void Tracking()
        {
            var sh = New("Tracking Log", "Prediction log for this slate", "The app grades these automatically (Grade button). The yellow cells are for your own bets: enter closing odds and the result to see Brier, log loss and closing-line value.",
                11, 14, 18, 40, 10, 10, 10, 11, 9, 9, 9, 11);
            Hdr(sh, "Date", "Game", "Market", "Selection", "Model prob", "Fair odds", "DK odds (analysis)", "DK closing odds", "Result 1/0", "Brier", "Log loss", "CLV (implied prob gained)");
            int first = sh.Rows.Count + 1;
            string ds = Program.SlateDate.ToString("yyyy-MM-dd");
            Action<string, string, string, double, double> add = (game, market, sel, p, odds) =>
            {
                var row = sh.Row(); int rn = sh.Rows.Count;
                string E = R(4, rn), G = R(6, rn), Hc = R(7, rn), I = R(8, rn);
                row.Add(Cell.Str(ds)); row.Add(Cell.Str(game)); row.Add(Cell.Str(market)); row.Add(Cell.Str(sel)); row.Add(Cell.Num(p, Styles.Pct)); row.Add(FairOddsFx(E, p));
                row.Add(HasOdds(odds) ? Cell.Num(odds, Styles.OddsBlue) : Cell.Str("n/a"));
                row.Add(Cell.Empty(Styles.InputOdds)); row.Add(Cell.Empty(Styles.InputOdds));
                row.Add(Cell.FxS("IF(" + I + "=\"\",\"\",(" + E + "-" + I + ")^2)", "", Styles.N2));
                row.Add(Cell.FxS("IF(" + I + "=\"\",\"\",-(" + I + "*LN(" + E + ")+(1-" + I + ")*LN(1-" + E + ")))", "", Styles.N2));
                row.Add(Cell.FxS("IF(OR(" + Hc + "=\"\",ISTEXT(" + G + ")),\"\",IF(" + Hc + ">0,100/(" + Hc + "+100),-" + Hc + "/(-" + Hc + "+100))-IF(" + G + ">0,100/(" + G + "+100),-" + G + "/(-" + G + "+100)))", "", Styles.Pct));
            };
            foreach (var r in MlRows()) add(GameLabel(r.G), "Moneyline", r.Team + " to win", r.P, r.DkMl);
            foreach (var g in Program.Games.Where(x => x.HasMarket))
            {
                var d = g.Def.DK;
                add(GameLabel(g), "Spread", g.Def.Home + " " + d.SprH.ToString(U.IC), g.PCoverH, d.SprHOdds);
                add(GameLabel(g), "Total", "Over " + d.Total.ToString(U.IC), g.POver, d.OverOdds);
            }
            foreach (var p in Analysis.Players.OrderByDescending(p => p.Pts.PAtLeast(25)).Take(5))
                add(GameLabel(p.G), "Player Points", p.P.Name + " 25+", p.Pts.PAtLeast(25), double.NaN);
            foreach (var t in Analysis.TeamQs.OrderByDescending(t => t.P100).Take(3))
                add(GameLabel(t.G), "Team Q1-Q3", t.Team + " 100+ through 3Q", t.P100, double.NaN);
            int last = sh.Rows.Count;
            sh.Row();
            var s1 = sh.Row(); s1.Add(Cell.Str("Average", Styles.Bold)); for (int i = 0; i < 8; i++) s1.Add(null);
            s1.Add(Cell.FxS("IFERROR(AVERAGE(" + R(9, first) + ":" + R(9, last) + "),\"\")", "", Styles.N2));
            s1.Add(Cell.FxS("IFERROR(AVERAGE(" + R(10, first) + ":" + R(10, last) + "),\"\")", "", Styles.N2));
            s1.Add(Cell.FxS("IFERROR(AVERAGE(" + R(11, first) + ":" + R(11, last) + "),\"\")", "", Styles.Pct));
        }
    }
}
