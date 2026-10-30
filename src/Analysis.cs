using System;
using System.Collections.Generic;
using System.Linq;

namespace NbaQuant
{
    class Dist
    {
        public double Mean, P10, P25, P50, P75, P90, P95, P99;
        public int[] Hist = new int[512];
        public int N;
        public double CalA, CalB = 1;    // logit calibration map for threshold probabilities (identity by default)
        public double PAtLeastRaw(int k) { if (k <= 0) return 1; long c = 0; for (int v = k; v < Hist.Length; v++) c += Hist[v]; return (double)c / N; }
        public double PAtLeast(int k) { return Analysis.Cal(PAtLeastRaw(k), CalA, CalB); }
        // keep: which simulations count (props are conditional on the player playing, as books void DNPs)
        public static Dist From(Func<int, int> val, int total, Func<int, bool> keep)
        {
            var d = new Dist();
            double sum = 0; int n = 0;
            for (int s = 0; s < total; s++) { if (!keep(s)) continue; int v = Math.Min(511, val(s)); d.Hist[v]++; sum += v; n++; }
            if (n == 0) { d.Hist[0] = 1; n = 1; }
            d.N = n;
            d.Mean = sum / n;
            Func<double, double> q = p => { long target = (long)Math.Floor(p * (n - 1)); long c = 0; for (int v = 0; v < 512; v++) { c += d.Hist[v]; if (c > target) return v; } return 511; };
            d.P10 = q(0.10); d.P25 = q(0.25); d.P50 = q(0.5); d.P75 = q(0.75); d.P90 = q(0.90); d.P95 = q(0.95); d.P99 = q(0.99);
            return d;
        }
    }

    class PStat
    {
        public PlayerP P; public GameCtx G; public int Gi; public bool Home; public string Opp;
        public double MeanMin, PPlays;   // minutes when playing; share of simulations in which the player logs minutes
        public Dist Pts, Reb, Ast, Tpm, Pra;
        public double DD, DDRaw, TD, Q3All, FirstFG;   // DD is calibrated (backtest map); DDRaw is the raw simulation share
        public int Conf; public string ConfNote;
    }

    class Leg
    {
        public string Desc, Kind, Key; public double P; public ulong[] Bits, PlayedBits; public PStat Ps; public int Line;
        public double Adj = 1;   // calibrated / raw single-leg probability; joint probabilities are scaled by the product of these
        public string Team; public double LineD; // game-level legs: team (or OVER/UNDER) and handicap/total
    }

    class Combo { public Leg[] Legs; public double Joint, Product; public double Lift { get { return Joint / Product; } } }

    class TeamQ { public string Team, Opp; public bool Home; public GameCtx G; public double Mean; public double P90, P95, P100, P105; public double MeanFull; }

    class Leader { public string Category; public PStat Ps; public string Team; public double P; }

    static class Analysis
    {
        public static List<PStat> Players = new List<PStat>();
        public static List<TeamQ> TeamQs = new List<TeamQ>();
        public static Dictionary<GameCtx, List<Leg>> Legs = new Dictionary<GameCtx, List<Leg>>();
        public static Dictionary<GameCtx, Combo> BestHitSgp = new Dictionary<GameCtx, Combo>();
        public static Dictionary<GameCtx, List<Combo>> BoostSgp = new Dictionary<GameCtx, List<Combo>>();
        public static Dictionary<GameCtx, Combo> RiskySgp = new Dictionary<GameCtx, Combo>();
        public static Dictionary<GameCtx, Combo> LiftSgp = new Dictionary<GameCtx, Combo>();
        public static List<Leader> Leaders = new List<Leader>();
        public static readonly int[] PtsT = { 10, 15, 20, 25, 30, 35, 40 }, RebT = { 5, 8, 10, 12, 15 }, AstT = { 4, 5, 6, 8, 10, 12 }, TpmT = { 1, 2, 3, 4, 5, 6 }, PraT = { 20, 25, 30, 35, 40, 45, 50 };

