using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace NbaQuant
{
    static class J
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };
        public static object Parse(string s) { return Ser.DeserializeObject(s); }
        public static object Get(object o, string path)
        {
            foreach (var part in path.Split('.'))
            {
                if (o == null) return null;
                var d = o as Dictionary<string, object>;
                if (d != null) { object v; if (!d.TryGetValue(part, out v)) return null; o = v; continue; }
                var a = o as object[]; int i;
                if (a != null && int.TryParse(part, out i)) { o = i < a.Length ? a[i] : null; continue; }
                return null;
            }
            return o;
        }
        public static string S(object o, string path) { var v = Get(o, path); return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); }
        public static double D(object o, string path, double def = double.NaN)
        {
            var v = Get(o, path);
            if (v == null) return def;
            if (v is string) return U.D((string)v, def);
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return def; }
        }
        public static bool B(object o, string path) { var v = Get(o, path); return v is bool && (bool)v; }
        public static object[] A(object o, string path) { return Get(o, path) as object[] ?? new object[0]; }
    }

    class BoxLine
    {
        public string Pid, Name, Team, Pos, Reason;
        public bool Starter, Dnp;
        public double Min;
        public int Pts, Fgm, Fga, Tpm, Tpa, Ftm, Fta, Reb, Ast, Tov, Stl, Blk, Oreb, Dreb, Pf;
        public int[] QPts = new int[4];
        public bool Played { get { return !Dnp && Min > 0; } }
    }

    class InjuryLine { public string Team, Pid, Name, Status, Detail, ReturnDate; }

    // something in the play-by-play that changes a game beyond shooting: an ejection, a flagrant or a technical foul
    class GameEvent { public string Type, Clock, Text, Pid, Team; public int Period, ScoreH, ScoreA; }

    class GameData
    {
        public string Id, Date, Home, Away, HomeId, AwayId, HomeName, AwayName, StartUtc, StatusText;
        public bool Completed;
        public int HomePts, AwayPts, OT, Period; public double ClockSec; public string State;
        public int[] HomeQ = new int[4], AwayQ = new int[4];
        public List<BoxLine> Box = new List<BoxLine>();
        public string FirstFgPid, FirstFgTeam, TipTeam;
        public string DkDetails; public double DkTotal = double.NaN, DkMlH = double.NaN, DkMlA = double.NaN, DkSprH = double.NaN;
        public List<InjuryLine> Injuries = new List<InjuryLine>();
        public List<string> Officials = new List<string>();
        public List<GameEvent> Events = new List<GameEvent>();   // ejections, flagrant and technical fouls (play-by-play)
        public int HomeLead, AwayLead, HomeTech, AwayTech;        // largest lead each side held; technical fouls charged
        public int HomeQ123 { get { return HomeQ[0] + HomeQ[1] + HomeQ[2]; } }
        public int AwayQ123 { get { return AwayQ[0] + AwayQ[1] + AwayQ[2]; } }
    }

    static class Espn
    {
        public const string Base = "https://site.api.espn.com/apis/site/v2/sports/basketball/nba/";

        public static GameData ParseSummary(string json)
        {
            var root = J.Parse(json);
            var g = new GameData();
            var comp = J.Get(root, "header.competitions.0");
            g.Id = J.S(root, "header.id");
            g.StartUtc = J.S(comp, "date");
            DateTime dt;
            if (DateTime.TryParse(g.StartUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                g.Date = dt.AddHours(-4).ToString("yyyy-MM-dd");
            g.Completed = J.B(comp, "status.type.completed");
            g.StatusText = J.S(comp, "status.type.shortDetail");
            g.State = J.S(comp, "status.type.state");
            g.Period = (int)J.D(comp, "status.period", 0);
            var clk = J.S(comp, "status.displayClock").Split(':');
            g.ClockSec = clk.Length == 2 ? U.D(clk[0]) * 60 + U.D(clk[1]) : U.D(clk[0], 0);
            var teamById = new Dictionary<string, string>();
            foreach (var c in J.A(comp, "competitors"))
            {
                bool home = J.S(c, "homeAway") == "home";
                string ab = J.S(c, "team.abbreviation"), id = J.S(c, "team.id"), nm = J.S(c, "team.displayName");
                teamById[id] = ab;
                int sc = (int)J.D(c, "score", 0);
                var ls = J.A(c, "linescores").Select(x => (int)J.D(x, "displayValue", 0)).ToList();
                if (home) { g.Home = ab; g.HomeId = id; g.HomeName = nm; g.HomePts = sc; for (int q = 0; q < 4 && q < ls.Count; q++) g.HomeQ[q] = ls[q]; g.OT = Math.Max(0, ls.Count - 4); }
                else { g.Away = ab; g.AwayId = id; g.AwayName = nm; g.AwayPts = sc; for (int q = 0; q < 4 && q < ls.Count; q++) g.AwayQ[q] = ls[q]; }
            }
            var byPid = new Dictionary<string, BoxLine>();
            foreach (var tp in J.A(root, "boxscore.players"))
            {
                string ta = J.S(tp, "team.abbreviation");
                foreach (var ath in J.A(tp, "statistics.0.athletes"))
                {
                    var b = new BoxLine { Pid = J.S(ath, "athlete.id"), Name = J.S(ath, "athlete.displayName"), Team = ta, Pos = J.S(ath, "athlete.position.abbreviation"), Starter = J.B(ath, "starter"), Reason = J.S(ath, "reason") };
                    var st = J.A(ath, "stats").Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToArray();
                    b.Dnp = J.B(ath, "didNotPlay") || st.Length < 14;
                    if (!b.Dnp)
                    {
                        Func<string, int, int> part = (s, k) => { var sp = s.Split('-'); return sp.Length > k ? (int)U.D(sp[k]) : 0; };
                        b.Min = U.D(st[0]); b.Pts = (int)U.D(st[1]); b.Fgm = part(st[2], 0); b.Fga = part(st[2], 1); b.Tpm = part(st[3], 0); b.Tpa = part(st[3], 1);
                        b.Ftm = part(st[4], 0); b.Fta = part(st[4], 1); b.Reb = (int)U.D(st[5]); b.Ast = (int)U.D(st[6]); b.Tov = (int)U.D(st[7]); b.Stl = (int)U.D(st[8]);
                        b.Blk = (int)U.D(st[9]); b.Oreb = (int)U.D(st[10]); b.Dreb = (int)U.D(st[11]); b.Pf = (int)U.D(st[12]);
                    }
                    g.Box.Add(b); byPid[b.Pid] = b;
                }
            }
            foreach (var o in J.A(root, "gameInfo.officials")) { string on = J.S(o, "displayName"); if (on != "") g.Officials.Add(on); }
            foreach (var bt in J.A(root, "boxscore.teams"))
            {
                bool home = J.S(bt, "team.abbreviation") == g.Home;
                foreach (var st in J.A(bt, "statistics"))
                {
                    string nm = J.S(st, "name"); int v = (int)J.D(st, "displayValue", 0);
                    if (nm == "largestLead") { if (home) g.HomeLead = v; else g.AwayLead = v; }
                    else if (nm == "technicalFouls") { if (home) g.HomeTech = v; else g.AwayTech = v; }
                }
            }
            bool tipDone = false;
            foreach (var pl in J.A(root, "plays"))
            {
                string ptxt = J.S(pl, "text"); string plow = ptxt.ToLowerInvariant();
                if (plow.Contains("eject") || (plow.Contains("foul") && (plow.Contains("flagrant") || plow.Contains("technical")) && !plow.Contains("free throw") && !plow.Contains("3-second")))
                {
                    string epid = J.S(pl, "participants.0.athlete.id"); BoxLine eb; byPid.TryGetValue(epid, out eb);
                    string etid = J.S(pl, "team.id");
                    g.Events.Add(new GameEvent
                    {
                        Type = plow.Contains("eject") ? "ejection" : plow.Contains("flagrant") ? "flagrant" : "technical",
                        Period = (int)J.D(pl, "period.number", 0), Clock = J.S(pl, "clock.displayValue"), Text = ptxt.Trim(), Pid = epid,
                        Team = eb != null ? eb.Team : (teamById.ContainsKey(etid) ? teamById[etid] : ""), ScoreH = (int)J.D(pl, "homeScore", 0), ScoreA = (int)J.D(pl, "awayScore", 0)
                    });
                }
                if (!tipDone && J.S(pl, "type.text") == "Jumpball")
                {
                    tipDone = true;
                    string gainer = J.S(pl, "participants.2.athlete.id");
                    BoxLine gb; if (gainer != "" && byPid.TryGetValue(gainer, out gb)) g.TipTeam = gb.Team;
                }
                if (!J.B(pl, "scoringPlay")) continue;
                string pid = J.S(pl, "participants.0.athlete.id");
                int v = (int)J.D(pl, "scoreValue", 0), q = (int)J.D(pl, "period.number", 0);
                BoxLine b;
                if (byPid.TryGetValue(pid, out b) && q >= 1 && q <= 4) b.QPts[q - 1] += v;
                if (g.FirstFgPid == null && v >= 2) { g.FirstFgPid = pid; string tid = J.S(pl, "team.id"); g.FirstFgTeam = b != null ? b.Team : (teamById.ContainsKey(tid) ? teamById[tid] : ""); }
            }
            foreach (var pc in J.A(root, "pickcenter"))
            {
                if (J.S(pc, "provider.name") != "DraftKings") continue;
                g.DkDetails = J.S(pc, "details"); g.DkTotal = J.D(pc, "overUnder");
                g.DkMlH = J.D(pc, "homeTeamOdds.moneyLine"); g.DkMlA = J.D(pc, "awayTeamOdds.moneyLine");
                var parts = g.DkDetails.Split(' ');
                if (parts.Length == 2) { double v = U.D(parts[1]); g.DkSprH = parts[0] == g.Home ? v : -v; }
                else if (g.DkDetails.ToUpperInvariant().Contains("EVEN")) g.DkSprH = 0;
                break;
            }
            foreach (var t in J.A(root, "injuries"))
                foreach (var i in J.A(t, "injuries"))
                    g.Injuries.Add(new InjuryLine { Team = J.S(t, "team.abbreviation"), Pid = J.S(i, "athlete.id"), Name = J.S(i, "athlete.displayName"), Status = J.S(i, "status"), Detail = J.S(i, "details.type") + " " + J.S(i, "details.location"), ReturnDate = J.S(i, "details.returnDate") });
            return g;
        }
    }
}
