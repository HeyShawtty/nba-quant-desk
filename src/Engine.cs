using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NbaQuant
{
    class GameCtx
    {
        public GameDef Def;
        public SimTeam H, A;
        public SimParams P;
        public GameData Info;                  // ESPN summary for this game (injuries, pregame line, or the final box in backtests)
        public bool HasMarket; public string MarketSource = "";
        public List<PropLine> Props = new List<PropLine>();
        public double ModelPtsH, ModelPtsA, ModelPoss, MktPtsH, MktPtsA, BlendPtsH, BlendPtsA, BlendPoss;
        public double OffH, DefH, OffA, DefA, PaceH, PaceA;
        public SimResult Res;
        public double ModelWinH, ModelMeanMargin, ModelMeanTotal, FormShift;
        public double WinH, MeanMargin, MeanTotal, SdMargin, SdTotal, PCoverH, POver, POT, PBlowout, PClose;
        public int[] TotalPct = new int[5];
        public List<PlayerP> Players = new List<PlayerP>();
    }

    static partial class Program
    {
        public static Settings S;
        public static string Root;
        public static Action<string> Log = s => Console.WriteLine(s);
        public static DateTime SlateDate; public static bool LiveMode;
        public static int NSIM = 100000;
        public static double WMKT = 0.75;
        public static double HCA = 1.72, B2BNet = 1.8, LgPace, LgORtg, LgOrPct, LgOrebRate, LgDrebRate;
        public static Dictionary<string, PlayerP> AllPlayers = new Dictionary<string, PlayerP>();
        public static Dictionary<string, List<PlayerP>> TeamPlayers = new Dictionary<string, List<PlayerP>>();
        public static List<GameCtx> Games = new List<GameCtx>();
        public static List<GameData> Completed = new List<GameData>();
        public static Dictionary<string, double> HistStats = new Dictionary<string, double>();
        public static double CalAlpha, CalSigmaEff, CalSigmaPace, CalMarginSd, CalTotalSd;
        public static List<string> CalGrid = new List<string>();
        public static double EmpTipTeamFirstFG; public static int EmpTipN;
        public static double[] QShareEmp = { 0.25, 0.25, 0.25, 0.25 };
        public static double Gamma = 1.1, GammaR2, GammaR2Prop; public static int GammaTeamGames;
        public static Dictionary<string, double> Pace26 = new Dictionary<string, double>();
        public static Dictionary<string, List<double>> CsPace = new Dictionary<string, List<double>>();
        public static List<string[]> Verification = new List<string[]>();  // item, status, detail
        public static List<string> Warnings = new List<string>();
        public static DateTime RunTime;
        public static List<string> SkippedStarted = new List<string>();
        public static List<string> SkippedBanned = new List<string>();   // games left out because a team is banned (Ban.cs)
        public static HashSet<string> BannedIds = new HashSet<string>();  // ESPN ids of the date's games that involve a banned team
        public static string ForceIncludeId;                      // in-game mode: price this game even though it has started
        public static HashSet<string> ForceOut = new HashSet<string>(); // in-game mode: players listed inactive in the live box

        // In-game: calibrate on the pre-tip blend, then simulate only the rest of the game from the live score and box.
        public static GameData RunLive(GameCtx g, int nsim)
        {
            var seed = (ulong)DateTime.Now.Ticks;
            g.H.Eff = 1; g.A.Eff = 1; g.H.Kappa = 1; g.A.Kappa = 1;
            CalibrateEff(g, g.P, g.BlendPtsH, g.BlendPtsA, g.BlendPoss, 20000, 5, seed);
            var now = Espn.ParseSummary(Net.Get(Espn.Base + "summary?event=" + g.Def.EspnId));
            int np = g.Players.Count;
            var ls = new LiveState { Q = Math.Max(0, now.Period - 1), FracLeft = Math.Max(0, Math.Min(1, now.ClockSec / (now.Period > 4 ? 300.0 : 720.0))), ScoreH = now.HomePts, ScoreA = now.AwayPts, Pts = new int[np], Reb = new int[np], Ast = new int[np], Tpm = new int[np] };
            if (now.State == "pre") { ls.Q = 0; ls.FracLeft = 1; }
            for (int i = 0; i < np; i++)
            {
                var b = now.Box.FirstOrDefault(x => x.Pid == g.Players[i].Pid);
                if (b == null || !b.Played) continue;
                ls.Pts[i] = b.Pts; ls.Reb[i] = b.Reb; ls.Ast[i] = b.Ast; ls.Tpm[i] = b.Tpm;
                g.Players[i].PRot = 1; // already in the rotation tonight
            }
            NSIM = nsim;
            g.Res = new GameSim(g.H, g.A, g.P).Run(nsim, seed + 99, true, ls);
            Summarize(g);
            return now;
        }

        static Dictionary<string, List<BRow>> B26, B25, B27;
        static Dictionary<string, GroupPrior> Pri;
        static Dictionary<string, string> Idx26, Idx25, Idx27;

        public static readonly Dictionary<string, string> EspnToBref = new Dictionary<string, string> {
            {"ATL","ATL"},{"BOS","BOS"},{"BKN","BRK"},{"CHA","CHO"},{"CHI","CHI"},{"CLE","CLE"},{"DAL","DAL"},{"DEN","DEN"},{"DET","DET"},{"GS","GSW"},
            {"HOU","HOU"},{"IND","IND"},{"LAC","LAC"},{"LAL","LAL"},{"MEM","MEM"},{"MIA","MIA"},{"MIL","MIL"},{"MIN","MIN"},{"NO","NOP"},{"NY","NYK"},
            {"OKC","OKC"},{"ORL","ORL"},{"PHI","PHI"},{"PHX","PHO"},{"POR","POR"},{"SAC","SAC"},{"SA","SAS"},{"TOR","TOR"},{"UTAH","UTA"},{"WSH","WAS"} };

        public static string P(string rel) { return Path.Combine(Root, rel); }
        static void Say(string s) { Log(s); }

        // ------------------------------------------------------------------ init
        public static void Init(string root)
        {
            Root = root; Store.Root = root; Store.Log = s => Log(s); Net.Log = s => Log(s);
            S = Settings.Load(P("settings.ini"));
            B26 = Data.LoadBref(Store.BrefCsv(S.PriorSeason, "totals", false), Store.BrefCsv(S.PriorSeason, "advanced", false));
            B25 = Data.LoadBref(Store.BrefCsv(S.OlderSeason, "totals", false), Store.BrefCsv(S.OlderSeason, "advanced", false));
            Pri = Data.Priors(B26);
            Idx26 = Index(B26); Idx25 = Index(B25);
            double totMp = B26.Values.Sum(l => l[0].MP);
            LgOrebRate = B26.Values.Sum(l => l[0].ORB) / totMp; LgDrebRate = B26.Values.Sum(l => l[0].DRB) / totMp;
            LoadHistory();
        }

        static Dictionary<string, string> Index(Dictionary<string, List<BRow>> b)
        {
            var idx = new Dictionary<string, string>();
            foreach (var kv in b) { string n = U.Norm(kv.Value[0].Name); if (!idx.ContainsKey(n)) idx[n] = kv.Key; }
            return idx;
        }

        static void LoadHistory()
        {
            HistStats.Clear();
            string hp = P(@"data\static\hist_2526.csv");
            if (File.Exists(hp))
            {
                var marg = new List<double>(); var tot = new List<double>(); var homeMargins = new List<double>();
                double brier = 0, ll = 0; int nml = 0;
                foreach (var r in U.ReadCsv(hp).Skip(1))
                {
                    if (r[13] != "reg") continue;
                    double ap = U.D(r[3]), hpp = U.D(r[4]); if (ap == 0 && hpp == 0) continue;
                    homeMargins.Add(hpp - ap);
                    if (r[5] != "") marg.Add((hpp - ap) + U.D(r[5]));
                    if (r[6] != "") tot.Add((hpp + ap) - U.D(r[6]));
                    double mlh = U.D(r[7]), mla = U.D(r[8]);
                    if (mlh != 0 && mla != 0) { double ph = U.ImpliedProb(mlh), pa = U.ImpliedProb(mla), p = ph / (ph + pa); int y = hpp > ap ? 1 : 0; brier += (p - y) * (p - y); ll += -(y * Math.Log(p) + (1 - y) * Math.Log(1 - p)); nml++; }
                }
                Func<List<double>, double> sd = l => { double m = l.Average(); return Math.Sqrt(l.Sum(x => (x - m) * (x - m)) / (l.Count - 1)); };
                HistStats["n_games"] = homeMargins.Count; HistStats["home_margin_mean"] = homeMargins.Average();
                HistStats["margin_resid_sd"] = sd(marg); HistStats["total_resid_sd"] = sd(tot);
                HistStats["ml_brier"] = brier / nml; HistStats["ml_logloss"] = ll / nml;
                HCA = Math.Max(1.0, Math.Min(3.5, HistStats["home_margin_mean"]));
            }
            var ta = U.ReadCsv(P(@"data\bbref\team_adv_" + S.PriorSeason + ".csv"));
            var h = ta[0].ToList();
            var paces = new List<double>(); var ortg = new List<double>(); var orp = new List<double>();
            foreach (var r in ta.Skip(1))
            {
                string name = r[h.IndexOf("team")].Replace("*", "").Trim();
                if (name.StartsWith("League")) continue;
                double pace = U.D(r[h.IndexOf("pace")]), o = U.D(r[h.IndexOf("off_rtg")]);
                paces.Add(pace); ortg.Add(o); orp.Add(U.D(r[h.IndexOf("orb_pct")]));
                TeamPace26ByName[name] = pace;
            }
            LgPace = paces.Average(); LgORtg26 = ortg.Average(); LgOrPct = orp.Average() / 100.0;
            Ppg26 = 0; for (int i = 0; i < paces.Count; i++) Ppg26 += paces[i] * ortg[i] / 100; Ppg26 /= paces.Count;
            HistStats["lg_pace_2526"] = LgPace; HistStats["lg_ortg_2526"] = LgORtg26;
        }
        static Dictionary<string, double> TeamPace26ByName = new Dictionary<string, double>();
        static double LgORtg26, Ppg26;

        // ------------------------------------------------------------------ slate construction (as of date d)
        public static void BuildSlate(DateTime d, bool live, bool quiet = false)
        {
            SlateDate = d.Date; LiveMode = live; RunTime = DateTime.Now;
            AllPlayers = new Dictionary<string, PlayerP>(); TeamPlayers = new Dictionary<string, List<PlayerP>>(); Games = new List<GameCtx>();
            Pace26 = new Dictionary<string, double>(); CsPace = new Dictionary<string, List<double>>(); Verification = new List<string[]>(); Warnings = new List<string>();
            NSIM = S.Sims; WMKT = S.MarketWeight; B2BNet = S.B2BNet;
            CalAlpha = S.Alpha; CalSigmaEff = S.SigmaEff; CalSigmaPace = S.SigmaPace;

            Completed = Store.CompletedBefore(d, S.SeasonStart);
            if (live) LoadBaseMargins();   // backtests build their own, date by date (Walk.Run)
            if (live) Learner.LoadLive(d); // trend learning from every graded game before this slate (backtests feed it date by date)
            Environment(quiet);
            CalibrateGamma();
            DefenseProfiles();
            if (live) { B27 = Data.LoadBref(Store.BrefCsv(S.CurrentSeason, "totals", true), Store.BrefCsv(S.CurrentSeason, "advanced", true)); Idx27 = Index(B27); }
            else { B27 = new Dictionary<string, List<BRow>>(); Idx27 = new Dictionary<string, string>(); } // no look-ahead in backtests

            // live runs only price games that have not tipped (in-progress games would pick up live lines and box scores)
            var events = Store.Events(d).Where(e => live ? !e.Completed && (e.State == "pre" || e.State == "" || e.Id == ForceIncludeId) : e.Completed).ToList();
            if (live) SkippedStarted = Store.Events(d).Where(e => !(e.State == "pre" || e.State == "")).Select(e => e.Away + " @ " + e.Home).ToList();
            // banned teams (overrides\banned_teams.csv): their games are never simulated, live or in backtests. Live runs still
            // read the game summary so its DraftKings line and injury news stay on record (information only).
            BannedIds = new HashSet<string>(Store.Events(d).Where(e => Ban.Involves(e.Away, e.Home)).Select(e => e.Id));
            var banned = events.Where(e => BannedIds.Contains(e.Id)).ToList();
            SkippedBanned = banned.Select(e => e.Away + " @ " + e.Home + (live ? " " + TipTime(e.StartUtc) : "")).ToList();
            if (live) foreach (var e in banned) { try { Store.Summary(e.Id, false); } catch (Exception ex) { Warnings.Add("Banned game " + e.Away + " @ " + e.Home + ": summary not read (" + ex.Message + ")"); } }
            events = events.Where(e => !BannedIds.Contains(e.Id)).ToList();
            if (events.Count == 0) { Warnings.Add("No " + (live ? "upcoming" : "completed") + " games found for " + d.ToString("yyyy-MM-dd") + (banned.Count > 0 ? " (banned, not priced: " + string.Join(", ", SkippedBanned) + ")" : "")); return; }
            var odds = live ? Store.AnOdds(d) : new List<AnGame>();
            var lastGame = new Dictionary<string, DateTime>();
            foreach (var g in Completed) { var gd = DateTime.ParseExact(g.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture); foreach (var t in new[] { g.Home, g.Away }) if (!lastGame.ContainsKey(t) || lastGame[t] < gd) lastGame[t] = gd; }

            foreach (var ev in events)
            {
                var info = Store.Summary(ev.Id, !live);
                var def = new GameDef { Away = ev.Away, Home = ev.Home, EspnId = ev.Id, Tip = TipTime(ev.StartUtc) };
                def.HomeRest = lastGame.ContainsKey(ev.Home) ? (int)(d.Date - lastGame[ev.Home]).TotalDays - 1 : 3;
                def.AwayRest = lastGame.ContainsKey(ev.Away) ? (int)(d.Date - lastGame[ev.Away]).TotalDays - 1 : 3;
                def.HomeB2B = def.HomeRest == 0; def.AwayB2B = def.AwayRest == 0;
                var ctx = new GameCtx { Def = def, Info = info, P = new SimParams { Alpha = S.Alpha, SigmaEff = S.SigmaEff, SigmaPace = S.SigmaPace, SigmaMin = S.SigmaMin, SigmaPlayer = S.SigmaPlayer, Sigma3 = S.Sigma3, BlowStart = S.BlowStart, BlowSpan = S.BlowSpan } };
                // market
                var an = odds.FirstOrDefault(a => a.HomeName == ev.HomeName && a.AwayName == ev.AwayName);
                if (an != null)
                {
                    def.Books = an.Books.Where(b => b.HasMl || b.Total > 0).ToList();
                    def.DK = an.Books.FirstOrDefault(b => b.Book == "DraftKings" && b.Total > 0 && b.HasMl);
                    def.Consensus = an.Books.FirstOrDefault(b => b.Book == "Consensus" && b.Total > 0);
                    def.AnId = an.Id;
                    if (def.DK != null) ctx.MarketSource = "DraftKings (Action Network feed)";
                    else if (def.Consensus != null) { def.DK = def.Consensus; ctx.MarketSource = "Consensus (no DraftKings line yet)"; }
                }
                if (def.DK == null && !double.IsNaN(info.DkTotal) && !double.IsNaN(info.DkSprH))
                {
                    def.DK = new MarketLine { Book = "DraftKings", SprH = info.DkSprH, Total = info.DkTotal, MlH = double.IsNaN(info.DkMlH) ? 0 : info.DkMlH, MlA = double.IsNaN(info.DkMlA) ? 0 : info.DkMlA, SprHOdds = -110, SprAOdds = -110, OverOdds = -110, UnderOdds = -110 };
                    ctx.MarketSource = live ? "DraftKings (ESPN feed; spread/total juice assumed -110)" : "DraftKings closing line (ESPN)";
                }
                ctx.HasMarket = def.DK != null && def.DK.Total > 0;
                if (ctx.HasMarket && !def.DK.HasMl) { Warnings.Add(ev.Away + " @ " + ev.Home + ": no moneyline posted; ML EV omitted"); }
                if (!ctx.HasMarket) Warnings.Add(ev.Away + " @ " + ev.Home + ": no market line found; model-only probabilities");
                // players
                foreach (var side in new[] { true, false })
                {
                    string abbr = side ? ev.Home : ev.Away, tid = side ? ev.HomeId : ev.AwayId, name = side ? ev.HomeName : ev.AwayName;
                    double p26; if (TeamPace26ByName.TryGetValue(name, out p26)) Pace26[abbr] = p26;
                    BuildRoster(abbr, tid, info, live);
                }
                Games.Add(ctx);
            }
            if (live) ApplyOverrides(d);
            foreach (var p in AllPlayers.Values) Project(p, Pri);
            foreach (var g in Games) FinishGame(g, live);
            if (live) SaveBaseMargins();
            if (!quiet) foreach (var g in Games) Say(string.Format(U.IC, "{0} @ {1}: market {2} | model {3:0.0}-{4:0.0} | blend {5:0.0}-{6:0.0} | Q players {7}", g.Def.Away, g.Def.Home, g.HasMarket ? g.MarketSource : "none", g.ModelPtsA, g.ModelPtsH, g.BlendPtsA, g.BlendPtsH,
                string.Join(", ", g.H.Pl.Concat(g.A.Pl).Where(x => x.Status == "Q").Select(x => x.Name))));
        }

        static string TipTime(string utc)
        {
            DateTime dt;
            if (!DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt)) return "";
            var et = TimeZoneInfo.ConvertTimeFromUtc(dt, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
            return et.ToString("h:mm tt", CultureInfo.InvariantCulture) + " ET";
        }

        static void BuildRoster(string abbr, string teamId, GameData info, bool live)
        {
            var list = new List<PlayerP>();
            if (live)
            {
                foreach (var r in Store.Roster(teamId, SlateDate))
                {
                    var p = new PlayerP { Team = abbr, Pid = r.Pid, Name = r.Name, Pos = r.Pos, Group = Data.Group(r.Pos), HeightIn = U.HeightInches(r.Height) };
                    if (ForceOut.Contains(p.Pid)) { p.Status = "OUT"; p.PPlay = 0; p.InjNote = "Inactive tonight (live box)"; list.Add(p); continue; }
                    var inj = info.Injuries.FirstOrDefault(i => i.Pid == p.Pid || U.Norm(i.Name) == U.Norm(p.Name));
                    if (inj != null)
                    {
                        string st = inj.Status.ToLowerInvariant();
                        p.InjNote = "ESPN: " + inj.Status + " (" + inj.Detail.Trim() + ")" + (inj.ReturnDate != "" ? " est. return " + inj.ReturnDate : "");
                        if (st.Contains("out")) { p.Status = "OUT"; p.PPlay = 0; }
                        else if (st.Contains("doubt")) { p.Status = "Q"; p.PPlay = 0.25; }
                        else if (st.Contains("question")) { p.Status = "Q"; p.PPlay = 0.5; }
                        else if (st.Contains("day")) { p.Status = "Q"; p.PPlay = 0.6; }
                        else if (st.Contains("prob")) { p.Status = "Q"; p.PPlay = 0.9; }
                    }
                    list.Add(p);
                }
            }
            else
            {
                foreach (var b in info.Box.Where(x => x.Team == abbr))
                {
                    var p = new PlayerP { Team = abbr, Pid = b.Pid, Name = b.Name, Pos = b.Pos, Group = Data.Group(b.Pos), HeightIn = 78 };
                    if (b.Dnp && !b.Reason.ToUpperInvariant().Contains("COACH")) { p.Status = "OUT"; p.PPlay = 0; p.InjNote = "Inactive: " + (b.Reason == "" ? "not with team / injured" : b.Reason); }
                    list.Add(p);
                }
            }
            foreach (var p in list) AllPlayers[p.Pid] = p;
            TeamPlayers[abbr] = list;
            // current-season production from completed games
            foreach (var g in Completed)
                foreach (var b in g.Box)
                {
                    PlayerP p; if (!AllPlayers.TryGetValue(b.Pid, out p) || p.Team != abbr) continue;
                    if (b.Team != abbr && b.Dnp) continue;
                    if (b.Dnp) { if (b.Reason.ToUpperInvariant().Contains("COACH")) p.DnpCd++; else p.DnpOther++; continue; }
                    if (b.Min <= 0) { p.DnpCd++; continue; }
                    p.Gp++; if (b.Starter) p.Starts++; p.CsBox.Add(b);
                    p.CsMin += b.Min; p.CsMinList.Add(b.Min); p.CsPtsList.Add(b.Pts);
                    p.CsPts += b.Pts; p.Cs2a += b.Fga - b.Tpa; p.Cs2m += b.Fgm - b.Tpm; p.Cs3a += b.Tpa; p.Cs3m += b.Tpm;
                    p.CsFtm += b.Ftm; p.CsFta += b.Fta; p.CsAst += b.Ast; p.CsTov += b.Tov; p.CsOreb += b.Oreb; p.CsDreb += b.Dreb;
                }
            foreach (var p in list)
            {
                string n = U.Norm(p.Name), id;
                if (Idx26.TryGetValue(n, out id)) { p.HasPs = true; p.Ps = B26[id][0]; p.Obpm26 = p.Ps.Obpm; p.Dbpm26 = p.Ps.Dbpm; p.Mp26 = p.Ps.MP; string bt; EspnToBref.TryGetValue(abbr, out bt); p.SameTeam = p.Ps.LastTeam == bt; }
                else if (Idx25.TryGetValue(n, out id)) { p.HasPs = true; p.PsStale = true; p.Ps = B25[id][0]; p.Obpm26 = p.Ps.Obpm; p.Dbpm26 = p.Ps.Dbpm; p.Mp26 = p.Ps.MP * 0.5; }
                if (Idx27.TryGetValue(n, out id)) { var b = B27[id][0]; p.Obpm27 = b.Obpm; p.Dbpm27 = b.Dbpm; p.Mp27 = b.MP; }
            }
        }

        public static string OverridePath(DateTime d) { return P(@"overrides\injuries_" + d.ToString("yyyy-MM-dd") + ".csv"); }

        static void ApplyOverrides(DateTime d)
        {
            string path = OverridePath(d);
            if (!File.Exists(path))
            {
                var sb = new StringBuilder("team,name,status,p_play,note\n# status: OUT, Q (questionable; set p_play 0-1) or AVAIL. Lines starting with # are ignored. Pre-filled from ESPN; edit and re-run.\n");
                foreach (var p in AllPlayers.Values.Where(x => x.Status != "AVAIL").OrderBy(x => x.Team))
                    sb.AppendLine(string.Format(U.IC, "{0},{1},{2},{3},{4}", p.Team, p.Name.Replace(",", ""), p.Status, p.PPlay, p.InjNote.Replace(",", ";")));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, sb.ToString());
                Verification.Add(new[] { "Injury overrides", "CREATED", "Template written to overrides\\" + Path.GetFileName(path) + " from ESPN statuses; edit it and re-run to apply the official report." });
                return;
            }
            int n = 0; var unmatched = new List<string>();
            foreach (var line in File.ReadAllLines(path).Skip(1))
            {
                if (line.Trim() == "" || line.StartsWith("#")) continue;
                var r = U.SplitLine(line, ',');
                if (r.Length < 3) continue;
                List<PlayerP> tl; if (!TeamPlayers.TryGetValue(r[0].Trim(), out tl)) continue;
                var p = tl.FirstOrDefault(x => U.Norm(x.Name) == U.Norm(r[1]));
                if (p == null) { unmatched.Add(r[0] + " " + r[1]); continue; }
                string st = r[2].Trim().ToUpperInvariant();
                p.Status = st == "OUT" ? "OUT" : st.StartsWith("Q") ? "Q" : "AVAIL";
                p.PPlay = p.Status == "OUT" ? 0 : p.Status == "AVAIL" ? 1 : (r.Length > 3 ? U.D(r[3], 0.5) : 0.5);
                if (r.Length > 4 && r[4].Trim() != "") p.InjNote = r[4].Trim();
                n++;
            }
            Verification.Add(new[] { "Injury overrides", "APPLIED", n + " rows from overrides\\" + Path.GetFileName(path) + (unmatched.Count > 0 ? "; not on current ESPN rosters (ignored): " + string.Join(", ", unmatched) : "") });
        }

        // ------------------------------------------------------------------ environment from completed games
        static void Environment(bool quiet)
        {
            var pacePerGame = new List<Tuple<string, string, double>>();
            double ptsCs = 0, minCs = 0; double[] qs = new double[4]; int tipSame = 0, nTip = 0;
            foreach (var g in Completed)
            {
                Func<string, double> poss = t => { var bx = g.Box.Where(b => b.Team == t && b.Played).ToList(); return bx.Sum(b => b.Fga) - bx.Sum(b => b.Oreb) + bx.Sum(b => b.Tov) + 0.44 * bx.Sum(b => b.Fta); };
                double gm = 48 + 5 * g.OT;
                double pace = (poss(g.Home) + poss(g.Away)) / 2 * 48 / gm;
                pacePerGame.Add(Tuple.Create(g.Home, g.Away, pace));
                ptsCs += g.HomePts + g.AwayPts; minCs += 2 * gm;
                for (int q = 0; q < 4; q++) qs[q] += g.HomeQ[q] + g.AwayQ[q];
                if (!string.IsNullOrEmpty(g.TipTeam) && !string.IsNullOrEmpty(g.FirstFgTeam)) { nTip++; if (g.TipTeam == g.FirstFgTeam) tipSame++; }
            }
            if (pacePerGame.Count > 0)
            {
                double csPace = pacePerGame.Average(x => x.Item3);
                foreach (var x in pacePerGame)
                    foreach (var t in new[] { x.Item1, x.Item2 }) { if (!CsPace.ContainsKey(t)) CsPace[t] = new List<double>(); CsPace[t].Add(LgPace * x.Item3 / csPace); }
                double ppgCs = ptsCs / minCs * 48;
                LgORtg = LgORtg26 * (0.5 + 0.5 * ppgCs / Ppg26);
                HistStats["lg_ppg_cs"] = ppgCs;
                double qsum = qs.Sum(); for (int q = 0; q < 4; q++) QShareEmp[q] = qs[q] / qsum;
            }
            else LgORtg = LgORtg26;
            EmpTipTeamFirstFG = nTip > 0 ? (double)tipSame / nTip : 0.5; EmpTipN = nTip;
            HistStats["lg_ortg_used"] = LgORtg;
        }

        static void CalibrateGamma()
        {
            var obs = new List<Tuple<string, double, double, double>>();
            foreach (var g in Completed)
                foreach (var b in g.Box.Where(x => x.Played))
                {
                    string id; double u; var gp = Pri[Data.Group(b.Pos)];
                    if (Idx26.TryGetValue(U.Norm(b.Name), out id) && B26[id][0].MP >= 200) { var r = B26[id][0]; u = (r.FGA + 0.44 * r.FTA + r.TOV) / r.MP; }
                    else u = gp.R2a + gp.R3a + 0.44 * gp.RFta + gp.RTov;
                    obs.Add(Tuple.Create(g.Id + "|" + b.Team, b.Min, u, b.Fga + 0.44 * b.Fta + b.Tov));
                }
            var groups = obs.GroupBy(o => o.Item1).ToList();
            GammaTeamGames = groups.Count;
            if (groups.Count < 30) { Gamma = S.GammaDefault; GammaR2 = double.NaN; GammaR2Prop = double.NaN; return; }
            double bestLoss = double.MaxValue, mean = obs.Average(o => o.Item4), sst = obs.Sum(o => (o.Item4 - mean) * (o.Item4 - mean));
            for (double gg = 0.6; gg <= 2.41; gg += 0.1)
            {
                double loss = 0;
                foreach (var grp in groups)
                {
                    double tot = grp.Sum(o => o.Item4), W = grp.Sum(o => o.Item2 * Math.Pow(o.Item3, gg));
                    foreach (var o in grp) { double pred = tot * o.Item2 * Math.Pow(o.Item3, gg) / W; loss += (o.Item4 - pred) * (o.Item4 - pred); }
                }
                if (Math.Abs(gg - 1.0) < 1e-6) GammaR2Prop = 1 - loss / sst;
                if (loss < bestLoss) { bestLoss = loss; Gamma = Math.Round(gg, 1); }
            }
            GammaR2 = 1 - bestLoss / sst;
            if (S.GammaOverride > 0) Gamma = S.GammaOverride; // backtest-tested alternative to the fitted shot concentration
        }

        // ------------------------------------------------------------------ player projection
        static void Project(PlayerP p, Dictionary<string, GroupPrior> pri)
        {
            var g = pri[p.Group];
            // M0: minutes of position-average play mixed into every rate; larger = extreme rates pulled harder toward the middle
            double csW = S.CsWeight, csS = csW, M0 = S.RatePriorMin, psW = 0, psMp = 0;
            // hot starts: the audit found projections that fade a player's better-than-last-season scoring run low,
            // so players scoring 10%+ above last season's rate lean further on this season for scoring inputs only
            // (shot volume and shooting %; rebounds/assists got worse when included). Strength tested out of sample.
            if (S.HotStartWeight > 1 && p.HasPs && p.Ps.MP > 200 && p.CsMin >= 40 && p.CsPts / p.CsMin > 1.1 * p.Ps.PTS / p.Ps.MP) csS *= S.HotStartWeight;
            double ps2a = 0, ps2m = 0, ps3a = 0, ps3m = 0, psFta = 0, psFtm = 0, psTov = 0, psOrb = 0, psDrb = 0, psAst = 0;
            if (p.HasPs && p.Ps.MP > 0)
            {
                var b = p.Ps;
                double eff = Math.Min(b.MP, 1200) * (p.SameTeam ? 1.0 : 0.8) * (p.PsStale ? 0.5 : 1.0);
                psW = eff / b.MP; psMp = b.MP;
                ps2a = b.FG2A; ps2m = b.FG2; ps3a = b.FG3A; ps3m = b.FG3; psFta = b.FTA; psFtm = b.FT; psTov = b.TOV; psOrb = b.ORB; psDrb = b.DRB; psAst = b.AST;
            }
            double den = p.CsMin * csW + psMp * psW + M0, denS = p.CsMin * csS + psMp * psW + M0;
            Func<double, double, double, double> rate = (c0, p0, pr) => (c0 * csW + p0 * psW + pr * M0) / den;
            Func<double, double, double, double> rateS = (c0, p0, pr) => (c0 * csS + p0 * psW + pr * M0) / denS;
            p.R2a = rateS(p.Cs2a, ps2a, g.R2a); p.R3a = rateS(p.Cs3a, ps3a, g.R3a);
            double rFta = rateS(p.CsFta, psFta, g.RFta);
            p.RTov = rate(p.CsTov, psTov, g.RTov); p.ROreb = rate(p.CsOreb, psOrb, g.ROreb); p.RDreb = rate(p.CsDreb, psDrb, g.RDreb); p.RAst = rate(p.CsAst, psAst, g.RAst);
            // trend learning: scoring and assist rates lean toward what he has actually done against the model's own
            // projections this season (three graded games minimum; strength tested out of sample by Recalibrate)
            p.LearnPts = 1; p.LearnAst = 1;
            var lr = Learner.Of(p.Pid);
            if (lr != null && lr.N >= 3)
            {
                p.LearnPts = Learner.Factor(lr.ActPts, lr.ExpPts, S.LearnPtsK, 0.8, 1.25);
                p.LearnAst = Learner.Factor(lr.ActAst, lr.ExpAst, S.LearnAstK, 0.75, 1.35);
                p.R2a *= p.LearnPts; p.R3a *= p.LearnPts; rFta *= p.LearnPts; p.RAst *= p.LearnAst;
            }
            p.P2 = (p.Cs2m * csS + ps2m * psW + g.P2 * 80) / (p.Cs2a * csS + ps2a * psW + 80);
            p.P3 = (p.Cs3m * csS + ps3m * psW + g.P3 * 120) / (p.Cs3a * csS + ps3a * psW + 120);
            p.Pft = (p.CsFtm * csS + psFtm * psW + g.Pft * 40) / (p.CsFta * csS + psFta * psW + 40);
            double r2m = p.R2a * p.P2, r3m = p.R3a * p.P3;
            p.RTrip = Math.Max(0, rFta - (0.07 * r2m + 0.012 * r3m)) / 2.0;
            p.Usage = p.R2a + p.R3a + p.RTrip + p.RTov;
            p.PickW = p.Usage * Math.Pow(p.Usage / 0.5, Gamma - 1);

            double w26 = p.HasPs ? Math.Min(p.Mp26, 2000) * 0.8 : 0;
            p.Obpm = (p.Obpm26 * w26 + p.Obpm27 * p.Mp27 + (-1.8) * 250) / (w26 + p.Mp27 + 250);
            p.Dbpm = (p.Dbpm26 * w26 + p.Dbpm27 * p.Mp27 + (-0.4) * 250) / (w26 + p.Mp27 + 250);

            double cs = p.CsMpg, ps = p.PsMpg, k = (p.SameTeam ? 1.5 : 0.75) * S.MinPriorK;
            // season trend: recent games count more for minutes so role changes show up sooner (strength tested out of sample)
            if (S.MinRecency > 0 && p.CsMinList.Count >= 2)
            {
                double w = 1, sw = 0, sm = 0;
                for (int i = p.CsMinList.Count - 1; i >= 0; i--) { sm += w * p.CsMinList[i]; sw += w; w *= 1 - S.MinRecency; }
                cs = sm / sw;
            }
            if (p.Gp > 0 && ps > 0) p.BaseMin = (p.Gp * cs + k * ps) / (p.Gp + k);
            else if (p.Gp > 0) p.BaseMin = cs;
            else if (ps > 0) p.BaseMin = ps * (p.DnpCd >= 2 ? 0.25 : 0.75);
            else p.BaseMin = p.DnpCd > 0 ? 2 : 0;
            if (p.Gp + p.DnpCd > 0) p.PRot = (p.Gp + 0.5) / (p.Gp + p.DnpCd + 0.5);
            else p.PRot = p.HasPs ? 0.8 : 0.3;
            p.ExpMin = p.BaseMin * p.PRot;
            // backtests showed high-usage players out-playing their minute projections; Recalibrate fits this slope out of sample
            if (p.ExpMin >= 12) p.ExpMin = Math.Max(0, Math.Min(42, p.ExpMin + S.MinUsageSlope * (p.Usage - 0.45)));
            // the audit found heavy-minute projections running ~2.5 min high; shrink the part above 28 (strength tested out of sample)
            if (p.ExpMin > 28) p.ExpMin -= S.MinTopShrink * (p.ExpMin - 28);
            // minutes trend: close part of the gap to what he has played in his last three games (role changes, returns
            // from injury and shortened rotations show up here before the season average catches them)
            p.MinTrendShift = 0;
            if (S.MinTrend > 0 && p.CsMinList.Count >= 3 && p.ExpMin > 0)
            {
                int c = p.CsMinList.Count; double l3 = (p.CsMinList[c - 1] + p.CsMinList[c - 2] + p.CsMinList[c - 3]) / 3;
                double before = p.ExpMin;
                p.ExpMin = Math.Max(0, Math.Min(42, p.ExpMin + S.MinTrend * (l3 * p.PRot - p.ExpMin)));
                p.MinTrendShift = p.ExpMin - before;
            }
            p.StartRate = p.Gp > 0 ? (double)p.Starts / p.Gp : (p.HasPs && p.Ps.G > 0 ? p.Ps.GS / p.Ps.G : 0);
        }

        // ------------------------------------------------------------------ teams, ratings, targets
        static SimTeam BuildTeam(string abbr)
        {
            var all = TeamPlayers[abbr].Where(p => p.Status != "OUT" && p.ExpMin > 0.3).OrderByDescending(p => p.ExpMin).ToList();
            var qs = all.Where(p => p.Status == "Q" && p.ExpMin >= 6).OrderByDescending(p => p.ExpMin).Take(4).ToList();
            foreach (var p in all.Where(p => p.Status == "Q" && !qs.Contains(p))) p.ExpMin *= p.PPlay;
            var team = new SimTeam { Abbr = abbr, Pl = all.ToArray() };
            int nsc = 1 << qs.Count;
            var scens = new List<Scenario>();
            for (int mask = 0; mask < nsc; mask++)
            {
                double prob = 1; var label = new List<string>();
                var playing = new bool[all.Count];
                for (int i = 0; i < all.Count; i++) playing[i] = true;
                for (int j = 0; j < qs.Count; j++)
                {
                    bool plays = (mask & (1 << j)) == 0;
                    prob *= plays ? qs[j].PPlay : 1 - qs[j].PPlay;
                    if (!plays) { playing[all.IndexOf(qs[j])] = false; label.Add(qs[j].Name + " OUT"); }
                }
                var m = new double[all.Count];
                for (int i = 0; i < all.Count; i++) m[i] = playing[i] ? all[i].ExpMin : 0;
                Normalize(m);
                var starter = new bool[all.Count];
                foreach (var i in Enumerable.Range(0, all.Count).Where(i => playing[i]).OrderByDescending(i => all[i].StartRate * 25 + m[i]).Take(5)) starter[i] = true;
                scens.Add(new Scenario { Prob = prob, Min = m, Starter = starter, Label = label.Count == 0 ? "All questionable players active" : string.Join("; ", label) });
            }
            team.Scen = scens.Where(s => s.Prob > 1e-9).ToArray();
            double tot = team.Scen.Sum(s => s.Prob);
            team.ScenCum = new double[team.Scen.Length]; double c = 0;
            for (int i = 0; i < team.Scen.Length; i++) { team.Scen[i].Prob /= tot; c += team.Scen[i].Prob; team.ScenCum[i] = c; }
            return team;
        }

        static void Normalize(double[] m)
        {
            double S0 = m.Sum();
            if (S0 <= 0) return;
            if (S0 > 240)
            {
                for (int it = 0; it < 30 && m.Sum() > 240.01; it++)
                {
                    double excess = m.Sum() - 240, W = 0; var w = new double[m.Length];
                    for (int i = 0; i < m.Length; i++) { w[i] = m[i] * Math.Max(0.05, 1 - m[i] / 40.0); W += w[i]; }
                    for (int i = 0; i < m.Length; i++) m[i] = Math.Max(0, m[i] - excess * w[i] / W);
                }
                return;
            }
            var cap = new double[m.Length];
            for (int i = 0; i < m.Length; i++) cap[i] = m[i] >= 8 ? Math.Max(m[i], Math.Min(38, m[i] * 1.4 + 4)) : m[i] + 6;
            for (int it = 0; it < 30; it++)
            {
                double deficit = 240 - m.Sum(); if (deficit < 0.01) break;
                double W = 0; for (int i = 0; i < m.Length; i++) if (m[i] > 0 && m[i] < cap[i]) W += m[i];
                if (W <= 0) break;
                for (int i = 0; i < m.Length; i++) if (m[i] > 0 && m[i] < cap[i]) m[i] = Math.Min(cap[i], m[i] + deficit * m[i] / W);
            }
            double rest = 240 - m.Sum();
            if (rest > 0.01) { double s2 = m.Sum(); for (int i = 0; i < m.Length; i++) m[i] += rest * m[i] / s2; }
        }

        public static double ExpectedMin(SimTeam t, int i) { double s = 0; foreach (var sc in t.Scen) s += sc.Prob * sc.Min[i]; return s; }

        static void TeamRatings(SimTeam t, out double off, out double def, out double ast, out double orIdx, out double drIdx)
        {
            off = 0; def = 0; ast = 0; double orr = 0, drr = 0;
            for (int i = 0; i < t.Pl.Length; i++)
            {
                double m = ExpectedMin(t, i); var p = t.Pl[i];
                off += p.Obpm * m / 48; def += p.Dbpm * m / 48; ast += p.RAst * m; orr += p.ROreb * m; drr += p.RDreb * m;
            }
            orIdx = orr / (LgOrebRate * 240); drIdx = drr / (LgDrebRate * 240);
        }

        static void FinishGame(GameCtx ctx, bool live)
        {
            var d = ctx.Def;
            ApplyMatchups(ctx);
            ctx.H = BuildTeam(d.Home); ctx.A = BuildTeam(d.Away);
            double ah, aa, orh, ora, drh, dra;
            TeamRatings(ctx.H, out ctx.OffH, out ctx.DefH, out ah, out orh, out drh);
            TeamRatings(ctx.A, out ctx.OffA, out ctx.DefA, out aa, out ora, out dra);
            ctx.H.TargetAst = ah; ctx.A.TargetAst = aa;
            ctx.H.POreb = Math.Max(0.15, Math.Min(0.40, LgOrPct * Math.Sqrt(orh / dra) * OrebFactor(d.Away)));
            ctx.A.POreb = Math.Max(0.15, Math.Min(0.40, LgOrPct * Math.Sqrt(ora / drh) * OrebFactor(d.Home)));
            Func<string, double> tpace = t =>
            {
                double p26 = Pace26.ContainsKey(t) ? Pace26[t] : LgPace;
                var cl = CsPace.ContainsKey(t) ? CsPace[t] : new List<double>();
                return (p26 * 8 + cl.Sum()) / (8 + cl.Count);
            };
            ctx.PaceH = tpace(d.Home); ctx.PaceA = tpace(d.Away);
            ctx.ModelPoss = ctx.PaceH * ctx.PaceA / LgPace;
            double ortgH = LgORtg + (ctx.OffH - ctx.DefA) + HCA / 2 - (d.HomeB2B ? B2BNet / 2 : 0) + (d.AwayB2B ? B2BNet / 2 : 0);
            double ortgA = LgORtg + (ctx.OffA - ctx.DefH) - HCA / 2 - (d.AwayB2B ? B2BNet / 2 : 0) + (d.HomeB2B ? B2BNet / 2 : 0);
            ctx.ModelPtsH = ortgH * ctx.ModelPoss / 100; ctx.ModelPtsA = ortgA * ctx.ModelPoss / 100;
            if (ctx.HasMarket)
            {
                ctx.MktPtsH = (d.DK.Total - d.DK.SprH) / 2; ctx.MktPtsA = (d.DK.Total + d.DK.SprH) / 2;
                ctx.BlendPtsH = WMKT * ctx.MktPtsH + (1 - WMKT) * ctx.ModelPtsH;
                ctx.BlendPtsA = WMKT * ctx.MktPtsA + (1 - WMKT) * ctx.ModelPtsA;
            }
            else
            {
                ctx.MktPtsH = double.NaN; ctx.MktPtsA = double.NaN; ctx.BlendPtsH = ctx.ModelPtsH; ctx.BlendPtsA = ctx.ModelPtsA;
                d.DK = new MarketLine { Book = "none", SprH = Math.Round(ctx.ModelPtsA - ctx.ModelPtsH) + 0.5, Total = Math.Round(ctx.ModelPtsH + ctx.ModelPtsA) + 0.5, SprHOdds = -110, SprAOdds = -110, OverOdds = -110, UnderOdds = -110 };
            }
            lock (BaseMargins) BaseMargins[d.EspnId] = ctx.ModelPtsH - ctx.ModelPtsA;
            if (S.TeamFormWeight > 0)
            {   // team form: shift the expected margin toward teams that keep beating what was expected of them (tested in Recalibrate)
                ctx.FormShift = S.TeamFormWeight * (TeamForm(d.Home) - TeamForm(d.Away));
                ctx.BlendPtsH += ctx.FormShift / 2; ctx.BlendPtsA -= ctx.FormShift / 2;
            }
            ctx.BlendPoss = ctx.ModelPoss * Math.Sqrt((ctx.BlendPtsH + ctx.BlendPtsA) / (ctx.ModelPtsH + ctx.ModelPtsA));
            int hh = TallestStarter(ctx.H), ha = TallestStarter(ctx.A);
            ctx.P.PTipHome = live ? Math.Max(0.3, Math.Min(0.7, 0.5 + 0.035 * (hh - ha))) : 0.5;
            ctx.P.QShare = QShareEmp.Select(x => 0.5 * x + 0.5 * 0.25).ToArray();
            double qs = ctx.P.QShare.Sum(); for (int q = 0; q < 4; q++) ctx.P.QShare[q] /= qs;
            ctx.Players.Clear(); ctx.Players.AddRange(ctx.H.Pl); ctx.Players.AddRange(ctx.A.Pl);
        }

        // average of (actual margin - expected margin) over this season's games before the slate, shrunk toward 0 with
        // TeamFormPrior games of nothing. Expected = the model's own pre-game margin (default) or the DraftKings spread.
        // Completed holds only earlier games, so backtests have no look-ahead.
        public static double TeamForm(string team)
        {
            double sum = 0; int n = 0; bool model = S.TeamFormSource != "market";
            foreach (var g in Completed)
            {
                double exp;
                if (model) { lock (BaseMargins) if (!BaseMargins.TryGetValue(g.Id, out exp)) continue; }
                else { if (double.IsNaN(g.DkSprH)) continue; exp = -g.DkSprH; }
                double resid = g.HomePts - g.AwayPts - exp;
                if (g.Home == team) { sum += resid; n++; }
                else if (g.Away == team) { sum -= resid; n++; }
            }
            return n == 0 ? 0 : sum / (n + S.TeamFormPrior);
        }

        // the model's own pre-game margin (home minus away, before any team-form shift) for every game it has priced:
        // recorded game by game in walk-forward backtests and live runs, kept in data\static\model_margins.csv
        public static Dictionary<string, double> BaseMargins = new Dictionary<string, double>();
        static string MarginsPath { get { return Path.Combine(Store.Root, "data", "static", "model_margins.csv"); } }
        public static void LoadBaseMargins()
        {
            lock (BaseMargins)
            {
                BaseMargins.Clear();
                if (File.Exists(MarginsPath)) foreach (var r in U.ReadCsv(MarginsPath).Skip(1)) if (r.Length >= 2) BaseMargins[r[0]] = U.D(r[1]);
            }
        }
        public static void SaveBaseMargins()
        {
            lock (BaseMargins)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarginsPath));
                File.WriteAllLines(MarginsPath, new[] { "game_id,model_margin_home" }.Concat(BaseMargins.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Key + "," + kv.Value.ToString("0.00", U.IC))), new UTF8Encoding(false));
            }
        }

        static int TallestStarter(SimTeam t)
        {
            int best = 0;
            for (int i = 0; i < t.Pl.Length; i++) if (t.Scen[0].Starter[i]) best = Math.Max(best, t.Pl[i].HeightIn);
            return best;
        }

        // ------------------------------------------------------------------ simulation
        static void CalibrateEff(GameCtx g, SimParams p, double tH, double tA, double poss, int n, int iters, ulong seed)
        {
            g.H.TargetPts = tH; g.A.TargetPts = tA; p.PossPerTeam = poss;
            for (int it = 0; it < iters; it++)
            {
                var r = new GameSim(g.H, g.A, p).Run(n, seed + (ulong)it, false);
                double mh = r.SumPtsH / n, ma = r.SumPtsA / n;
                g.H.Eff = Math.Max(0.7, Math.Min(1.35, g.H.Eff * Math.Pow(tH / mh, 1.1)));
                g.A.Eff = Math.Max(0.7, Math.Min(1.35, g.A.Eff * Math.Pow(tA / ma, 1.1)));
                double ah = r.SumAstH / n, aa = r.SumAstA / n;
                if (ah > 0) g.H.Kappa = Math.Max(0.6, Math.Min(1.4, g.H.Kappa * Math.Pow(g.H.TargetAst / ah, 0.8)));
                if (aa > 0) g.A.Kappa = Math.Max(0.6, Math.Min(1.4, g.A.Kappa * Math.Pow(g.A.TargetAst / aa, 0.8)));
            }
        }

        public static void Simulate(int nsim, bool modelOnlyRun, int calibN = 20000)
        {
            NSIM = nsim;
            var seedBase = (ulong)(SlateDate.Year * 10000 + SlateDate.Month * 100 + SlateDate.Day) * 1000;
            Parallel.ForEach(Games.Select((g, i) => Tuple.Create(g, i)), new ParallelOptions { MaxDegreeOfParallelism = System.Environment.ProcessorCount }, t =>
            {
                var g = t.Item1; ulong seed = seedBase + (ulong)t.Item2 * 10;
                if (modelOnlyRun)
                {
                    g.H.Eff = 1; g.A.Eff = 1; g.H.Kappa = 1; g.A.Kappa = 1;
                    CalibrateEff(g, g.P, g.ModelPtsH, g.ModelPtsA, g.ModelPoss, calibN / 2, 3, seed + 1);
                    var rm = new GameSim(g.H, g.A, g.P).Run(Math.Max(10000, nsim / 2), seed + 5, true);
                    int wins = 0; double mm = 0, mt = 0;
                    for (int s = 0; s < rm.N; s++) { if (rm.HomePts[s] > rm.AwayPts[s]) wins++; mm += rm.HomePts[s] - rm.AwayPts[s]; mt += rm.HomePts[s] + rm.AwayPts[s]; }
                    g.ModelWinH = (double)wins / rm.N; g.ModelMeanMargin = mm / rm.N; g.ModelMeanTotal = mt / rm.N;
                }
                g.H.Eff = 1; g.A.Eff = 1; g.H.Kappa = 1; g.A.Kappa = 1;
                CalibrateEff(g, g.P, g.BlendPtsH, g.BlendPtsA, g.BlendPoss, calibN, 5, seed + 2);
                g.Res = new GameSim(g.H, g.A, g.P).Run(nsim, seed + 7, true);
                if (!modelOnlyRun) { g.ModelWinH = double.NaN; }
                Summarize(g);
            });
        }

        public static double NoVigHome(MarketLine m)
        {
            if (m == null || !m.HasMl) return double.NaN;
            double h = U.ImpliedProb(m.MlH), a = U.ImpliedProb(m.MlA); return h / (h + a);
        }

        static void Summarize(GameCtx g)
        {
            var r = g.Res; int n = r.N;
            int wins = 0, cover = 0, over = 0, ot = 0, blow = 0, close = 0, push = 0, opush = 0;
            double sm = 0, st = 0; var totals = new int[n];
            for (int s = 0; s < n; s++)
            {
                int m = r.HomePts[s] - r.AwayPts[s], t = r.HomePts[s] + r.AwayPts[s];
                totals[s] = t;
                if (m > 0) wins++;
                double cm = m + g.Def.DK.SprH;
                if (cm > 0) cover++; else if (cm == 0) push++;
                if (t > g.Def.DK.Total) over++; else if (t == g.Def.DK.Total) opush++;
                if (r.OT[s] > 0) ot++; if (Math.Abs(m) >= 20) blow++; if (Math.Abs(m) <= 5) close++;
                sm += m; st += t;
            }
            g.WinH = (double)wins / n; g.MeanMargin = sm / n; g.MeanTotal = st / n;
            double vm = 0, vt = 0;
            for (int s = 0; s < n; s++) { double a = r.HomePts[s] - r.AwayPts[s] - g.MeanMargin, b = totals[s] - g.MeanTotal; vm += a * a; vt += b * b; }
            g.SdMargin = Math.Sqrt(vm / n); g.SdTotal = Math.Sqrt(vt / n);
            g.PCoverH = (double)cover / (n - push); g.POver = (double)over / (n - opush); g.POT = (double)ot / n; g.PBlowout = (double)blow / n; g.PClose = (double)close / n;
            Array.Sort(totals);
            g.TotalPct[0] = (int)U.Quantile(totals, 0.10); g.TotalPct[1] = (int)U.Quantile(totals, 0.25); g.TotalPct[2] = (int)U.Quantile(totals, 0.5);
            g.TotalPct[3] = (int)U.Quantile(totals, 0.75); g.TotalPct[4] = (int)U.Quantile(totals, 0.90);
        }
    }
}
