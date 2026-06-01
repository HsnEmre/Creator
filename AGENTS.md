\# AGENTS.md



\## Project Context



This project is an AI video generation platform.



Architecture:

\- ASP.NET Core API

\- SQL Server database

\- Python video worker

\- FFmpeg-based media processing

\- Wan2.2-TI2V-5B video generation

\- SDXL base 1.0 image/reference generation



\## Hard Constraints



\- Keep ASP.NET Core API architecture.

\- Keep SQL Server.

\- Do not add Redis.

\- Do not add Docker.

\- Do not add PostgreSQL.

\- Do not add SQLite.

\- Do not add ComfyUI.

\- Do not move media processing into ASP.NET Core.

\- Do not change Wan2.2 rendering logic unless explicitly requested.

\- Python worker owns FFmpeg, video assembly, muxing, finalization, and inference orchestration.



\## Current Known Issue



A 9-second assembled video currently takes around 48 minutes to render on the local machine.



Current hardware:

\- AMD Ryzen 9 9900X

\- RTX 5080 16GB

\- 64GB DDR5 RAM

\- NVMe SSD



This render time is too slow. Optimization must be based on diagnostics, not guessing.



\## Required Codex Behavior



Before editing:

\- Inspect the relevant files.

\- Explain the suspected root cause.

\- Identify which part of the pipeline is affected.



When editing:

\- Make small, reviewable changes.

\- Preserve existing API endpoints.

\- Preserve existing database schema unless explicitly requested.

\- Preserve existing job flow.

\- Do not rewrite the whole project.

\- Do not introduce unrelated libraries or infrastructure.



After editing:

\- List changed files.

\- Explain what changed.

\- Run Python compile checks when Python files change.

\- Run .NET build checks when ASP.NET files change.

\- Include exact commands used for verification.

\- Include important log examples if logging was changed.



\## Python Worker Rules



\- FFmpeg operations belong in the Python worker.

\- Video duration must be preserved during finalize/mux.

\- If audio is shorter than video, audio must be padded or video must finalize without being trimmed.

\- Never use ffmpeg behavior that cuts video duration to match shorter audio unless explicitly requested.



\## Performance Investigation Rules



When investigating render performance, collect diagnostics first:

\- total job duration

\- model load duration

\- SDXL/reference generation duration

\- Wan pipeline initialization duration

\- per-shot render duration

\- frame count

\- output resolution

\- sample steps

\- guidance/CFG settings

\- dtype

\- device

\- CUDA availability

\- VRAM usage if available

\- video assembly duration

\- mux/finalize duration



Do not change generation quality settings until diagnostics show the bottleneck.

