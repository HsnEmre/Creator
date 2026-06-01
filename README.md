# VideoStudio

Production-oriented AI video generation monorepo skeleton with an ASP.NET Core API, SQL Server metadata storage, and Python video workers.

The ASP.NET Core API owns projects, planning, metadata, and a SQL Server-backed render-job queue. It never runs GPU inference. Python workers poll the API and are the only process responsible for Wan2.2 adapters, FFmpeg operations, and future GPU work.

## Structure

```text
backend/VideoStudio.Api        ASP.NET Core Web API
frontend/VideoStudio.Web       React/Vite frontend
workers/video-worker           Python render worker
storage/assets                 Uploaded/source assets
storage/renders                Shot render outputs
storage/audio                  Generated/imported audio
storage/finals                 Final video outputs
docs                           Architecture notes
```

## Development Setup

1. Start Ollama and make sure the configured model is available.

   ```powershell
   ollama serve
   ollama pull llama3.1
   ```

2. Restore .NET packages.

   ```powershell
   cd backend/VideoStudio.Api
   dotnet tool restore
   dotnet restore
   ```

3. Create the initial migration if it is not already present.

   ```powershell
   dotnet ef migrations add InitialCreate
   ```

4. Create/update the SQL Server LocalDB database.

   ```powershell
   dotnet ef database update
   ```

5. Run the API.

   ```powershell
   dotnet run
   ```

6. Run the Python worker in another terminal.

   ```powershell
   cd workers/video-worker
   python main.py
   ```

   On Windows, use `py -3.9 main.py` if `python` is not on PATH.

## Local development: start all services

From the repository root, launch API + frontend + worker together:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\dev-start.ps1
```

This starts three separate PowerShell windows:

- API: `http://localhost:5281/swagger`
- Frontend: `http://localhost:5173`
- Worker: polls `VIDEO_API_BASE_URL=http://localhost:5281`

