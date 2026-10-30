using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NbaQuant
{
    class Settings
    {
        public DateTime SeasonStart = new DateTime(2026, 10, 20);
        public string CurrentSeason = "2027", PriorSeason = "2026", OlderSeason = "2025";
        public int Sims = 100000, BacktestSims = 20000;
        public double MarketWeight = 0.75, Alpha = 0.25, SigmaEff = 0.025, SigmaPace = 2.0, SigmaMin = 0.12, SigmaPlayer = 0.0;
        public double B2BNet = 1.8, GammaDefault = 1.1;
        public double MinUsageSlope = 0.0;   // extra projected minutes per unit of per-minute usage above 0.45 (fitted by Recalibrate)
        public double CsWeight = 1.5;        // weight on each 2026-27 minute vs prior-season minutes in player rates
        public double MatchupStrength = 0.0; // 0 = ignore opponent defensive profile, 1 = apply shrunk profile in full
        public double MatchupShrinkGames = 6;// games of league-average prior when estimating a team's defensive profile
        public double DdLogitSlope = 1.0, DdLogitShift = 0.0; // calibration map for double-double probabilities (fitted out of sample)
        public double Sigma3 = 0.0;          // extra 3-point shooting-night SD (3PM dispersion)
        public double MinTopShrink = 0.0;    // share of projected minutes above 28 removed (heavy-minute projections run high)
        public double BlowStart = 12, BlowSpan = 14; // Q4 blowout benching window (margin entering Q4)
        public double MinRecency = 0.0;      // per-game decay for recency-weighted season minutes (0 = plain season average)
        public double MinPriorK = 1.0;       // multiplier on the games' worth of weight given to last season's minutes
        public double RatePriorMin = 150;    // minutes of position-average play blended into each player's per-minute rates
        public double GammaOverride = 0;     // shot-concentration exponent; 0 = fitted from this season's box scores
        public double HotStartWeight = 1.0;  // extra multiplier on CsWeight for players outscoring last season's rate (1 = off)
        public string PropCal = "";          // per-stat logit maps for player thresholds, "PTS:shift:slope;..." (fitted out of sample)
        public string LastRecalibration = "";

        public static Settings Load(string path)
        {
            var s = new Settings();
            if (!File.Exists(path)) { s.Save(path); return s; }
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim(); if (t.Length == 0 || t.StartsWith("#")) continue;
                int eq = t.IndexOf('='); if (eq < 0) continue;
                s.Set(t.Substring(0, eq).Trim(), t.Substring(eq + 1).Trim());
            }
            return s;
        }
        public bool Set(string k, string v)
        {
            var f = typeof(Settings).GetField(k); if (f == null) return false;
            if (f.FieldType == typeof(int)) f.SetValue(this, (int)U.D(v));
            else if (f.FieldType == typeof(double)) f.SetValue(this, U.D(v));
            else if (f.FieldType == typeof(DateTime)) f.SetValue(this, DateTime.ParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture));
            else f.SetValue(this, v);
            return true;
        }
        public void Save(string path)
        {
            var sb = new StringBuilder("# NBA Quant settings. Edit with care; recalibration rewrites the model parameters.\n");
            foreach (var f in typeof(Settings).GetFields())
            {
                object v = f.GetValue(this);
                string sv = v is DateTime ? ((DateTime)v).ToString("yyyy-MM-dd") : v is double ? ((double)v).ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture);
                sb.AppendLine(f.Name + "=" + sv);
            }
            File.WriteAllText(path, sb.ToString());
        }
    }

    class EventRef { public string Id, Home, Away, HomeId, AwayId, HomeName, AwayName, StartUtc, Status, State; public bool Completed; }
    class RosterEntry { public string Pid, Name, Pos, Height; }
    class AnGame { public string Id, HomeName, AwayName; public List<MarketLine> Books = new List<MarketLine>(); }
    class PropLine { public string Player, Stat, Book; public double Line, OverOdds = double.NaN, UnderOdds = double.NaN; }

    static class Store
    {
        public static string Root;
        public static string OddsFile;   // when set, odds come from this saved snapshot (e.g. pre-tip lines for in-game runs)
        public static Action<string> Log = s => Console.WriteLine(s);
        public static Dictionary<string, string> AnBooks = new Dictionary<string, string> { { "15", "Consensus" }, { "30", "Open" }, { "68", "DraftKings" }, { "69", "FanDuel" }, { "71", "BetRivers" }, { "75", "BetMGM" }, { "79", "bet365" }, { "123", "Caesars" } };
        static string C(string rel) { return Path.Combine(Root, "data", "cache", rel); }

        static TimeSpan? AgeFor(DateTime d)
        {
            if (d.Date < DateTime.Now.Date.AddDays(-1)) return null;
            return d.Date == DateTime.Now.Date ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(10); // game-day state changes fast
        }

        public static List<EventRef> Events(DateTime d)
        {
            string ds = d.ToString("yyyyMMdd");
            var json = Net.Cached(Espn.Base + "scoreboard?dates=" + ds, C(@"espn\scoreboard_" + ds + ".json"), AgeFor(d));
            var evs = J.A(J.Parse(json), "events");
            if (d.Date < DateTime.Now.Date && (evs.Length == 0 || evs.Any(e => { var st = J.S(e, "competitions.0.status.type.state"); return st == "in" || st == "pre"; })))
            {   // a past date's scoreboard saved mid-game, before tip, or empty (a throttled response during a cold-cache
                // cloud run dropped two whole dates) is re-read; an empty answer is never kept, so the next run tries again
                json = Net.Get(Espn.Base + "scoreboard?dates=" + ds);
                string path = C(@"espn\scoreboard_" + ds + ".json");
                if (J.A(J.Parse(json), "events").Length > 0) File.WriteAllText(path, json, new UTF8Encoding(false));
                else if (File.Exists(path)) File.Delete(path);
            }
            var res = new List<EventRef>();
            foreach (var e in J.A(J.Parse(json), "events"))
            {
                var c = J.Get(e, "competitions.0");
                var ev = new EventRef { Id = J.S(e, "id"), StartUtc = J.S(e, "date"), Completed = J.B(c, "status.type.completed"), Status = J.S(c, "status.type.shortDetail"), State = J.S(c, "status.type.state") };
                foreach (var t in J.A(c, "competitors"))
                {
                    if (J.S(t, "homeAway") == "home") { ev.Home = J.S(t, "team.abbreviation"); ev.HomeId = J.S(t, "team.id"); ev.HomeName = J.S(t, "team.displayName"); }
                    else { ev.Away = J.S(t, "team.abbreviation"); ev.AwayId = J.S(t, "team.id"); ev.AwayName = J.S(t, "team.displayName"); }
                }
                res.Add(ev);
            }
            return res;
        }

        static readonly Dictionary<string, GameData> Final = new Dictionary<string, GameData>();

        public static GameData Summary(string id, bool completed)
        {
            lock (Final) { GameData hit; if (completed && Final.TryGetValue(id, out hit)) return hit; }
            var json = Net.Cached(Espn.Base + "summary?event=" + id, C(@"espn\games\" + id + ".json"), completed ? (TimeSpan?)null : TimeSpan.FromMinutes(10));
            var g = Espn.ParseSummary(json);
            if (completed && !g.Completed)
            {   // cached before the final whistle: refresh once
                json = Net.Get(Espn.Base + "summary?event=" + id);
                File.WriteAllText(C(@"espn\games\" + id + ".json"), json, new UTF8Encoding(false));
                g = Espn.ParseSummary(json);
            }
            if (completed && g.Completed) lock (Final) Final[id] = g;
            return g;
        }

        public static List<GameData> CompletedBefore(DateTime d, DateTime seasonStart)
        {
            var res = new List<GameData>();
            for (var day = seasonStart; day < d.Date; day = day.AddDays(1))
                foreach (var e in Events(day).Where(x => x.Completed))
                    res.Add(Summary(e.Id, true));
            return res;
        }

        public static List<RosterEntry> Roster(string teamId, DateTime d)
        {
            var json = Net.Cached(Espn.Base + "teams/" + teamId + "/roster", C(@"espn\rosters\" + teamId + "_" + d.ToString("yyyyMMdd") + ".json"), TimeSpan.FromHours(6));
            return J.A(J.Parse(json), "athletes").Select(a => new RosterEntry { Pid = J.S(a, "id"), Name = J.S(a, "displayName"), Pos = J.S(a, "position.abbreviation"), Height = J.S(a, "height") }).ToList();
        }

        // ---------------- Action Network: odds across books ----------------
        public static List<AnGame> AnOdds(DateTime d)
        {
            string ds = d.ToString("yyyyMMdd");
            string json;
            try
            {
                json = OddsFile != null && File.Exists(OddsFile) ? File.ReadAllText(OddsFile)
                    : Net.Cached("https://api.actionnetwork.com/web/v2/scoreboard/nba?bookIds=15,30,68,69,71,75,79,123&date=" + ds + "&periods=event", C(@"an\odds_" + ds + ".json"), AgeFor(d));
            }
            catch (Exception e) { Log("Odds fetch failed: " + e.Message); return new List<AnGame>(); }
            var res = new List<AnGame>();
            foreach (var g in J.A(J.Parse(json), "games"))
            {
                var names = new Dictionary<string, string>();
                foreach (var t in J.A(g, "teams")) names[J.S(t, "id")] = J.S(t, "full_name");
                var ag = new AnGame { Id = J.S(g, "id"), HomeName = names.ContainsKey(J.S(g, "home_team_id")) ? names[J.S(g, "home_team_id")] : "", AwayName = names.ContainsKey(J.S(g, "away_team_id")) ? names[J.S(g, "away_team_id")] : "" };
                var markets = J.Get(g, "markets") as Dictionary<string, object>;
                if (markets != null)
                    foreach (var kv in markets)
                    {
                        var ev = J.Get(kv.Value, "event");
                        Func<string, string, string, double> pick = (mk, side, field) =>
                        {
                            foreach (var o in J.A(ev, mk)) if (J.S(o, "side") == side) return J.D(o, field);
                            return double.NaN;
                        };
                        string book; AnBooks.TryGetValue(kv.Key, out book);
                        var ml = new MarketLine
                        {
                            Book = book ?? ("book " + kv.Key), MlH = pick("moneyline", "home", "odds"), MlA = pick("moneyline", "away", "odds"),
                            SprH = pick("spread", "home", "value"), SprHOdds = pick("spread", "home", "odds"), SprAOdds = pick("spread", "away", "odds"),
                            Total = pick("total", "over", "value"), OverOdds = pick("total", "over", "odds"), UnderOdds = pick("total", "under", "odds")
                        };
                        foreach (var f in typeof(MarketLine).GetFields()) if (f.FieldType == typeof(double) && double.IsNaN((double)f.GetValue(ml))) f.SetValue(ml, 0.0);
                        ag.Books.Add(ml);
                    }
                res.Add(ag);
            }
            return res;
        }

        // Action Network player props (populated on game day). The schema could not be observed while the board was empty,
        // so this walks the JSON generically for entries carrying a player id, a line and American odds.
        public static List<PropLine> AnProps(string anGameId, string rawPath)
        {
            var res = new List<PropLine>();
            string json;
            try { json = Net.Get("https://api.actionnetwork.com/web/v2/games/" + anGameId + "/props?bookIds=15,68,69,71,75,79,123"); }
            catch { return res; }
            Directory.CreateDirectory(Path.GetDirectoryName(rawPath));
            File.WriteAllText(rawPath, json);
            var root = J.Parse(json);
            var players = new Dictionary<string, string>();
            var pl = J.Get(root, "players") as Dictionary<string, object>;
            if (pl != null) foreach (var kv in pl) { string nm = J.S(kv.Value, "full_name"); if (nm == "") nm = J.S(kv.Value, "display_name"); players[kv.Key] = nm; }
            var props = J.Get(root, "player_props") as Dictionary<string, object>;
            if (props == null) return res;
            foreach (var mk in props)
            {
                string stat = StatFromMarket(mk.Key);
                if (stat == null) continue;
                var entries = new List<Dictionary<string, object>>();
                Collect(mk.Value, entries);
                foreach (var grp in entries.GroupBy(e => Str(e, "player_id") + "|" + Str(e, "book_id") + "|" + Str(e, "value", "line")))
                {
                    var f = grp.First();
                    string pid = Str(f, "player_id");
                    var p = new PropLine { Player = players.ContainsKey(pid) ? players[pid] : pid, Stat = stat, Book = AnBooks.ContainsKey(Str(f, "book_id")) ? AnBooks[Str(f, "book_id")] : Str(f, "book_id"), Line = U.D(Str(f, "value", "line"), double.NaN) };
                    foreach (var e in grp)
                    {
                        string side = (Str(e, "side") + Str(e, "option_type")).ToLowerInvariant();
                        double odds = U.D(Str(e, "odds", "money"), double.NaN);
                        if (side.Contains("over")) p.OverOdds = odds; else if (side.Contains("under")) p.UnderOdds = odds;
                    }
                    if (!double.IsNaN(p.Line) && (!double.IsNaN(p.OverOdds) || !double.IsNaN(p.UnderOdds))) res.Add(p);
                }
            }
            return res;
        }
        static string Str(Dictionary<string, object> d, params string[] keys) { foreach (var k in keys) { object v; if (d.TryGetValue(k, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture); } return ""; }
        static void Collect(object o, List<Dictionary<string, object>> acc)
        {
            var d = o as Dictionary<string, object>;
            if (d != null) { if (d.ContainsKey("player_id") && (d.ContainsKey("value") || d.ContainsKey("line"))) acc.Add(d); foreach (var v in d.Values) Collect(v, acc); return; }
            var a = o as object[]; if (a != null) foreach (var v in a) Collect(v, acc);
        }
        static string StatFromMarket(string k)
        {
            k = k.ToLowerInvariant();
            if (k.Contains("rebounds") && k.Contains("assists") && k.Contains("points")) return "PRA";
            if (k.Contains("three") || k.Contains("3pt")) return "3PM";
            if (k.Contains("points") && !k.Contains("rebounds") && !k.Contains("assists")) return "PTS";
            if (k.Contains("rebounds") && !k.Contains("points") && !k.Contains("assists")) return "REB";
            if (k.Contains("assists") && !k.Contains("points") && !k.Contains("rebounds")) return "AST";
            return null;
        }

        // ---------------- Basketball-Reference ----------------
        public static string BrefCsv(string season, string kind, bool refreshDaily)
        {
            string csv = Path.Combine(Root, "data", "bbref", "NBA_" + season + "_" + kind + ".csv");
            if (File.Exists(csv) && (!refreshDaily || DateTime.Now - File.GetLastWriteTime(csv) < TimeSpan.FromHours(20))) return csv;
            try
            {
                string html = Net.Get("https://www.basketball-reference.com/leagues/NBA_" + season + "_" + kind + ".html");
                File.WriteAllLines(csv, ParseBrefTable(html, null), new UTF8Encoding(false));
            }
            catch (Exception e) { Log("Basketball-Reference " + season + " " + kind + " unavailable (" + e.Message + "); using cached copy if present."); }
            return csv;
        }

        public static List<string> ParseBrefTable(string html, string tableId)
        {
            html = html.Replace("<!--", "").Replace("-->", "");
            int start = tableId == null ? 0 : html.IndexOf("id=\"" + tableId + "\"", StringComparison.Ordinal);
            int tb = html.IndexOf("<tbody>", Math.Max(0, start), StringComparison.Ordinal);
            int te = html.IndexOf("</tbody>", tb, StringComparison.Ordinal);
            string body = html.Substring(tb, te - tb);
            var outp = new List<string>(); List<string> cols = null;
            foreach (Match r in Regex.Matches(body, "<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline))
            {
                var cells = Regex.Matches(r.Groups[1].Value, "<t[dh][^>]*data-stat=\"([^\"]+)\"[^>]*>(.*?)</t[dh]>", RegexOptions.Singleline);
                if (cells.Count < 5) continue;
                var idm = Regex.Match(r.Groups[1].Value, "data-append-csv=\"([^\"]+)\"");
                var names = new List<string> { "bbref_id" }; var vals = new List<string> { idm.Groups[1].Value };
                foreach (Match c in cells)
                {
                    names.Add(c.Groups[1].Value);
                    vals.Add(System.Net.WebUtility.HtmlDecode(Regex.Replace(c.Groups[2].Value, "<[^>]+>", "")).Replace(",", " ").Trim());
                }
                if (cols == null) { cols = names; outp.Add(string.Join(",", tableId == null ? cols : cols.Skip(1))); }
                if (vals.Count > 2 && (vals[1] == "Rk" || vals[2] == "Player")) continue;
                outp.Add(string.Join(",", tableId == null ? vals : vals.Skip(1)));
            }
            return outp;
        }
    }
}
