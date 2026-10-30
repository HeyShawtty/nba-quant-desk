using System;
using System.Collections.Generic;
using System.Linq;

namespace NbaQuant
{
    // Narrative text for the Summary tab, generated from the slate so it cannot drift from the tables.
    static class Notes
    {
        static double Mpg(PlayerP p) { return Math.Max(p.CsMpg, p.PsMpg); }

        static List<PlayerP> KeyAbsences(string team, int take)
        {
            return Program.TeamPlayers[team].Where(p => p.Status == "OUT" && Mpg(p) >= 18).OrderByDescending(Mpg).Take(take).ToList();
        }
        static List<PlayerP> KeyQuestionable(string team)
        {
            return Program.TeamPlayers[team].Where(p => p.Status == "Q" && p.PPlay <= 0.75 && Mpg(p) >= 15).OrderByDescending(Mpg).ToList();
        }

        public static string MlNote(Report.MlRow r)
        {
            var g = r.G; double margin = r.Home ? g.MeanMargin : -g.MeanMargin;
            var adv = new List<string> { string.Format(U.IC, "Simulated margin {0:+0.0;-0.0}", margin) };
            if (!double.IsNaN(r.PModel) && !double.IsNaN(r.PMkt)) adv.Add(string.Format(U.IC, "independent model {0} vs market {1}", U.Pct(r.PModel), U.Pct(r.PMkt)));
            var oppOut = KeyAbsences(r.Opp, 3);
            if (oppOut.Count > 0) adv.Add(r.Opp + " without " + string.Join(", ", oppOut.Select(p => p.Name)));
            int myRest = r.Home ? g.Def.HomeRest : g.Def.AwayRest, oppRest = r.Home ? g.Def.AwayRest : g.Def.HomeRest;
            if (myRest > oppRest && oppRest == 0) adv.Add(r.Opp + " on a back-to-back");
            var risk = new List<string>();
            if (!double.IsNaN(r.DkMl) && Math.Abs(r.DkMl) >= 100)
                risk.Add(string.Format(U.IC, "price {0} needs {1} to break even (model {2})", U.Am(r.DkMl), U.Pct(U.ImpliedProb(r.DkMl)), U.Pct(r.P)));
            var myOut = KeyAbsences(r.Team, 2); if (myOut.Count > 0) risk.Add("own absences: " + string.Join(", ", myOut.Select(p => p.Name)));
            var myQ = KeyQuestionable(r.Team).Concat(KeyQuestionable(r.Opp)).ToList(); if (myQ.Count > 0) risk.Add("questionable: " + string.Join(", ", myQ.Select(p => p.Name + " (" + p.Team + ")")));
            if (myRest == 0) risk.Add("2nd night of a back-to-back");
            if (Math.Abs(margin) < 4) risk.Add("projected margin under 4 points: close to a coin flip");
            if (risk.Count == 0) risk.Add("ordinary game variance; margins carry a ~14-point standard deviation");
            return string.Join("; ", adv) + ". | Risk: " + string.Join("; ", risk) + ".";
        }

        public static string Q3Note(TeamQ t)
        {
            return string.Format(U.IC, "Full-game projection {0:0.0}; game total median {1}. Needs roughly a 133-point full-game pace.", t.MeanFull, t.G.TotalPct[2]);
        }

        public static Combo BestSgp()
        {
            Combo best = null;
            foreach (var g in Program.Games)
                foreach (var c in Analysis.BoostSgp[g])
                    if (best == null || c.Joint * Math.Min(c.Lift, 1.5) > best.Joint * Math.Min(best.Lift, 1.5)) best = c;
            return best;
        }

        public static string GameOf(Combo c)
        {
            foreach (var g in Program.Games)
                if (Analysis.BoostSgp[g].Contains(c) || Analysis.BestHitSgp[g] == c || Analysis.RiskySgp[g] == c || Analysis.LiftSgp[g] == c)
                    return g.Def.Away + " @ " + g.Def.Home;
            return "";
        }

