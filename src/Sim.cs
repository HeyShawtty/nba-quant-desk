using System;
using System.Collections.Generic;
using System.Linq;

namespace NbaQuant
{
    class Scenario { public double Prob; public double[] Min; public bool[] Starter; public string Label; }

    class SimTeam
    {
        public string Abbr;
        public PlayerP[] Pl;
        public Scenario[] Scen; public double[] ScenCum;
        public double Eff = 1.0, Kappa = 1.0, POreb = 0.25, TargetPts, TargetAst;
    }

    class SimParams
    {
        public double SigmaPace = 2.5, SigmaEff = 0.03, Alpha = 0.3, SigmaMin = 0.12, SigmaPlayer = 0.0;
        public double Sigma3 = 0.0;                 // extra per-player 3-point shooting-night SD (3P% only)
        public double BlowStart = 12, BlowSpan = 14; // Q4 starter benching: starts at this margin, full after BlowSpan more points
        public double[] QShare = { 0.2525, 0.25, 0.25, 0.2475 };
        public double PossPerTeam = 99;
        public double PTipHome = 0.5;
        public double AndOne2 = 0.07, AndOne3 = 0.012, AstBase2 = 0.52, AstBase3 = 0.82, TeamRebShare = 0.12, FtOreb = 0.13;
        public SimParams Clone() { var c = (SimParams)MemberwiseClone(); c.QShare = (double[])QShare.Clone(); return c; }
    }

    // In-game starting point: simulate only what is left of the game on top of what has already happened.
    class LiveState
    {
        public int Q;            // 0-based current period (4+ = overtime)
        public double FracLeft;  // share of the current period still to play
        public int ScoreH, ScoreA;
        public int[] Pts, Reb, Ast, Tpm; // per global player index (home first)
    }

    class SimResult
    {
        public int N, NH, NA;
        public short[] HomePts, AwayPts, HomeQ123, AwayQ123;
        public byte[] OT;
        public byte[][] Pts, Reb, Ast, Tpm; // [player][sim]
        public bool[][] Q3All;            // 3+ points in each of the 4 regulation quarters
        public bool[][] Played;           // player logged minutes in this simulated game
        public double[] MinSum; public int[] PlayedCount;
        public short[] FirstFG;           // global player index (home first), -1 none
        public byte[] TipHome;
        public double SumPtsH, SumPtsA, SumAstH, SumAstA, SumPoss;
    }

    class GameSim
    {
        public SimTeam H, A; public SimParams P;
        static readonly double[] TStarter = { 0.285, 0.215, 0.285, 0.215 };
        static readonly double[] TBench = { 0.17, 0.33, 0.17, 0.33 };

        public GameSim(SimTeam h, SimTeam a, SimParams p) { H = h; A = a; P = p; }

        // per-sim working state
        Rng rng;
        int nh, na;
        double[][] minNow = new double[2][];
        bool[][] starterNow = new bool[2][];
        double[][][] minQ = new double[2][][];          // [team][q 0..4][player]
        double[][] cumU = new double[2][], wAst = new double[2][], cumO = new double[2][], cumD = new double[2][];
        double[] totU = new double[2], totO = new double[2], totD = new double[2], totAst = new double[2];
        double[] effNow = new double[2];
        double[][] effPl = new double[2][];   // per-player shooting night (hot/cold), drawn once per simulated game
        double[][] eff3 = new double[2][];    // per-player 3-point night on top of effPl
        int[] score = new int[2];
        int[] pts, reb, ast, tpm; int[,] qp;
        int firstFG; double possCount;
        int teamAst0, teamAst1;

        SimTeam T(int t) { return t == 0 ? H : A; }
        int Off(int t) { return t == 0 ? 0 : nh; }