        public static double Cal(double p, double a, double b)
        {
            if (Math.Abs(b - 1) < 1e-9 && Math.Abs(a) < 1e-9) return p;
            if (p <= 0 || p >= 1) return p;
            double q = Math.Min(0.9999, Math.Max(0.0001, p));
            return 1 / (1 + Math.Exp(-(a + b * Math.Log(q / (1 - q)))));
        }
        public static double Uncal(double p, double a, double b)
        {
            if (Math.Abs(b - 1) < 1e-9 && Math.Abs(a) < 1e-9) return p;
            if (p <= 0 || p >= 1) return p;
            double q = Math.Min(0.9999, Math.Max(0.0001, p));
            return 1 / (1 + Math.Exp(-((Math.Log(q / (1 - q)) - a) / b)));
        }
        // settings PropCal "PTS:a:b;REB:a:b;..." (fitted out of sample by Recalibrate)
        public static Tuple<double, double> PropMap(string stat)
        {
            foreach (var part in (Program.S.PropCal ?? "").Split(';'))
            {
                var f = part.Split(':');
                if (f.Length == 3 && f[0] == stat) return Tuple.Create(U.D(f[1]), U.D(f[2], 1));
            }
            return Tuple.Create(0.0, 1.0);
        }
        static void ApplyMap(Dist d, string stat) { var m = PropMap(stat); d.CalA = m.Item1; d.CalB = m.Item2; }

        public static double CalibrateDd(double p)
        {
            var S = Program.S;
            if (Math.Abs(S.DdLogitSlope - 1) < 1e-9 && Math.Abs(S.DdLogitShift) < 1e-9) return p;
            double q = Math.Min(0.999, Math.Max(0.001, p));
            double z = S.DdLogitShift + S.DdLogitSlope * Math.Log(q / (1 - q));
            return 1 / (1 + Math.Exp(-z));
        }

        public static void Reset()
        {
            Players = new List<PStat>(); TeamQs = new List<TeamQ>(); Legs = new Dictionary<GameCtx, List<Leg>>();
            BestHitSgp = new Dictionary<GameCtx, Combo>(); BoostSgp = new Dictionary<GameCtx, List<Combo>>(); RiskySgp = new Dictionary<GameCtx, Combo>();
            LiftSgp = new Dictionary<GameCtx, Combo>(); Leaders = new List<Leader>();
        }