        public static List<string> Contrarian()
        {
            var res = new List<string>();
            var rows = Report.MlRows().Where(r => !double.IsNaN(r.PMkt) && !double.IsNaN(r.DkMl) && Math.Abs(r.DkMl) >= 100).ToList();
            foreach (var r in rows.Where(x => U.EvPerDollar(x.P, x.DkMl) > 0).OrderByDescending(x => U.EvPerDollar(x.P, x.DkMl)).Take(2))
            {
                double mu = r.Home ? r.G.MeanMargin : -r.G.MeanMargin;
                double ev = U.EvPerDollar(r.P, r.DkMl), ev12 = U.EvPerDollar(Report.Phi(mu / 12), r.DkMl), ev13 = U.EvPerDollar(Report.Phi(mu / 13), r.DkMl);
                res.Add(string.Format(U.IC, "{0} moneyline {1}: simulation {2} vs {3} DraftKings no-vig, EV {4:+0.0%;-0.0%}. With a 12-13 point margin SD the EV is {5:+0.0%;-0.0%} / {6:+0.0%;-0.0%}{7}",
                    r.Team, U.Am(r.DkMl), U.Pct(r.P), U.Pct(r.PMkt), ev, ev12, ev13, ev12 > 0 && ev13 > 0 ? ", so it survives the variance check (still unvalidated)." : ": not robust, treat as a watch item."));
            }
            // injury-driven role changes: projected minutes well above the player's own baseline
            var bumps = new List<Tuple<PStat, double>>();
            foreach (var ps in Analysis.Players.Where(p => p.MeanMin >= 18 && p.P.Status != "Q"))
            {
                double baseMin = ps.P.Gp >= 2 ? ps.P.CsMpg : ps.P.PsMpg;
                if (baseMin > 0 && ps.MeanMin - baseMin >= 4) bumps.Add(Tuple.Create(ps, ps.MeanMin - baseMin));
            }
            foreach (var b in bumps.OrderByDescending(x => x.Item2).Take(3))
            {
                var ps = b.Item1;
                var outMates = KeyAbsences(ps.P.Team, 2);
                res.Add(string.Format(U.IC, "Role change: {0} ({1}) projects {2:0.0} min vs a {3:0.0} baseline{4}: {5:0.0} pts / {6:0.0} reb / {7:0.0} ast. Books sometimes lag these minute bumps on alternate lines.",
                    ps.P.Name, ps.P.Team, ps.MeanMin, ps.MeanMin - b.Item2, outMates.Count > 0 ? " with " + string.Join(", ", outMates.Select(p => p.Name)) + " out" : "", ps.Pts.Mean, ps.Reb.Mean, ps.Ast.Mean));
            }
            var reb = Analysis.Players.Where(p => p.MeanMin >= 20).OrderByDescending(p => p.Reb.Mean).FirstOrDefault();
            if (reb != null)
                res.Add(string.Format(U.IC, "Top projected rebounder: {0} ({1:0.0}; P(12+) {2}). Blowout risk in that game is {3} (20+ point margin), which trims fourth-quarter minutes on high alternate lines.",
                    reb.P.Name, reb.Reb.Mean, U.Pct(reb.Reb.PAtLeast(12)), U.Pct(reb.G.PBlowout)));
            res.Add(Ledger.BiasNote());
            return res;
        }

        public static List<string> Final()
        {
            var res = new List<string>();
            var ml = Report.MlRows();
            var robust = ml.Where(r => !double.IsNaN(r.DkMl) && Math.Abs(r.DkMl) >= 100).Where(r =>
            {
                double mu = r.Home ? r.G.MeanMargin : -r.G.MeanMargin;
                return U.EvPerDollar(r.P, r.DkMl) > 0.03 && U.EvPerDollar(Report.Phi(mu / 12), r.DkMl) > 0 && U.EvPerDollar(Report.Phi(mu / 13), r.DkMl) > 0;
            }).ToList();
            if (robust.Count > 0)
            {
                res.Add("Moneyline edge(s) that survive the variance check: " + string.Join(", ", robust.Select(r => string.Format(U.IC, "{0} {1} (model {2} vs market {3}, EV {4:+0.0%;-0.0%})", r.Team, U.Am(r.DkMl), U.Pct(r.P), U.Pct(r.PMkt), U.EvPerDollar(r.P, r.DkMl)))) + ". The model is still unvalidated; size small and record closing-line value.");
                foreach (var r in robust)
                {
                    var q = KeyQuestionable(r.Team).Concat(KeyQuestionable(r.Opp)).ToList();
                    if (q.Count > 0)
                        res.Add(string.Format(U.IC, "Caution on {0}: the gap to the market sits in a game with questionable rotation players ({1}). The market may already know their status better than the injury tag; the minimum acceptable price is {2} and only once their status is confirmed.",
                            r.Team, string.Join(", ", q.Select(p => p.Name + " (" + p.Team + ")")), U.Am(U.FairAmerican(r.P - 0.03))));
                }
            }
            else
                res.Add("NO QUALIFYING STRAIGHT BET. No moneyline shows a positive EV that survives reasonable changes to the model's variance assumption" + (Report.PropEdges().Count == 0 ? ", and no player-prop prices were available to test." : "; prop edges in the Props vs Book tab are unvalidated."));
            var best = BestSgp();
            if (best != null)
            {
                var g = Program.Games.First(x => GameOf(best) == x.Def.Away + " @ " + x.Def.Home);
                var qs = g.H.Pl.Concat(g.A.Pl).Where(x => x.Status == "Q" && x.ExpMin >= 6).Select(x => x.Name + " (" + x.Team + ")").ToList();
                string skip = qs.Count > 0 ? "Re-run or skip it if " + string.Join(" / ", qs) + " is ruled in or out, or if a starter in this game is added to the injury report." : "Skip it if a starter in this game is added to the injury report.";
                res.Add(string.Format(U.IC, "Strongest research candidate: the 50% profit-boost SGP {0}: {1}. Evidence: {2} joint hit rate in {3:N0} correlated simulations (fair {4}); a qualifying +300 price boosted to +450 needs only an 18.2% true rate. Minimum acceptable price: +300 displayed before the boost, and only if the boost terms are confirmed in the app. {5}",
                    GameOf(best), string.Join(" + ", best.Legs.Select(l => l.Desc)), U.Pct(best.Joint), Program.NSIM, U.Am(U.FairAmerican(best.Joint)), skip));
                double ev = best.Joint * 4.5 - (1 - best.Joint);
                res.Add(string.Format(U.IC, "Size: the boost is the edge, not the picks. Keep it to the $5 promo stake: at exactly +300 boosted to +450 the EV is about {0:+$0.00;-$0.00} per $5 if the simulated probability is right, and the ticket still loses about {1:0}% of the time.", ev * 5, 100 * (1 - best.Joint)));
            }
            return res;
        }
    }
}