        public SimResult Run(int n, ulong seed, bool record, LiveState live = null)
        {
            rng = new Rng(seed);
            nh = H.Pl.Length; na = A.Pl.Length;
            int np = nh + na;
            var res = new SimResult { N = n, NH = nh, NA = na, MinSum = new double[np], PlayedCount = new int[np] };
            if (record)
            {
                res.HomePts = new short[n]; res.AwayPts = new short[n]; res.HomeQ123 = new short[n]; res.AwayQ123 = new short[n];
                res.OT = new byte[n]; res.FirstFG = new short[n]; res.TipHome = new byte[n];
                res.Pts = new byte[np][]; res.Reb = new byte[np][]; res.Ast = new byte[np][]; res.Tpm = new byte[np][]; res.Q3All = new bool[np][]; res.Played = new bool[np][];
                for (int i = 0; i < np; i++) { res.Pts[i] = new byte[n]; res.Reb[i] = new byte[n]; res.Ast[i] = new byte[n]; res.Tpm[i] = new byte[n]; res.Q3All[i] = new bool[n]; res.Played[i] = new bool[n]; }
            }
            for (int t = 0; t < 2; t++)
            {
                int m = t == 0 ? nh : na;
                minNow[t] = new double[m]; starterNow[t] = new bool[m]; effPl[t] = new double[m]; eff3[t] = new double[m];
                minQ[t] = new double[5][]; for (int q = 0; q < 5; q++) minQ[t][q] = new double[m];
                cumU[t] = new double[m]; wAst[t] = new double[m]; cumO[t] = new double[m]; cumD[t] = new double[m];
            }
            pts = new int[np]; reb = new int[np]; ast = new int[np]; tpm = new int[np]; qp = new int[np, 4];

            for (int s = 0; s < n; s++)
            {
                Array.Clear(pts, 0, np); Array.Clear(reb, 0, np); Array.Clear(ast, 0, np); Array.Clear(tpm, 0, np); Array.Clear(qp, 0, qp.Length);
                score[0] = 0; score[1] = 0; firstFG = -1; teamAst0 = 0; teamAst1 = 0;
                if (live != null)
                {
                    score[0] = live.ScoreH; score[1] = live.ScoreA; firstFG = -2;
                    for (int i = 0; i < np; i++) { pts[i] = live.Pts[i]; reb[i] = live.Reb[i]; ast[i] = live.Ast[i]; tpm[i] = live.Tpm[i]; }
                }
                SetupMinutes(0); SetupMinutes(1);
                for (int t = 0; t < 2; t++) for (int q = 0; q < 3; q++) BuildQuarter(t, q, 0, false);
                double pace = P.PossPerTeam + P.SigmaPace * rng.Normal();
                effNow[0] = H.Eff * Math.Exp(P.SigmaEff * rng.Normal());
                effNow[1] = A.Eff * Math.Exp(P.SigmaEff * rng.Normal());
                for (int t = 0; t < 2; t++)
                    for (int i = 0; i < effPl[t].Length; i++)
                    {
                        effPl[t][i] = P.SigmaPlayer > 0 ? Math.Exp(P.SigmaPlayer * rng.Normal() - 0.5 * P.SigmaPlayer * P.SigmaPlayer) : 1.0;
                        eff3[t][i] = P.Sigma3 > 0 ? Math.Exp(P.Sigma3 * rng.Normal() - 0.5 * P.Sigma3 * P.Sigma3) : 1.0;
                    }
                bool tipHome = rng.U01() < P.PTipHome;
                int q0 = live != null ? Math.Min(4, live.Q) : 0;
                for (int q = q0; q < 4; q++)
                {
                    if (q == 3)
                    {
                        int margin = Math.Abs(score[0] - score[1]);
                        double b = Math.Max(0, Math.Min(1, (margin - P.BlowStart) / P.BlowSpan));
                        BuildQuarter(0, 3, b, margin <= 6); BuildQuarter(1, 3, b, margin <= 6);
                    }
                    Weights(0, q); Weights(1, q);
                    bool partial = live != null && q == q0;
                    double share = P.QShare[q] * (partial ? live.FracLeft : 1.0);
                    int nPoss = Math.Max(partial ? 0 : 14, (int)Math.Round(pace * share + (partial ? live.FracLeft : 1.0) * rng.Normal()));
                    bool homeFirst = partial ? rng.U01() < 0.5 : (q == 0 || q == 3) ? tipHome : !tipHome;
                    int total = 2 * nPoss + (rng.U01() < 0.5 ? 1 : 0);
                    for (int k = 0; k < total; k++)
                    {
                        bool homeOff = (k % 2 == 0) == homeFirst;
                        Possession(homeOff ? 0 : 1, q);
                    }
                }
                int ot = 0;
                while (score[0] == score[1] && ot < 6)
                {
                    ot++;
                    BuildOT(0); BuildOT(1); Weights(0, 4); Weights(1, 4);
                    int nPoss = Math.Max(5, (int)Math.Round(pace * 5.0 / 48.0 + 0.8 * rng.Normal()));
                    bool homeFirst = rng.U01() < 0.5;
                    int total = 2 * nPoss + (rng.U01() < 0.5 ? 1 : 0);
                    for (int k = 0; k < total; k++) { bool homeOff = (k % 2 == 0) == homeFirst; Possession(homeOff ? 0 : 1, 4); }
                }
                if (score[0] == score[1]) score[rng.U01() < 0.5 ? 0 : 1] += 1; // practically unreachable safety

                res.SumPtsH += score[0]; res.SumPtsA += score[1]; res.SumAstH += teamAst0; res.SumAstA += teamAst1;
                for (int t = 0; t < 2; t++)
                {
                    int o = Off(t);
                    for (int i = 0; i < minNow[t].Length; i++)
                    {
                        double played = minQ[t][0][i] + minQ[t][1][i] + minQ[t][2][i] + minQ[t][3][i];
                        if (ot > 0) played += ot * minQ[t][4][i] * 5.0 / 12.0;
                        res.MinSum[o + i] += played;
                        if (played > 0.05) { res.PlayedCount[o + i]++; if (record) res.Played[o + i][s] = true; }
                    }
                }
                if (record)
                {
                    res.HomePts[s] = (short)score[0]; res.AwayPts[s] = (short)score[1]; res.OT[s] = (byte)ot;
                    res.FirstFG[s] = (short)firstFG; res.TipHome[s] = (byte)(tipHome ? 1 : 0);
                    int hq = 0, aq = 0;
                    for (int i = 0; i < nh; i++) hq += qp[i, 0] + qp[i, 1] + qp[i, 2];
                    for (int i = nh; i < np; i++) aq += qp[i, 0] + qp[i, 1] + qp[i, 2];
                    res.HomeQ123[s] = (short)hq; res.AwayQ123[s] = (short)aq;
                    for (int i = 0; i < np; i++)
                    {
                        res.Pts[i][s] = (byte)Math.Min(255, pts[i]); res.Reb[i][s] = (byte)Math.Min(255, reb[i]);
                        res.Ast[i][s] = (byte)Math.Min(255, ast[i]); res.Tpm[i][s] = (byte)Math.Min(255, tpm[i]);
                        res.Q3All[i][s] = qp[i, 0] >= 3 && qp[i, 1] >= 3 && qp[i, 2] >= 3 && qp[i, 3] >= 3;
                    }
                }
            }
            res.SumPoss = possCount;
            return res;
        }

