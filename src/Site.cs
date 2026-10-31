using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace NbaQuant
{
    // Website data export: tonight's slate, projected box scores, picks, the season bank, the live track record and the audit,
    // written as site\data.js (window.NBA = {...}) for the NBA Quant Desk website.
    static partial class Report
    {
        static double R1(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 1); }
        static double R3(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 3); }
        static Dictionary<string, object> O(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }

        static int ModeIdx(int[] h) { int b = 0; for (int v = 1; v < h.Length; v++) if (h[v] > h[b]) b = v; return b; }
        // most common value after pooling +/-w neighbours: team scores, margins and totals are spread thin (2-3% per exact value)
        static int SmoothIdx(int[] h, int w = 2)
        {
            int b = 0; long bs = -1;
            for (int v = 0; v < h.Length; v++)
            {
                long s = 0; for (int k = Math.Max(0, v - w); k <= Math.Min(h.Length - 1, v + w); k++) s += h[k];
                if (s > bs || (s == bs && h[v] > h[b])) { bs = s; b = v; }
            }
            return b;
        }

        // Most common outcomes from the recorded simulations: each team's final score, margin, total and team stat line,
        // the most common exact final score (smoothed over +/-2 points per team so one noisy cell cannot win), and one real
        // simulated game with that score whose box score sits closest to every player's most common line.
        static object ModeJson(GameCtx g, List<PStat> ps)
        {
            var r = g.Res; int N = r.N, nh = r.NH, np = nh + r.NA;
            if (r.HomePts == null || r.Pts == null) return null;
            int[] hH = new int[400], hA = new int[400], hM = new int[401], hT = new int[700];
            int[][] team = new int[6][]; for (int k = 0; k < 6; k++) team[k] = new int[256]; // reb, ast, 3pm for home then away
            var pair = new Dictionary<int, int>();
            for (int s = 0; s < N; s++)
            {
                int h = Math.Min(399, (int)r.HomePts[s]), a = Math.Min(399, (int)r.AwayPts[s]);
                hH[h]++; hA[a]++; hM[Math.Max(0, Math.Min(400, h - a + 200))]++; hT[Math.Min(699, h + a)]++;
                int key = h * 1000 + a; int c; pair.TryGetValue(key, out c); pair[key] = c + 1;
                int rb0 = 0, as0 = 0, tp0 = 0, rb1 = 0, as1 = 0, tp1 = 0;
                for (int i = 0; i < np; i++)
                {
                    if (i < nh) { rb0 += r.Reb[i][s]; as0 += r.Ast[i][s]; tp0 += r.Tpm[i][s]; }
                    else { rb1 += r.Reb[i][s]; as1 += r.Ast[i][s]; tp1 += r.Tpm[i][s]; }
                }
                team[0][Math.Min(255, rb0)]++; team[1][Math.Min(255, as0)]++; team[2][Math.Min(255, tp0)]++;
                team[3][Math.Min(255, rb1)]++; team[4][Math.Min(255, as1)]++; team[5][Math.Min(255, tp1)]++;
            }
            int bestKey = -1; long bestSm = -1; int bestRaw = -1;
            foreach (var kv in pair)
            {
                int h = kv.Key / 1000, a = kv.Key % 1000; long sm = 0;
                for (int dh = -2; dh <= 2; dh++) for (int da = -2; da <= 2; da++) { int c; if (pair.TryGetValue((h + dh) * 1000 + (a + da), out c)) sm += c; }
                if (sm > bestSm || (sm == bestSm && kv.Value > bestRaw)) { bestSm = sm; bestRaw = kv.Value; bestKey = kv.Key; }
            }
            int ph = bestKey / 1000, pa = bestKey % 1000;
            // representative game: exact modal score, closest to each regular's most common line
            var regs = ps.Where(p => p.PPlays * p.MeanMin >= 10).Select(p => new { P = p, Pts = ModeIdx(p.Pts.Hist), Reb = ModeIdx(p.Reb.Hist), Ast = ModeIdx(p.Ast.Hist), Tpm = ModeIdx(p.Tpm.Hist) }).ToList();
            int rep = -1; double repD = double.MaxValue;
            for (int s = 0; s < N; s++)
            {
                if (r.HomePts[s] != ph || r.AwayPts[s] != pa) continue;
                double dist = 0;
                foreach (var x in regs)
                {
                    int i = x.P.Gi;
                    if (r.Played != null && !r.Played[i][s]) { dist += 10; continue; }
                    dist += Math.Abs(r.Pts[i][s] - x.Pts) + 0.8 * Math.Abs(r.Reb[i][s] - x.Reb) + 0.8 * Math.Abs(r.Ast[i][s] - x.Ast) + 0.5 * Math.Abs(r.Tpm[i][s] - x.Tpm);
                }
                if (dist < repD) { repD = dist; rep = s; }
            }
            object repJson = null;
            if (rep >= 0)
            {
                Func<bool, object> side = home => ps.Where(p => p.Home == home && p.PPlays * p.MeanMin >= 3).OrderByDescending(p => p.PPlays * p.MeanMin)
                    .Select(p => { int i = p.Gi; bool pl = r.Played == null || r.Played[i][rep]; return (object)O("name", p.P.Name, "played", pl, "pts", (int)r.Pts[i][rep], "reb", (int)r.Reb[i][rep], "ast", (int)r.Ast[i][rep], "tpm", (int)r.Tpm[i][rep]); })
                    .Where(o => true).ToList();
                int hq = r.HomeQ123 != null ? r.HomeQ123[rep] : 0, aq = r.AwayQ123 != null ? r.AwayQ123[rep] : 0;
                repJson = O("count", bestRaw, "share", R3((double)bestRaw / N), "ot", r.OT != null ? (int)r.OT[rep] : 0, "homeQ123", hq, "awayQ123", aq, "home", side(true), "away", side(false));
            }
            int mm = SmoothIdx(hM), mt = SmoothIdx(hT), mh = SmoothIdx(hH), ma = SmoothIdx(hA);
            return O("home", mh, "homeShare", R3((double)hH[mh] / N), "away", ma, "awayShare", R3((double)hA[ma] / N),
                "pairHome", ph, "pairAway", pa, "pairShare", R3((double)bestRaw / N), "pairNear", R3((double)bestSm / N),
                "margin", mm - 200, "marginShare", R3((double)hM[mm] / N), "total", mt, "totalShare", R3((double)hT[mt] / N),
                "teamHome", O("reb", ModeIdx(team[0]), "ast", ModeIdx(team[1]), "tpm", ModeIdx(team[2])), "teamAway", O("reb", ModeIdx(team[3]), "ast", ModeIdx(team[4]), "tpm", ModeIdx(team[5])),
                "rep", repJson);
        }

        static object ComboJson(Combo c)
        {
            return O("legs", c.Legs.Select(l => l.Desc).ToList(), "legP", c.Legs.Select(l => R3(l.P)).ToList(), "joint", R3(c.Joint), "lift", Math.Round(c.Lift, 2), "fair", Math.Round(U.FairAmerican(c.Joint)));
        }

        public static string ExportSite(string path, DateTime d)
        {
            var S = Program.S;
            var rot = Rotations().ToDictionary(x => x.Ps, x => x);
            var games = new List<object>();
            foreach (var g in Program.Games)
            {
                var r = g.Res; double n = r.N; var dk = g.Def.DK;
                Func<bool, object> box = home =>
                {
                    var ps = Analysis.Players.Where(p => p.G == g && p.Home == home && p.PPlays * p.MeanMin >= 3).OrderByDescending(p => p.PPlays * p.MeanMin).ToList();
                    int k = 0;
                    return ps.Select(p =>
                    {
                        k++; var f = FormOf(p); RotRow rr; rot.TryGetValue(p, out rr); var bp = Bank.Player(p.P.Pid);
                        return (object)O("name", p.P.Name, "starter", k <= 5, "status", p.P.Status, "play", R3(p.PPlays), "min", R1(p.MeanMin),
                            "pts", R1(p.Pts.Mean), "reb", R1(p.Reb.Mean), "ast", R1(p.Ast.Mean), "tpm", R1(p.Tpm.Mean), "ptsLo", p.Pts.P10, "ptsHi", p.Pts.P90,
                            // most common value of each stat (when he plays) and how often it came up
                            "mPts", ModeIdx(p.Pts.Hist), "mReb", ModeIdx(p.Reb.Hist), "mAst", ModeIdx(p.Ast.Hist), "mTpm", ModeIdx(p.Tpm.Hist), "mPra", ModeIdx(p.Pra.Hist),
                            "sPts", R3((double)p.Pts.Hist[ModeIdx(p.Pts.Hist)] / p.Pts.N), "sReb", R3((double)p.Reb.Hist[ModeIdx(p.Reb.Hist)] / p.Reb.N),
                            "sAst", R3((double)p.Ast.Hist[ModeIdx(p.Ast.Hist)] / p.Ast.N), "sTpm", R3((double)p.Tpm.Hist[ModeIdx(p.Tpm.Hist)] / p.Tpm.N), "med", p.Pts.P50,
                            "p10", R3(p.Pts.PAtLeast(10)), "p20", R3(p.Pts.PAtLeast(20)), "p25", R3(p.Pts.PAtLeast(25)), "dd", R3(p.DD), "p3", R3(p.Tpm.PAtLeast(3)),
                            "season", f == null ? null : O("gp", f.Gp, "prior", f.FromPrior, "min", R1(f.Mpg), "pts", R1(f.Ppg), "reb", R1(f.Rpg), "ast", R1(f.Apg), "tpm", R1(f.Tpm)),
                            "bias", bp != null && bp.ModelN > 0 ? (object)R1(bp.ModelPtsBias) : null, "biasN", bp != null ? bp.ModelN : 0,
                            "flags", rr != null ? rr.Flags : new List<string>(), "up", rr != null ? rr.Up : 0);
                    }).ToList();
                };
                Func<string, object> outs = t => Program.TeamPlayers[t].Where(x => x.Status == "OUT" && Math.Max(x.CsMpg, x.PsMpg) >= 10).Select(x => x.Name).ToList();
                Func<string, object> qs = t => Program.TeamPlayers[t].Where(x => x.Status == "Q" && Math.Max(x.CsMpg, x.PsMpg) >= 8).Select(x => x.Name + " (" + U.Pct(x.PPlay) + ")").ToList();
                var tqH = Analysis.TeamQs.First(t => t.G == g && t.Home); var tqA = Analysis.TeamQs.First(t => t.G == g && !t.Home);
                double nv = Program.NoVigHome(dk);
                games.Add(O("id", g.Def.EspnId, "away", g.Def.Away, "home", g.Def.Home, "tip", g.Def.Tip ?? "",
                    "awayName", g.Info != null ? g.Info.AwayName : g.Def.Away, "homeName", g.Info != null ? g.Info.HomeName : g.Def.Home,
                    "dk", O("spreadHome", dk.SprH, "total", dk.Total, "mlHome", dk.MlH, "mlAway", dk.MlA),
                    "winHome", R3(g.WinH), "modelWinHome", R3(g.ModelWinH), "marketWinHome", R3(nv),
                    "ptsHome", R1(r.SumPtsH / n), "ptsAway", R1(r.SumPtsA / n), "total", R1(g.MeanTotal), "totalLo", g.TotalPct[0], "totalHi", g.TotalPct[4],
                    "pOver", R3(g.POver), "pCoverHome", R3(g.PCoverH), "pBlowout", R3(g.PBlowout), "pClose", R3(g.PClose), "pOT", R3(g.POT),
                    "evHome", dk.HasMl ? R3(U.EvPerDollar(g.WinH, dk.MlH)) : 0, "evAway", dk.HasMl ? R3(U.EvPerDollar(1 - g.WinH, dk.MlA)) : 0,
                    "b2bHome", g.Def.HomeB2B, "b2bAway", g.Def.AwayB2B,
                    "q3", O("home", O("mean", R1(tqH.Mean), "p100", R3(tqH.P100)), "away", O("mean", R1(tqA.Mean), "p100", R3(tqA.P100))),
                    "boxAway", box(false), "boxHome", box(true),
                    "mode", ModeJson(g, Analysis.Players.Where(p => p.G == g).ToList()),
                    "teamAway", O("pts", R1(r.SumPtsA / n), "ast", R1(r.SumAstA / n)), "teamHome", O("pts", R1(r.SumPtsH / n), "ast", R1(r.SumAstH / n)),
                    "outAway", outs(g.Def.Away), "outHome", outs(g.Def.Home), "qAway", qs(g.Def.Away), "qHome", qs(g.Def.Home),
                    "boost", Analysis.BoostSgp[g].Take(3).Select(ComboJson).ToList(),
                    "four", O("safest", Analysis.Best4(g, 0.30, 1.0, 2).Select(ComboJson).ToList(), "boost", Analysis.Best4(g, 0.19, 0.26, 2).Select(ComboJson).ToList(), "bigger", Analysis.Best4(g, 0.09, 0.14, 2).Select(ComboJson).ToList())));
            }
            var best = Notes.BestSgp();

            // erupt / dud lists against this season's scoring
            Func<PStat, bool, object> ed = (p, erupt) =>
            {
                RotRow rr; rot.TryGetValue(p, out rr); var fm = FormOf(p);
                return O("name", p.P.Name, "team", p.P.Team, "opp", p.Opp, "season", R1(p.SeasonPpg), "proj", R1(p.Pts.Mean), "mode", ModeIdx(p.Pts.Hist),
                    "line", erupt ? p.EruptLine : p.DudLine, "p", R3(TrackAdjust.Adj(erupt ? "erupt" : "dud", erupt ? p.PErupt : p.PDud)), "check", BankCheck(p, erupt),
                    "seasonMin", fm == null ? 0 : R1(fm.Mpg), "min", R1(p.MeanMin), "flags", rr != null ? rr.Flags : new List<string>(), "bias", Bank.Player(p.P.Pid) != null && Bank.Player(p.P.Pid).ModelN > 0 ? (object)R1(Bank.Player(p.P.Pid).ModelPtsBias) : null, "biasN", Bank.Player(p.P.Pid) != null ? Bank.Player(p.P.Pid).ModelN : 0);
            };
            var eruptJson = Analysis.EruptList().Select(p => ed(p, true)).ToList();
            var dudJson = Analysis.DudList().Select(p => ed(p, false)).ToList();

            // season bank: tonight's teams and every flagged player on them
            var teams = Program.Games.SelectMany(g => new[] { g.Def.Away, g.Def.Home }).ToList();
            var bankTeams = teams.Select(Bank.Team).Where(t => t != null).Select(t => (object)O("team", t.Team, "gp", t.Gp, "w", t.W, "l", t.L, "ppg", R1(t.Ppg), "opp", R1(t.OppPpg), "margin", R1(t.Margin), "pace", R1(t.Pace), "ortg", R1(t.ORtg), "drtg", R1(t.DRtg),
                "atsW", t.AtsW, "atsL", t.AtsL, "overs", t.Overs, "unders", t.Unders, "blowouts", t.Blowouts, "bias", R1(t.ModelMarginBias), "totalBias", R1(t.ModelTotalBias), "flags", t.Flags)).ToList();
            var rosterIds = new HashSet<string>(teams.SelectMany(t => Program.TeamPlayers[t].Select(p => p.Pid)));
            var bankPlayers = Bank.Players.Values.Where(p => p.Gp > 0 && rosterIds.Contains(p.Pid) && p.Mpg >= 10).OrderBy(p => p.Team).ThenByDescending(p => p.Mpg)
                .Select(p => (object)O("name", p.Name, "team", p.Team, "gp", p.Gp, "gs", p.Gs, "min", R1(p.Mpg), "pts", R1(p.Ppg), "reb", R1(p.Rpg), "ast", R1(p.Apg), "tpm", R1(p.Tpm), "ts", R3(p.TsPct),
                    "l3", double.IsNaN(p.L3Pts) ? null : (object)R1(p.L3Pts), "minSlope", double.IsNaN(p.MinSlope) ? null : (object)Math.Round(p.MinSlope, 1),
                    "mins", p.Log.Select(x => Math.Round(x.B.Min)).ToList(), "ptsLog", p.Log.Select(x => x.B.Pts).ToList(),
                    "bias", p.ModelN > 0 ? (object)R1(p.ModelPtsBias) : null, "biasN", p.ModelN, "flags", p.Flags)).ToList();

            // live track record from graded slates
            var live = Ledger.LoadAll();
            var tagRec = live.Where(p => !string.IsNullOrEmpty(p.Tag) && !p.Void && !double.IsNaN(p.Y)).GroupBy(p => p.Tag).OrderBy(gr => gr.Key)
                .Select(gr => (object)O("tag", gr.Key, "n", gr.Count(), "hits", (int)gr.Sum(x => x.Y), "pred", R3(gr.Average(x => x.P)))).ToList();
            var mlLive = live.Where(p => p.Kind == "ML" && !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.PMarket)).ToList();
            var slates = live.Select(p => p.Date).Distinct().OrderBy(x => x).Select(dt =>
            {
                var l = live.Where(p => p.Date == dt).ToList();
                var ml = l.Where(p => p.Kind == "ML" && !p.Void && !double.IsNaN(p.Y)).GroupBy(p => p.GameId).Select(gr => gr.OrderByDescending(x => x.P).First()).ToList();
                var pl = l.Where(p => p.Kind == "PLAYER" && !p.Void && !double.IsNaN(p.Y) && PropStats.Contains(p.Stat)).ToList();
                var gamesRows = ml.Select(m =>
                {
                    var mm = l.FirstOrDefault(p => p.Kind == "MEAN" && p.Stat == "MARGIN" && p.GameId == m.GameId); var mt = l.FirstOrDefault(p => p.Kind == "MEAN" && p.Stat == "TOTAL" && p.GameId == m.GameId);
                    var to = l.FirstOrDefault(p => p.Kind == "TOTAL" && p.Stat == "OVER" && p.GameId == m.GameId);
                    return (object)O("game", m.Game, "fav", m.Team, "p", R3(m.P), "won", m.Y >= 1, "marginModel", mm != null ? R1(mm.Mean) : 0, "marginActual", mm != null ? R1(mm.Actual) : 0, "home", mm != null ? mm.Team : "",
                        "totalModel", mt != null ? R1(mt.Mean) : 0, "totalDk", to != null ? to.Line : 0, "totalActual", mt != null ? R1(mt.Actual) : 0);
                }).ToList();
                return (object)O("date", dt, "games", ml.Count, "favWon", ml.Count(x => x.Y >= 1), "props", pl.Count, "propPred", pl.Count > 0 ? R3(pl.Average(x => x.P)) : 0, "propObs", pl.Count > 0 ? R3(pl.Average(x => x.Y)) : 0, "rows", gamesRows);
            }).ToList();

            // audit on the walk-forward backtest
            object audit = null, tiers = null;
            string btPath = Program.P(@"ledger\backtest_latest.csv");
            if (File.Exists(btPath))
            {
                var bt = MakeSrc("Backtest", Pred.Load(btPath), Bank.Games);
                var props = PropRows(bt);
                var rel = Grader.Reliability(props).Where(b => b[2] > 0).Select(b => (object)O("lo", b[0], "hi", b[1], "n", (int)b[2], "pred", R3(b[3]), "obs", R3(b[4]))).ToList();
                var segs = PropSegments(bt).Select(s => (object)O("group", s.Group, "name", s.Name, "n", s.N, "pred", R3(s.MeanP), "obs", R3(s.Obs), "skill", R3(s.Skill))).ToList();
                var conf = props.Where(p => p.P >= 0.8).ToList();
                var causes = new[] { "minutes short", "minutes surplus", "blowout", "normal minutes" }.Select(c => (object)O("cause", c, "miss", conf.Count(p => p.Y < 1 && Cause(CtxOf(bt, p)) == c), "hit", conf.Count(p => p.Y >= 1 && Cause(CtxOf(bt, p)) == c))).ToList();
                var sgp = bt.Preds.Where(p => p.Kind == "SGP" && Gradable(p) && !double.IsNaN(p.Mean)).GroupBy(p => p.Tag).Select(gr => (object)O("tag", gr.Key, "n", gr.Count(), "joint", R3(gr.Average(x => x.P)), "indep", R3(gr.Average(x => x.Mean)), "obs", R3(gr.Average(x => x.Y)))).ToList();
                var ml = bt.Preds.Where(p => p.Kind == "ML" && Gradable(p) && !double.IsNaN(p.PMarket)).ToList();
                var gi = bt.G.Values.Where(x => !double.IsNaN(x.Margin) && !double.IsNaN(x.SprH) && !double.IsNaN(x.MktTotal)).ToList();
                var means = bt.C.Values.Where(c => !double.IsNaN(c.ProjPts) && !double.IsNaN(c.ActPts)).ToList();
                audit = O("games", bt.G.Count(x => !double.IsNaN(x.Value.Margin)), "dates", bt.Preds.Select(p => p.Date).Distinct().Count(), "props", props.Count,
                    "propPred", R3(props.Average(p => p.P)), "propObs", R3(props.Average(p => p.Y)), "confN", conf.Count, "confHit", R3(conf.Average(p => p.Y)),
                    "mlBrier", R3(ml.Average(p => (p.P - p.Y) * (p.P - p.Y))), "mlBrierMkt", R3(ml.Average(p => (p.PMarket - p.Y) * (p.PMarket - p.Y))),
                    "marginMae", R1(gi.Average(x => Math.Abs(x.Margin - x.ModelMargin))), "marginMaeMkt", R1(gi.Average(x => Math.Abs(x.Margin + x.SprH))),
                    "totalMae", R1(gi.Average(x => Math.Abs(x.Total - x.ModelTotal))), "totalMaeMkt", R1(gi.Average(x => Math.Abs(x.Total - x.MktTotal))),
                    "blowoutShare", R3(gi.Count(x => Math.Abs(x.Margin) >= 20) / (double)Math.Max(1, gi.Count)),
                    "ptsMae", R1(means.Average(c => Math.Abs(c.ProjPts - c.ActPts))), "minMae", R1(means.Average(c => Math.Abs(c.ProjMin - c.ActMin))),
                    "reliability", rel, "segments", segs, "causes", causes, "sgp", sgp);
                // upset risk: how often the model's favorite actually won, by how strongly it was favored (every backtest game, out of sample)
                var favs = bt.Preds.Where(p => p.Kind == "ML" && Gradable(p)).GroupBy(p => p.Date + "|" + p.GameId).Select(gr => gr.OrderByDescending(x => x.P).First()).ToList();
                Func<double, double, object> band = (lo, hi) =>
                {
                    var b = favs.Where(x => x.P >= lo && x.P < hi).ToList();
                    return O("lo", lo, "hi", hi, "n", b.Count, "won", (int)b.Sum(x => x.Y), "exp", R1(b.Sum(x => x.P)));
                };
                tiers = O("games", favs.Count, "bands", new List<object> { band(0.75, 1.01), band(0.65, 0.75), band(0.55, 0.65), band(0, 0.55) }, "parlay", band(0.70, 1.01));
            }

            var data = O(
                "meta", O("slate", d.ToString("yyyy-MM-dd"), "slateLabel", d.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture), "built", DateTime.Now.ToString("ddd MMM d, h:mm tt", CultureInfo.InvariantCulture),
                    "sims", Program.NSIM, "bankThrough", Bank.BuiltThrough, "bankGames", Bank.Games.Count, "bankPlayers", Bank.Players.Values.Count(p => p.Gp > 0),
                    "recal", S.LastRecalibration, "settings", O("marketWeight", S.MarketWeight, "alpha", S.Alpha, "sigmaEff", S.SigmaEff, "sigmaPace", S.SigmaPace, "sigmaMin", S.SigmaMin,
                        "csWeight", S.CsWeight, "minUsageSlope", S.MinUsageSlope, "matchup", S.MatchupStrength, "gamma", Program.Gamma, "hca", R1(Program.HCA))),
                "games", games,
                "bestSgp", best == null ? null : O("game", Notes.GameOf(best), "sgp", ComboJson(best)),
                "erupt", eruptJson, "dud", dudJson,
                "bank", O("teams", bankTeams, "players", bankPlayers),
                "record", O("tags", tagRec, "slates", slates, "mlBrier", mlLive.Count > 0 ? (object)R3(mlLive.Average(p => (p.P - p.Y) * (p.P - p.Y))) : null, "mlBrierMkt", mlLive.Count > 0 ? (object)R3(mlLive.Average(p => (p.PMarket - p.Y) * (p.PMarket - p.Y))) : null),
                "audit", audit, "tiers", tiers);
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "window.NBA = " + ser.Serialize(data) + ";\n", new UTF8Encoding(false));
            return path;
        }
    }
}
