# Forklift Chess Engine

Forklift is a UCI-compatible chess engine implemented in .NET. This repository contains the core engine, benchmarking harnesses, testing utilities, and automation to help evaluate strength improvements over time.

## Automated Elo Evaluation

The repository ships a GitHub Actions workflow named **Elo Evaluation** (`.github/workflows/elo-eval.yml`) that automates nightly and on-push engine self-play using [cutechess-cli](https://github.com/cutechess/cutechess) and [Ordo](https://github.com/michiguel/Ordo).

### Running the workflow

- **Manual trigger**: Visit the repository's *Actions* tab, choose **Elo Evaluation**, and click **Run workflow** to start an immediate match between the checked-out commit and the latest commit on `main` (or the previous commit if you are already on `main`).
- **Automatic triggers**: The workflow also runs on every push to `main` and nightly at 03:00 UTC.

### What the workflow produces

- Two self-contained Linux binaries (`artifacts/current/engine` and `artifacts/previous/engine`) built from the current commit and the baseline commit taken from `main`.
- A cutechess match of at least 100 games (or early SPRT stop) using a curated openings set and UCI protocol.
- Ordo rating estimates anchored to the baseline build for easier trend tracking.
- Uploaded artifacts containing:
  - `matches/latest-vs-previous.pgn` (all played games)
  - `matches/logs/` (cutechess logs and raw output)
  - `matches/ratings.txt` and `matches/ratings.csv` (Ordo summaries)
  - `matches/tool-versions.txt` (exact cutechess-cli and Ordo versions)

Artifacts are accessible from the workflow run summary in GitHub Actions. Download them to inspect individual games or the rating tables.

### Tuning the evaluation

- **SPRT**: Pass a different `-Sprt` argument to `scripts/elo-eval.ps1` (or tweak the workflow invocation) to tighten or loosen the acceptance bounds. For example, increase `elo1` to demand a larger Elo gain before early acceptance.
- **Time control**: Supply a custom `-TimeControl` argument such as `'3+1'` for slower games. The workflow defaults to `1+0.1` blitz time but any cutechess-compatible control works.
- **Openings**: Replace `matches/openings.epd` with a richer book or point the script at another file via the `-OpeningsFile` parameter. The PowerShell helper will download a lightweight suite if the file is missing.

### Understanding the results

- SPRT status in the job summary indicates whether the test met its stop conditions (accept, reject, or continue).
- Ordo's Elo difference is only as reliable as the number of games and the draw rate—expect wide error bars for small sample sizes. The workflow warns when fewer than 10 games are played.
- Because the workflow anchors the baseline build at 2500 Elo, the reported ratings are relative changes across runs rather than absolute playing strength.


### Run Elo Evaluation Locally (PowerShell)

The same PowerShell script that powers CI also runs locally on Windows, macOS, and Linux. A few common invocations:

```powershell
# Compare HEAD against its parent using the defaults
pwsh ./scripts/elo-eval.ps1 -VsParent -Games 300 -TimeControl '1+0.1'

# Compare against a specific reference (branch, tag, or commit)
pwsh ./scripts/elo-eval.ps1 -VsRef v0.3.1 -Games 400 -TimeControl '2+0.2'

# On Linux, reuse the CI installer for cutechess-cli/Ordo via apt
$env:USE_APT_CUTECHESS = '1'
pwsh ./scripts/elo-eval.ps1 -VsParent
```

On Linux CI the script automatically calls `./scripts/install-chess-tools.sh`; on Windows you can add `-InstallTools` to attempt downloading a portable cutechess build if one is not already on `PATH`. Artifacts land under `matches/`, mirroring the GitHub Action layout.

For local experimentation, run `./scripts/install-chess-tools.sh` (Linux/macOS) or `pwsh ./scripts/elo-eval.ps1 -InstallTools` (Windows) to provision cutechess-cli and Ordo before executing a match.
