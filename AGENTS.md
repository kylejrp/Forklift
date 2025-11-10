# Repository Guidelines

## Project Layout

- `Forklift.Core`: engine primitives and baked tables.
- `Forklift.Analyzers`: repo Roslyn diagnostics.
- `Forklift.ConsoleClient` / `Forklift.TestConsoleApp`: UCI console hosts.
- `Forklift.Testing`: xUnit suite; TRX in `TestResults/`.
- `Forklift.Benchmark`: BenchmarkDotNet harness; reports in `BenchmarkDotNet.Artifacts/` + `.eval/reports/`.

## Build & Test Commands

- `dotnet restore` – restore NuGet packages.
- `dotnet build -c Release` – compile solution; FLK00x warnings fail the build.
- `dotnet test Forklift.Testing/Forklift.Testing.csproj --logger "trx;LogFileName=TestResults.trx"` – execute tests and capture TRX.
- `dotnet test ... --filter <expr>` – run targeted subsets.
- `dotnet run --project Forklift.ConsoleClient` – launch the UCI console (`--help` lists switches).
- `pwsh Forklift.Benchmark/Run-Benchmark.ps1 -ArtifactsRoot C:\ForkliftArtifacts -Suite minimal` – compare `BaselineGitRef` vs `CandidateGitRef` via temp worktrees before BenchmarkDotNet; override refs or paths as needed. Don't put `ArtifactsRoot` inside the repo folder.
- `dotnet run --project Forklift.Benchmark -c Release` – run single-ref benchmarks.

## Coding Style

Follow idiomatic C# 11: 4-space indentation, file-scoped namespaces, expression-bodied helpers only when clearer. PascalCase types/public members, camelCase locals/parameters, `_camelCase` private readonly fields. Prefer `var` when intent is obvious. Use descriptive names (e.g., `isCapture`, not `ic`). Nullable references and implicit usings are enabled—resolve new diagnostics, especially FLK001–FLK003.

## Testing Practice

Add tests in `Forklift.Testing` beside related behavior with class names `<Feature>Tests` and methods like `GeneratesEnPassantCapture`. Use `TestHelpers`, keep perft baselines aligned, run filtered subsets during iteration, ensure new tests fail first, then run the full suite before committing.

## Environment Guardrails

In Codex Web or Codex Cloud, skip long benchmarks and full suites—stick to targeted checks and reserve heavy runs for confirmed local shells like Codex CLI or defer to the user/continuous integration.

## Commits & Pull Requests

Keep commits building and testing clean with short imperative subjects (e.g., `Refine move buffer handling`). PR descriptions must recap conversation requirements, note tradeoffs and rejected options, include pre-fix failing output, attach perf or visual evidence when relevant, and keep unrelated engine, benchmark, and infrastructure work separate.

## Comments & Documentation

Favor XML doc comments (e.g., `<summary>`) on public APIs and add inline comments only for intent or invariants code cannot express. Skip narration or “new code” markers—comments should help maintainers.

## Maintaining This Guide

Agents may update this file when guidance helps most contributors—context tokens are expensive. Remove or relocate stale information rather than letting it drift.

## Analyzer & Configuration Notes

Set `FKLIFT_BAKE=true` when regenerating `EngineTables.Baked.cs`. BenchmarkDotNet artifacts grow quickly, so confirm `.gitignore` coverage. Respect hints in `.config` and `.vscode` to match CI.