To stop launcher-started windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\dev-stop.ps1
```

## Visual Studio guidance

- Open `VideoStudio.slnx` in Visual Studio.
- API-only debugging: start `VideoStudio.Api`.
- Full-stack local development: start `VideoStudio.DevLauncher`. It calls `.\scripts\dev-start.ps1`, which opens separate windows for the API, React/Vite frontend, and Python worker.
- Stop launcher-started windows with `powershell -ExecutionPolicy Bypass -File .\scripts\dev-stop.ps1`.
- The Python worker remains a separate process by design and should not be hosted inside the API process.

## Development modes

- Mode A (Visual Studio API-only): start `VideoStudio.Api` for backend work and Swagger testing.
- Mode B (Visual Studio full stack): start `VideoStudio.DevLauncher` to run API + React web + Python worker together.
- Mode C (terminal full stack): use `.\scripts\dev-start.ps1` directly.

For real local Wan2.2 TI2V renders, configure `workers/video-worker/.env` from `.env.example`:

- `WAN22_PYTHON_EXE=C:/AI/Wan2.2/.venv/Scripts/python.exe`
- `WAN22_REPO_DIR=C:/AI/Wan2.2`
- `WAN22_TI2V_CKPT_DIR=C:/AI/models/Wan2.2-TI2V-5B`
- `WAN22_OUTPUT_DIR=C:/Users/USER/Desktop/Creator/storage/renders`

The worker launches Wan2.2 directly through subprocess. ComfyUI is not used.

## Notes

- The SQL Server connection string can be overridden with standard ASP.NET Core configuration, for example `ConnectionStrings__DefaultConnection`.
- Planning flow: Story -> Ollama ProductionPlan JSON -> Characters/Scenes/Shots -> Visual Preparation -> RenderJobs -> Python Worker -> Wan2.2 Adapter -> FFmpeg Finalizer.
- MagicLight-style visual preparation is now explicit:
  - `POST /api/projects/{id}/preproduction/prepare` fills character reference image prompts and shot start image/keyframe prompts.
  - Character prompts describe stable identity on a neutral cinematic background.
  - Shot start image prompts describe the full scene composition for later image-to-video.
  - `POST /api/projects/{id}/visuals/generate-character-references` and `/visuals/generate-shot-start-images` create database-backed image generation jobs.
  - The Python worker can generate these images locally with SDXL. No external image generation API is used.
- Director workflow: analyze the story, edit scenes/shots, upload shot start images, render selected shots or scenes, assemble completed shots into `assembled.mp4`, then finalize with audio into `final-preview.mp4`.
- Ollama is used only for planning and prompt generation.
- Wan2.2 is not an Ollama model. Local Wan2.2 rendering is invoked by the Python worker adapter.
- The ASP.NET Core API never runs GPU inference.
- Local Windows renders are preview-speed: without `flash_attn`, Wan2.2 typically falls back to SDPA and is significantly slower. Current defaults prioritize stability (`1280*704`, `frame_num=49`, `sample_steps=10`).
- Local runtime model uses three processes: API (ASP.NET Core), Web (React/Vite), and Worker (Python/Wan2.2 + TTS + FFmpeg).
- Render presets:
  - `FastPreview` (default): `1280*704`, `frame_num=25`, `sample_steps=5`
  - `Preview`: `1280*704`, `frame_num=49`, `sample_steps=10`
  - `Final`: `1280*704`, `frame_num=121`, `sample_steps=25`
- `FastPreview` is recommended for integration testing (`maxShots=1`). `Final` is intentionally slower and should be used later.
- The project detail page includes a practical scene/shot editor. You can patch scene metadata, edit shot prompts and camera/motion notes, select shots, render selected shots, render a full scene, or queue the whole project.
- Character consistency is improved by prompt compilation but not fully solved in prompt-only mode.
- Character reference images can be uploaded from the frontend per character. They guide identity in compiled prompts; they are not passed directly to Wan2.2 as `--image`.
- Wan2.2 `--image` is treated as a shot start image / scene keyframe. Upload shot start images per shot and render with `useShotStartImage=true` to create `ImageToVideo` jobs.
- Visual-prep generated or uploaded shot start images are the bridge from prompt-only T2V to more controlled I2V. If no shot start image exists, rendering stays in Text-to-Video mode.
- Local image generation:
  - Default provider: `IMAGE_MODEL_PROVIDER=SDXL`
  - Model path: `SDXL_MODEL_PATH=C:/AI/models/sdxl-base-1.0`
  - Output root: `IMAGE_OUTPUT_DIR=C:/Users/USER/Desktop/Creator/storage/assets`
  - Recommended preview defaults: `SDXL_WIDTH=1280`, `SDXL_HEIGHT=704`, `SDXL_NUM_INFERENCE_STEPS=20`, `SDXL_GUIDANCE_SCALE=7.0`
  - Set `IMAGE_MODEL_PROVIDER=PLACEHOLDER` to avoid loading SDXL and test the queue/UI path with placeholder PNGs.
  - Planned image providers are represented as config names (`FLUX_DEV`, `FLUX_KONTEXT`, `HIDREAM`) and fail clearly until adapters are implemented.
  - SDXL VRAM use depends on GPU, driver, and offload settings. `SDXL_ENABLE_CPU_OFFLOAD=true` is recommended for local development if memory is tight.
- For best I2V results, upload a scene-like `1280x704` image with the character already placed in the desired environment and composition. Portrait-only references may produce static portrait videos if used as start frames.
- Audio pipeline:
  - Dialogue lines are persisted in SQL Server.
  - `POST /api/projects/{id}/audio/generate` creates `GenerateAudio` jobs.
  - Python worker generates audio via Edge TTS (`edge-tts`).
  - `POST /api/projects/{id}/finalize` creates `MuxAudio` jobs for FFmpeg muxing.
  - Wan2.2 remains video-only; TTS and mux are separate stages.
- Assembly pipeline:
  - `POST /api/projects/{id}/assemble` creates an `AssembleVideo` job.
  - The Python worker stitches completed shot renders in scene/shot order with FFmpeg.
  - `POST /api/projects/{id}/finalize` prefers `storage/finals/{projectId}/assembled.mp4` when available, then muxes audio into `final-preview.mp4`.
- Generated media files are served by the API for browser playback:
  - `/media/renders/{projectId}/{fileName}`
  - `/media/audio/{projectId}/{fileName}`
  - `/media/finals/{projectId}/{fileName}`
  - `/media/assets/{projectId}/characters/{characterId}/{fileName}`
  - `/media/assets/{projectId}/shots/{shotId}/{fileName}`
  Frontend players should use these URLs instead of local `C:\...` paths.