        void SetupMinutes(int t)
        {
            var tm = T(t);
            double r = rng.U01();
            int sc = 0;
            while (sc < tm.ScenCum.Length - 1 && r > tm.ScenCum[sc]) sc++;
            var scen = tm.Scen[sc];
            double sum = 0;
            for (int i = 0; i < tm.Pl.Length; i++)
            {
                double b = scen.Min[i];
                // rotation risk is a yes/no draw (books void props for players who sit), not a minutes haircut
                double pr = tm.Pl[i].PRot;
                if (b > 0 && pr < 0.95) b = rng.U01() < pr ? b / Math.Max(0.05, pr) : 0;
                double m = b <= 0 ? 0 : b * Math.Exp(P.SigmaMin * rng.Normal() - 0.5 * P.SigmaMin * P.SigmaMin);
                minNow[t][i] = m; sum += m; starterNow[t][i] = scen.Starter[i];
            }
            double f = 240.0 / sum;
            for (int i = 0; i < tm.Pl.Length; i++) minNow[t][i] = Math.Min(46, minNow[t][i] * f);
        }

        void BuildQuarter(int t, int q, double blowout, bool close)
        {
            var mq = minQ[t][q];
            int m = mq.Length;
            for (int i = 0; i < m; i++) mq[i] = minNow[t][i] * (starterNow[t][i] ? TStarter[q] : TBench[q]);
            if (q == 3 && blowout > 0)
            {
                double freed = 0, wsum = 0;
                for (int i = 0; i < m; i++) if (starterNow[t][i]) { double cut = mq[i] * 0.8 * blowout; mq[i] -= cut; freed += cut; }
                for (int i = 0; i < m; i++) if (!starterNow[t][i]) wsum += minNow[t][i] + 3;
                if (wsum > 0) for (int i = 0; i < m; i++) if (!starterNow[t][i]) mq[i] += freed * (minNow[t][i] + 3) / wsum;
            }
            else if (q == 3 && close)
            {
                double add = 0;
                for (int i = 0; i < m; i++) if (starterNow[t][i]) { double a = mq[i] * 0.12; mq[i] += a; add += a; }
                double bsum = 0; for (int i = 0; i < m; i++) if (!starterNow[t][i]) bsum += mq[i];
                if (bsum > 0) for (int i = 0; i < m; i++) if (!starterNow[t][i]) mq[i] -= add * mq[i] / bsum;
            }
            for (int it = 0; it < 60; it++)   // until the quarter holds exactly 60 player-minutes (4 passes left it short when stars hit the 12-minute cap)
            {
                double s = 0; for (int i = 0; i < m; i++) s += mq[i];
                if (s <= 0 || Math.Abs(s - 60) < 1e-6) break;
                double f = 60.0 / s;
                for (int i = 0; i < m; i++) mq[i] = Math.Min(12, Math.Max(0, mq[i] * f));
            }
        }