        public static void Run(bool sgp = true)
        {
            Reset();
            foreach (var g in Program.Games)
            {
                var r = g.Res; int n = r.N;
                for (int i = 0; i < g.Players.Count; i++)
                {
                    int gi = i;
                    bool home = i < r.NH;
                    var pl = r.Played[gi]; int np = Math.Max(1, r.PlayedCount[gi]);
                    var ps = new PStat { P = g.Players[i], G = g, Gi = i, Home = home, Opp = home ? g.Def.Away : g.Def.Home, MeanMin = r.MinSum[i] / np, PPlays = (double)r.PlayedCount[gi] / n };
                    Func<int, bool> keep = s => pl[s];
                    ps.Pts = Dist.From(s => r.Pts[gi][s], n, keep); ps.Reb = Dist.From(s => r.Reb[gi][s], n, keep); ps.Ast = Dist.From(s => r.Ast[gi][s], n, keep);
                    ps.Tpm = Dist.From(s => r.Tpm[gi][s], n, keep); ps.Pra = Dist.From(s => r.Pts[gi][s] + r.Reb[gi][s] + r.Ast[gi][s], n, keep);
                    ApplyMap(ps.Pts, "PTS"); ApplyMap(ps.Reb, "REB"); ApplyMap(ps.Ast, "AST"); ApplyMap(ps.Tpm, "3PM"); ApplyMap(ps.Pra, "PRA");
                    int dd = 0, td = 0, q3 = 0, ff = 0;
                    for (int s = 0; s < n; s++)
                    {
                        if (!pl[s]) continue;
                        int c = (r.Pts[gi][s] >= 10 ? 1 : 0) + (r.Reb[gi][s] >= 10 ? 1 : 0) + (r.Ast[gi][s] >= 10 ? 1 : 0);
                        if (c >= 2) dd++; if (c >= 3) td++;
                        if (r.Q3All[gi][s]) q3++;
                        if (r.FirstFG[s] == gi) ff++;
                    }
                    ps.DDRaw = (double)dd / np; ps.TD = (double)td / np; ps.Q3All = (double)q3 / np; ps.FirstFG = (double)ff / np;
                    ps.DD = CalibrateDd(ps.DDRaw);
                    Confidence(ps);
                    Players.Add(ps);
                }
                foreach (bool home in new[] { true, false })
                {
                    var tq = new TeamQ { Team = home ? g.Def.Home : g.Def.Away, Opp = home ? g.Def.Away : g.Def.Home, Home = home, G = g };
                    int c90 = 0, c95 = 0, c100 = 0, c105 = 0; double sum = 0, sf = 0;
                    for (int s = 0; s < n; s++)
                    {
                        int v = home ? r.HomeQ123[s] : r.AwayQ123[s];
                        sum += v; sf += home ? r.HomePts[s] : r.AwayPts[s];
                        if (v >= 90) c90++; if (v >= 95) c95++; if (v >= 100) c100++; if (v >= 105) c105++;
                    }
                    tq.Mean = sum / n; tq.MeanFull = sf / n; tq.P90 = (double)c90 / n; tq.P95 = (double)c95 / n; tq.P100 = (double)c100 / n; tq.P105 = (double)c105 / n;
                    TeamQs.Add(tq);
                }
            }
            if (sgp)
                System.Threading.Tasks.Parallel.ForEach(Program.Games, g =>
                {
                    var legs = BuildLegs(g);
                    lock (Legs) Legs[g] = legs;
                    SearchSgp(g, legs);
                });
            else
                foreach (var g in Program.Games) { BestHitSgp[g] = null; BoostSgp[g] = new List<Combo>(); RiskySgp[g] = null; LiftSgp[g] = null; }
            SlateLeaders();
        }

        static void Confidence(PStat ps)
        {
            var p = ps.P; var notes = new List<string>();
            int c = 70;
            if (p.Status == "Q") { c -= 15; notes.Add("own status questionable"); }
            var team = ps.Home ? ps.G.H : ps.G.A;
            if (team.Pl.Any(x => x != p && x.Status == "Q" && x.ExpMin >= 12)) { c -= 5; notes.Add("teammate questionable"); }
            if (p.Gp <= 1) { c -= 8; notes.Add("<=1 game this season"); }
            if (!p.SameTeam && p.Gp < 3) { c -= 5; notes.Add("new team, small sample"); }
            if (!p.HasPs) { c -= 5; notes.Add("no prior NBA season"); }
            if (p.PsStale) { c -= 4; notes.Add("prior from 2024-25"); }
            if (p.Gp >= 3 && p.SameTeam) { c += 5; notes.Add("stable role"); }
            bool b2b = ps.Home ? ps.G.Def.HomeB2B : ps.G.Def.AwayB2B;
            if (b2b) { c -= 6; notes.Add("2nd night of back-to-back (rest risk)"); }
            if (!ps.G.HasMarket) { c -= 8; notes.Add("no market line for this game"); }
            ps.Conf = Math.Max(20, Math.Min(90, c));
            ps.ConfNote = string.Join("; ", notes);
        }

