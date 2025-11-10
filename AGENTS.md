# Agent Notes for Forklift

- This repo targets .NET 9; use `dotnet --info` if you need to confirm SDK availability.
- The main executable lives in `Forklift.ConsoleClient`. Publish self-contained binaries with `dotnet publish Forklift.ConsoleClient/Forklift.ConsoleClient.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true` when mimicking CI.
- Use `rg` for searching and avoid `ls -R` to keep command output manageable.
- When editing GitHub Actions, remember workflows run on Ubuntu and should avoid repository-relative absolute paths such as `/home/runner/...`; stick to relative paths from the repo root.
- Local cutechess/Ordo tooling can be installed via `./scripts/install-chess-tools.sh`—this is what CI uses, so keeping it functional keeps the workflow healthy.