        void BuildOT(int t)
        {
            var mq = minQ[t][4]; int m = mq.Length;
            for (int i = 0; i < m; i++) mq[i] = minNow[t][i] * minNow[t][i];
            for (int it = 0; it < 60; it++)
            {
                double s = 0; for (int i = 0; i < m; i++) s += mq[i];
                if (s <= 0 || Math.Abs(s - 60) < 1e-6) break;
                double f = 60.0 / s;
                for (int i = 0; i < m; i++) mq[i] = Math.Min(12, mq[i] * f);
            }
        }

        void Weights(int t, int q)
        {
            var tm = T(t); var mq = minQ[t][q]; int m = mq.Length;
            double cu = 0, co = 0, cd = 0, ca = 0;
            for (int i = 0; i < m; i++)
            {
                double w = mq[i] / 12.0;
                var p = tm.Pl[i];
                cu += w * p.PickW; cumU[t][i] = cu;
                co += w * p.ROreb; cumO[t][i] = co;
                cd += w * p.RDreb; cumD[t][i] = cd;
                wAst[t][i] = w * p.RAst; ca += wAst[t][i];
            }
            totU[t] = cu; totO[t] = co; totD[t] = cd; totAst[t] = ca;
        }

        int Pick(double[] cum, double tot)
        {
            double r = rng.U01() * tot;
            for (int i = 0; i < cum.Length; i++) if (r < cum[i]) return i;
            return cum.Length - 1;
        }

        void Credit(int t, int i, int v, int q)
        {
            int g = Off(t) + i;
            pts[g] += v; score[t] += v;
            if (q < 4) qp[g, q] += v;
        }

        void Assist(int t, int shooter, double prob)
        {
            if (rng.U01() >= prob) return;
            double tot = totAst[t] - wAst[t][shooter];
            if (tot <= 0) return;
            double r = rng.U01() * tot, c = 0;
            var w = wAst[t];
            for (int j = 0; j < w.Length; j++)
            {
                if (j == shooter) continue;
                c += w[j];
                if (r < c) { ast[Off(t) + j]++; if (t == 0) teamAst0++; else teamAst1++; return; }
            }
        }

        // returns true if the offense keeps the ball (offensive rebound)
        bool Rebound(int o, int d, double pOff)
        {
            if (rng.U01() < pOff)
            {
                if (rng.U01() >= P.TeamRebShare) reb[Off(o) + Pick(cumO[o], totO[o])]++;
                return true;
            }
            if (rng.U01() >= P.TeamRebShare) reb[Off(d) + Pick(cumD[d], totD[d])]++;
            return false;
        }

        void Possession(int o, int q)
        {
            int d = 1 - o;
            var tm = T(o);
            possCount += 0.5;
            for (int play = 0; play < 8; play++)
            {
                int i = Pick(cumU[o], totU[o]);
                var p = tm.Pl[i];
                double r = rng.U01() * p.Usage;
                int lead = score[o] - score[d];
                if (lead > 30) lead = 30; if (lead < -30) lead = -30;
                double sef = 1.0 - P.Alpha * lead / 100.0;
                if (r < p.R2a + p.R3a)
                {
                    bool three = r >= p.R2a;
                    double pm = Math.Min(0.93, (three ? p.P3 * eff3[o][i] : p.P2) * effNow[o] * effPl[o][i] * sef);
                    if (rng.U01() < pm)
                    {
                        Credit(o, i, three ? 3 : 2, q);
                        if (three) tpm[Off(o) + i]++;
                        if (firstFG < 0) firstFG = Off(o) + i;
                        Assist(o, i, Math.Min(0.97, (three ? P.AstBase3 : P.AstBase2) * tm.Kappa));
                        if (rng.U01() < (three ? P.AndOne3 : P.AndOne2) && rng.U01() < p.Pft) Credit(o, i, 1, q);
                        return;
                    }
                    if (Rebound(o, d, tm.POreb)) continue;
                    return;
                }
                if (r < p.R2a + p.R3a + p.RTrip)
                {
                    if (rng.U01() < p.Pft) Credit(o, i, 1, q);
                    if (rng.U01() < p.Pft) { Credit(o, i, 1, q); return; }
                    if (Rebound(o, d, P.FtOreb)) continue;
                    return;
                }
                return; // turnover
            }
        }
    }
}