        public static int GameConfidence(GameCtx g, out string note)
        {
            var notes = new List<string>();
            double c = 75;
            double nv = Program.NoVigHome(g.Def.DK);
            if (!double.IsNaN(nv) && !double.IsNaN(g.ModelWinH))
            {
                double dis = Math.Abs(g.ModelWinH - nv);
                c -= 60 * dis; notes.Add("model-vs-market gap " + U.Pct(dis));
            }
            if (!g.HasMarket) { c -= 15; notes.Add("no market line"); }
            int q = g.H.Pl.Concat(g.A.Pl).Count(x => x.Status == "Q" && x.ExpMin >= 12);
            if (q > 0) { c -= 4 * q; notes.Add(q + " questionable rotation player(s)"); }
            if (g.Def.HomeB2B || g.Def.AwayB2B) { c -= 6; notes.Add("back-to-back team"); }
            c -= 5; notes.Add("opening-week sample");
            note = string.Join("; ", notes);
            return (int)Math.Max(20, Math.Min(90, Math.Round(c)));
        }

        static ulong[] Bits(int n, Func<int, bool> f)
        {
            var b = new ulong[(n + 63) / 64];
            for (int s = 0; s < n; s++) if (f(s)) b[s >> 6] |= 1UL << (s & 63);
            return b;
        }

