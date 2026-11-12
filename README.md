# NBA Quant Desk

A self-contained Windows app (no Python or installs needed; uses the .NET Framework that ships with Windows) that builds an NBA slate report from live data, simulates every game 100,000 times, saves every prediction, grades it after the games, and refits itself from the results.

## Daily loop

1. **Double-click `NbaQuant.exe`.** It opens on the next slate date.
2. **Edit injuries** (optional, recommended): opens `overrides\injuries_YYYY-MM-DD.csv`. It is pre-filled from ESPN. Correct it against the official report (button **Official injury report**), save, then run.
3. **Run slate.** Fetches the schedule, rosters, injuries, odds (DraftKings + 5 books), player props (when posted), simulates, and writes `reports\NBA_Quant_Report_YYYY-MM-DD.xlsx`. The tabs **Moneylines**, **Top picks**, **Standouts** (projection vs season form, with reasons), **DD & 3PT** (double-double and three-point props) and **Boost SGP** show the highlights; the boost calculator takes the price DraftKings displays.
4. **Next morning: Grade results** with the slate date selected. Every saved prediction is resolved from ESPN box scores and play-by-play and added to `ledger\`. The **Track record** tab and the report's Track Record sheet update.
5. **Recalibrate** (weekly is plenty): reruns a walk-forward backtest over every completed game this season and refits the variance settings in `settings.ini`. Details land in `ledger\recalibration.txt` and the report's Method sheet.

Re-run close to tip-off: injuries and lines move, and props usually post on game day.

## Command line

`nbaquant-cli.exe` does the same without the window:

```
nbaquant-cli run next            # next slate, 100,000 sims
nbaquant-cli run 2026-10-28 --sims 50000
nbaquant-cli grade 2026-10-28
nbaquant-cli recalibrate
nbaquant-cli validate                         # walk-forward backtest with current settings (Brier by market, early vs late dates)
nbaquant-cli validate --set Sigma3=0.15       # same, testing an alternative setting (saved as ledger\backtest_Sigma30p15.csv)
nbaquant-cli audit                            # reports\Model_Audit_DATE.xlsx: where the model works and fails
nbaquant-cli sgp4 2026-10-29 CLE@ATL          # four-leg parlays in three risk bands for one game
nbaquant-cli live 2026-10-28 LAL@LAC          # re-simulate the rest of an in-progress game
nbaquant-cli bank                             # rebuild the 2026-27 data bank (also done by every run)
nbaquant-cli review 2026-10-28                # game-by-game right/wrong review (also built by grade)
nbaquant-cli site 2026-10-29                  # website data only (site\data.js); every run also writes it
nbaquant-cli ban CLE: unreliable              # banned list: the team's games are never simulated or priced
nbaquant-cli unban CLE                        # bring a team back
nbaquant-cli banned                           # watch report for banned teams (also ledger\banned_watch.csv)
nbaquant-cli trends                           # season trends study (also ledger\trends.txt)
```

## Favorites, trend learning and the season study

**Favorite lines.** For every player and stat the model names one favorite: the highest rung of a DraftKings-style ladder (points 5/10/15..., rebounds and assists in twos, threes in ones) that he clears in at least 60% of the simulations he plays in (`FavMin`). It is not the safest line and not the single most common value; it is the boldest line the simulations still back. The website shows the projection (the average across simulations, so player lines add up to the team score), the favorite under it, and the full ladder on demand. Every favorite is saved as a `FAV` prediction and graded, so its record builds from day one.

**Trend learning.** After each game the projection is compared with what the player did, and the gap is carried into his next projection:

| Setting | What it does |
|---|---|
| `MinTrend` | closes this share of the gap between his projected minutes and his last-three-game average |
| `LearnPtsK` | scales his scoring rate by (actual + K) / (expected at the minutes he played + K) over his graded games |
| `LearnAstK` | the same for assists |

Rates are judged at the minutes he actually played, so a blowout or foul trouble does not read as a slump. Each rule is off until the walk-forward backtest shows it helps both halves of the season, and Recalibrate re-tests each one every morning.

**Season trends** (`trends`, and the Trends tab): team trajectories (last five against the games before), players who are moving, with/without splits for every regular who has missed games, records that are still perfect and how often perfect records held up the next game, how much of a recent stretch carried over, and referee tendencies. It is information only and feeds nothing into the simulation.

**Last night** (the Last night tab): the latest graded slate, game by game: the pick and the result, comebacks and quarter swings, three-point shooting against each team's season norm, free throws and fouls, technicals, flagrants and ejections, players whose minutes were cut short, and which player calls and tickets hit.

Banned teams (`overrides\banned_teams.csv`) are left out of every simulation: slates, four-leg and custom tickets, stress tests, live re-sims and the walk-forward backtests behind recalibration. Their games against everyone are skipped, since a game can't be simulated without both teams. Their information is still collected (scores, box scores, DraftKings lines, injury reports, the data bank), and the website and the `banned` report track their record, cover rate and margin against the DraftKings spread, so a trend shows before a team is brought back. In the cloud, run the `ban` or `unban` task with the team (for example `CLE: reason`). Every change is logged in `ledger\ban_log.csv`.

## Running in the cloud (GitHub Actions)

Smart App Control blocks unsigned home-built programs, so the app runs on GitHub's Windows machines instead (`.github\workflows\daily.yml`, `cloud\daily.ps1`):

| When (ET, daylight time) | Task | What it does |
|---|---|---|
| 11:00 AM | learn | grades yesterday, builds the game review, recalibrates on every completed game, backtests, audits, refreshes the data bank |
| 6:40 PM, 7:40 PM, 9:40 PM | slate | reads the official NBA injury report, prices tonight's games still to tip, four-leg tickets, publishes the website |

Results are committed back to the repository (reports, predictions, ledger, settings) and the website is published with GitHub Pages. Run a task by hand from the Actions tab (Run workflow), including `test` to backtest settings such as `RatePriorMin=300; GammaOverride=1.0` before adopting them.

The website lives in `site\`: `index.html` reads `data.js`, which every `run` rewrites. To preview it locally, run `site\serve.ps1` and open http://localhost:8765.

The audit splits every backtest and live prediction by player role, projected minutes, game script, rest, absences and sample size, and compares skill against the base rate for the same line. Its "Misses" sheet shows what went wrong on confident calls (usually minutes, not shooting).

## Folders

| Folder | What's in it |
|---|---|
| `reports\` | Excel reports (formulas recalculate; yellow cells are inputs) |
| `predictions\` | Every published prediction, machine-readable |
| `ledger\` | Graded predictions, backtest output, recalibration notes |
| `overrides\` | Per-date injury corrections you control |
| `data\` | Cached ESPN / Action Network / Basketball-Reference data |
| `settings.ini` | Model parameters (market weight, variance settings, sims) |
| `src\` | C# source; rebuild with `build.bat` |

## Honest limits

- Injury statuses come from ESPN unless you override them; ESPN can lag the official report.
- DraftKings same-game-parlay prices and boost terms can't be read; type the displayed price into the calculator.
- Player-prop lines appear only after books post them on the Action Network feed.
- The model is still being validated: treat edges under ~5% as noise and judge it by the Track Record, not by one night.
