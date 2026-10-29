# PackOptimizerBench

Headless runner for `PackTextureAtlasBatchService.Process`, to measure the batch tool's runtime and to check that an optimisation leaves its output unchanged.

Run (Release, from the repo root):

    dotnet build Tools\PackOptimizerBench -c Release
    dotnet Tools\PackOptimizerBench\bin\Release\net10.0-windows\PackOptimizerBench.dll --tag run1

It loads the WH3 packs from `K:\SteamLibrary\steamapps\common\Total War WARHAMMER III\data` and the source pack `K:\wh3mods\albion\!ak_teb3.pack` (read-only), runs `Process` with the last real run's options (atlasing off; cross-rigid, merge, geometry, downscale, repack, compaction, drop unread alpha on), and writes `bench_output.pack`, the report, `fingerprint.txt` (SHA-256 of every output file plus the report without timing lines) and `wall_time.txt` to `K:\projects\assedfork-bench\<tag>\`. Options: `--source`, `--game-dir`, `--out`, `--tag`, `--report-diagnostics true|false` (default true; false skips the report-only audits). Diff two `fingerprint.txt` files to check a change.