        static int Pop(ulong x)
        {
            x = x - ((x >> 1) & 0x5555555555555555UL);
            x = (x & 0x3333333333333333UL) + ((x >> 2) & 0x3333333333333333UL);
            x = (x + (x >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((x * 0x0101010101010101UL) >> 56);
        }

        static List<Leg> BuildLegs(GameCtx g)
        {
            var r = g.Res; int n = r.N; var d = g.Def; var legs = new List<Leg>();
            Action<string, string, string, Func<int, bool>, PStat, int> add = (desc, kind, key, f, ps, line) =>
            {
                var b = Bits(n, f); long c = 0; foreach (var w in b) c += Pop(w);
                ulong[] played = null; long den = n;
                if (ps != null) { int gi = ps.Gi; played = Bits(n, s => r.Played[gi][s]); den = 0; foreach (var w in played) den += Pop(w); }
                double raw = (double)c / Math.Max(1, den), cal = raw;
                if (ps != null) { if (kind == "DD") cal = CalibrateDd(raw); else { var m = PropMap(kind); cal = Cal(raw, m.Item1, m.Item2); } }
                legs.Add(new Leg { Desc = desc, Kind = kind, Key = key, Bits = b, PlayedBits = played, P = cal, Adj = raw > 0 ? cal / raw : 1, Ps = ps, Line = line });
            };
            double spr = d.DK.SprH, tot = d.DK.Total;
            Action<string, string, string, double, Func<int, bool>> addG = (desc, kind, team, lineD, f) =>
            {
                add(desc, kind, kind, f, null, 0);
                legs[legs.Count - 1].Team = team; legs[legs.Count - 1].LineD = lineD;
            };
            addG(d.Home + " moneyline", "ML", d.Home, 0, s => r.HomePts[s] > r.AwayPts[s]);
            addG(d.Away + " moneyline", "ML", d.Away, 0, s => r.AwayPts[s] > r.HomePts[s]);
            addG(d.Home + " " + (spr > 0 ? "+" : "") + spr.ToString(U.IC), "SPR", d.Home, spr, s => r.HomePts[s] - r.AwayPts[s] + spr > 0);
            addG(d.Away + " " + (-spr > 0 ? "+" : "") + (-spr).ToString(U.IC), "SPR", d.Away, -spr, s => r.AwayPts[s] - r.HomePts[s] - spr > 0);
            addG("Over " + tot.ToString(U.IC), "TOT", "OVER", tot, s => r.HomePts[s] + r.AwayPts[s] > tot);
            addG("Under " + tot.ToString(U.IC), "TOT", "UNDER", tot, s => r.HomePts[s] + r.AwayPts[s] < tot);
            // 15+ minutes so bench players with real roles can enter parlays, not only the headline names
            foreach (var ps in Players.Where(x => x.G == g && x.MeanMin >= 15 && x.PPlays >= 0.9 && x.P.Status != "Q"))
            {
                int gi = ps.Gi; string nm = ps.P.Name;
                foreach (int t in new[] { 10, 15, 20, 25, 30, 35 }) { int tt = t; add(nm + " " + t + "+ pts", "PTS", nm + "|PTS", s => r.Pts[gi][s] >= tt, ps, t); }
                foreach (int t in new[] { 4, 6, 8, 10, 12 }) { int tt = t; add(nm + " " + t + "+ reb", "REB", nm + "|REB", s => r.Reb[gi][s] >= tt, ps, t); }
                foreach (int t in new[] { 2, 4, 6, 8, 10 }) { int tt = t; add(nm + " " + t + "+ ast", "AST", nm + "|AST", s => r.Ast[gi][s] >= tt, ps, t); }
                foreach (int t in new[] { 1, 2, 3, 4 }) { int tt = t; add(nm + " " + t + "+ threes", "3PM", nm + "|3PM", s => r.Tpm[gi][s] >= tt, ps, t); }
                foreach (int t in new[] { 15, 20, 25, 30, 35, 40, 45 }) { int tt = t; add(nm + " " + t + "+ PRA", "PRA", nm + "|PRA", s => r.Pts[gi][s] + r.Reb[gi][s] + r.Ast[gi][s] >= tt, ps, t); }
                add(nm + " double-double", "DD", nm + "|DD", s => (r.Pts[gi][s] >= 10 ? 1 : 0) + (r.Reb[gi][s] >= 10 ? 1 : 0) + (r.Ast[gi][s] >= 10 ? 1 : 0) >= 2, ps, 0);
            }
            return legs.Where(l => l.P >= 0.40 && l.P <= 0.93).ToList();
        }

        // Four-leg tickets for one game, from the joint simulation. Keeps the combinations whose joint probability falls in
        // [lo, hi], ranked by joint probability (capped correlation bonus), skipping near-duplicates.
        public static List<Combo> Best4(GameCtx g, double lo, double hi, int take)
        {
            int n = g.Res.N;
            // longer-odds bands need coin-flip-ish legs: four 90% legs can never land at 9-14% joint
            double target = hi < 0.2 ? 0.58 : 0.75, maxP = hi < 0.2 ? 0.75 : 0.93;
            var legs = BuildLegs(g).Where(l => l.P >= 0.45 && l.P <= maxP).ToList();
            var pool = legs.Where(l => l.Ps == null).Concat(legs.Where(l => l.Ps != null).GroupBy(l => l.Key)
                .SelectMany(grp => grp.OrderBy(l => Math.Abs(l.P - target)).Take(2))).OrderByDescending(l => l.P).Take(44).ToList();
            var found = new List<Combo>();
            int m = pool.Count;
            for (int a = 0; a < m; a++)
                for (int b = a + 1; b < m; b++)
                {
                    if (!Compatible(new[] { pool[a], pool[b] })) continue;
                    for (int c = b + 1; c < m; c++)
                    {
                        if (!Compatible(new[] { pool[a], pool[b], pool[c] })) continue;
                        for (int d = c + 1; d < m; d++)
                        {
                            var ls = new[] { pool[a], pool[b], pool[c], pool[d] };
                            if (!Compatible(ls)) continue;
                            double prod = ls.Aggregate(1.0, (x, l) => x * l.P);
                            if (prod > hi * 1.2 || prod < lo * 0.4) continue;
                            double jp = JointP(n, ls);
                            if (jp < lo || jp > hi) continue;
                            found.Add(new Combo { Legs = ls, Joint = jp, Product = prod });
                        }
                    }
                }
            var picked = new List<Combo>();
            foreach (var c in found.OrderByDescending(x => x.Joint * Math.Min(x.Lift, 1.5)))
            {
                if (picked.Any(pk => pk.Legs.Select(l => l.Key).Intersect(c.Legs.Select(l => l.Key)).Count() >= 3)) continue;
                picked.Add(c); if (picked.Count >= take) break;
            }
            return picked;
        }

        static bool Compatible(Leg[] ls)
        {
            for (int i = 0; i < ls.Length; i++)
                for (int j = i + 1; j < ls.Length; j++)
                {
                    if (ls[i].Key == ls[j].Key) return false;
                    // moneyline + spread on the same game is an implication, not a correlation (and books restrict it)
                    if ((ls[i].Kind == "ML" || ls[i].Kind == "SPR") && (ls[j].Kind == "ML" || ls[j].Kind == "SPR")) return false;
                    if (ls[i].Ps != null && ls[i].Ps == ls[j].Ps && (ls[i].Kind == "PRA" || ls[j].Kind == "PRA")) return false;
                }
            return true;
        }

        static double JointP(int n, params Leg[] ls)
        {
            long c = 0; int w = ls[0].Bits.Length;
            if (ls.Length == 3) { var a = ls[0].Bits; var b = ls[1].Bits; var d = ls[2].Bits; for (int k = 0; k < w; k++) c += Pop(a[k] & b[k] & d[k]); }
            else { for (int k = 0; k < w; k++) { ulong x = ulong.MaxValue; foreach (var l in ls) x &= l.Bits[k]; c += Pop(x); } }
            // condition on every player in the ticket taking the floor (a DNP leg is voided, not lost)
            double adj = ls.Aggregate(1.0, (x, l) => x * l.Adj);
            var pl = ls.Where(l => l.PlayedBits != null).Select(l => l.PlayedBits).Distinct().ToList();
            if (pl.Count == 0) return Math.Min(1, adj * c / n);
            long den = 0;
            for (int k = 0; k < w; k++) { ulong x = ulong.MaxValue; foreach (var p in pl) x &= p[k]; den += Pop(x); }
            return Math.Min(1, adj * c / Math.Max(1, den));
        }

        static void SearchSgp(GameCtx g, List<Leg> legs)
        {
            int n = g.Res.N; int m = legs.Count;
            Combo bestHit = null, bestLift = null; var boost = new List<Combo>();
            for (int i = 0; i < m; i++)
                for (int j = i + 1; j < m; j++)
                {
                    if (!Compatible(new[] { legs[i], legs[j] })) continue;
                    for (int k = j + 1; k < m; k++)
                    {
                        var ls = new[] { legs[i], legs[j], legs[k] };
                        if (!Compatible(ls)) continue;
                        double prod = legs[i].P * legs[j].P * legs[k].P;
                        if (prod > 0.95 || prod < 0.06) continue;
                        double jp = JointP(n, ls);
                        var c = new Combo { Legs = ls, Joint = jp, Product = prod };
                        if (bestHit == null || jp > bestHit.Joint) bestHit = c;
                        // boost window: fair odds about +325..+400 so a DraftKings price (which carries hold) can still clear +300;
                        // legs restricted to mainstream thresholds and to combinations that genuinely move together
                        if (jp >= 0.20 && jp <= 0.235 && c.Lift >= 1.03 && ls.All(l => l.P >= 0.45 && l.P <= 0.88)) boost.Add(c);
                        if (jp >= 0.15 && (bestLift == null || c.Lift > bestLift.Lift)) bestLift = c;
                    }
                }
            lock (Legs) { BestHitSgp[g] = bestHit; LiftSgp[g] = bestLift; }
            // diversified boost list: highest joint probability within the +300..+426 fair window, avoid near-duplicates
            var picked = new List<Combo>();
            foreach (var c in boost.OrderByDescending(c => c.Joint * Math.Min(c.Lift, 1.5)))
            {
                if (picked.Any(pk => pk.Legs.Select(l => l.Key).Intersect(c.Legs.Select(l => l.Key)).Count() >= 2)) continue;
                picked.Add(c); if (picked.Count >= 5) break;
            }
            lock (Legs) BoostSgp[g] = picked;
            // higher-risk four-leg ticket from the most likely legs
            var top = legs.Where(l => l.P >= 0.45 && l.P <= 0.75).OrderByDescending(l => l.P).Take(30).ToList();
            Combo risky = null;
            for (int a = 0; a < top.Count; a++)
                for (int b = a + 1; b < top.Count; b++)
                    for (int c2 = b + 1; c2 < top.Count; c2++)
                        for (int d = c2 + 1; d < top.Count; d++)
                        {
                            var ls = new[] { top[a], top[b], top[c2], top[d] };
                            if (!Compatible(ls)) continue;
                            double prod = ls.Aggregate(1.0, (x, l) => x * l.P);
                            if (prod > 0.2 || prod < 0.03) continue;
                            double jp = JointP(n, ls);
                            if (jp < 0.08 || jp > 0.14) continue;
                            var cmb = new Combo { Legs = ls, Joint = jp, Product = prod };
                            if (risky == null || cmb.Joint * Math.Min(cmb.Lift, 2) > risky.Joint * Math.Min(risky.Lift, 2)) risky = cmb;
                        }
            lock (Legs) RiskySgp[g] = risky;
        }

        static void SlateLeaders()
        {
            int n = Program.NSIM;
            var all = Players.Where(p => p.MeanMin >= 12).ToList();
            foreach (var cat in new[] { "Most points (player)", "Most rebounds (player)", "Most assists (player)" })
            {
                var share = new double[all.Count];
                for (int s = 0; s < n; s++)
                {
                    int best = -1; int cnt = 0;
                    for (int i = 0; i < all.Count; i++)
                    {
                        var r = all[i].G.Res; int gi = all[i].Gi;
                        int v = cat.Contains("points") ? r.Pts[gi][s] : cat.Contains("rebounds") ? r.Reb[gi][s] : r.Ast[gi][s];
                        if (v > best) { best = v; cnt = 1; } else if (v == best) cnt++;
                    }
                    for (int i = 0; i < all.Count; i++)
                    {
                        var r = all[i].G.Res; int gi = all[i].Gi;
                        int v = cat.Contains("points") ? r.Pts[gi][s] : cat.Contains("rebounds") ? r.Reb[gi][s] : r.Ast[gi][s];
                        if (v == best) share[i] += 1.0 / cnt; // dead-heat split
                    }
                }
                for (int i = 0; i < all.Count; i++) Leaders.Add(new Leader { Category = cat, Ps = all[i], Team = all[i].P.Team, P = share[i] / n });
            }
            // team-level: most points, most made threes (8 teams)
            var teams = new List<Tuple<string, GameCtx, bool>>();
            foreach (var g in Program.Games) { teams.Add(Tuple.Create(g.Def.Home, g, true)); teams.Add(Tuple.Create(g.Def.Away, g, false)); }
            foreach (var cat in new[] { "Most points (team)", "Most made threes (team)" })
            {
                var share = new double[teams.Count];
                var vals = new int[teams.Count];
                for (int s = 0; s < n; s++)
                {
                    for (int t = 0; t < teams.Count; t++)
                    {
                        var g = teams[t].Item2; var r = g.Res; bool home = teams[t].Item3;
                        if (cat.Contains("points")) vals[t] = home ? r.HomePts[s] : r.AwayPts[s];
                        else { int sum = 0; int from = home ? 0 : r.NH, to = home ? r.NH : r.NH + r.NA; for (int i = from; i < to; i++) sum += r.Tpm[i][s]; vals[t] = sum; }
                    }
                    int best = vals.Max(); int cnt = vals.Count(v => v == best);
                    for (int t = 0; t < teams.Count; t++) if (vals[t] == best) share[t] += 1.0 / cnt;
                }
                for (int t = 0; t < teams.Count; t++) Leaders.Add(new Leader { Category = cat, Team = teams[t].Item1, P = share[t] / n });
            }
        }
    }
}
