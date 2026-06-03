# Render Pipeline

## Overview

This project is an AI video generation platform built with:

* ASP.NET Core API
* SQL Server
* Python video worker
* Ollama for story analysis and planning
* SDXL base 1.0 for local image/reference generation
* Wan2.2-TI2V-5B for video rendering
* FFmpeg for assembly, muxing, and finalization

ASP.NET Core owns API, project state, and database operations.
The Python worker owns ML inference, image generation, video rendering, assembly, audio muxing, and final media finalization.

ComfyUI is not used anywhere in this pipeline.

---

## Project and Story Flow

1. A project is created through `POST /api/projects` with `title`, optional `storyText`, and `targetDurationSeconds`.

2. The story is saved through `POST /api/projects/{id}/story`.

3. `POST /api/projects/{id}/analyze` sends the story to Ollama and asks for strict `ProductionPlan` JSON.

4. The API parses, validates, normalizes, and persists:

   * Characters
   * Scenes
   * Shots
   * Dialogue
   * Audio cues

5. `GET /api/projects/{id}/production-plan` reconstructs the saved production plan from SQL Server.

6. `POST /api/projects/{id}/preproduction/prepare` creates or refreshes MagicLight-style visual preparation prompts:

   * Character reference image prompts in English
   * Character reference negative prompts
   * Shot start image / keyframe prompts in English
   * Shot start image negative prompts

7. The frontend lets the user review, edit, copy, generate, or manually upload these visual assets before rendering.

---

## Local Image Generation

8. Optional image generation endpoints create database-backed jobs:

   * `POST /api/projects/{id}/visuals/generate-character-references`
   * `POST /api/projects/{id}/visuals/generate-shot-start-images`

9. The Python worker handles `GenerateCharacterReferenceImage` and `GenerateShotStartImage` through an image-model adapter.

10. The first real local adapter is SDXL through diffusers. `PLACEHOLDER` remains available for queue and UI testing.

11. Image generation runs only in the Python worker.

Current SDXL settings:

```env
IMAGE_MODEL_PROVIDER=SDXL
SDXL_MODEL_PATH=C:/AI/models/sdxl-base-1.0
SDXL_WIDTH=1280
SDXL_HEIGHT=704
SDXL_NUM_INFERENCE_STEPS=20
SDXL_GUIDANCE_SCALE=7.0
SDXL_ENABLE_CPU_OFFLOAD=true
IMAGE_OUTPUT_DIR=C:/Users/USER/Desktop/Creator/storage/assets
```

Generated character references are saved as:

```text
storage/assets/{projectId}/characters/{characterId}/reference.png
```

Generated shot start images are saved as:

```text
storage/assets/{projectId}/shots/{shotId}/start.png
```

Use `IMAGE_MODEL_PROVIDER=PLACEHOLDER` to test the full workflow without loading SDXL.

Future provider names such as `FLUX_DEV`, `FLUX_KONTEXT`, and `HIDREAM` are reserved and should fail with a clear “not implemented yet” message until adapters are added.

---

## Render Job Creation

12. `POST /api/projects/{id}/render` creates one `RenderJob` per saved shot with `Pending` status.

13. Render creation supports presets and shot limits:

| Preset        | Resolution | Frame Count | Sample Steps |
| ------------- | ---------: | ----------: | -----------: |
| `FastPreview` | `1280x704` |        `25` |          `5` |
| `Preview`     | `1280x704` |        `49` |         `10` |
| `Final`       | `1280x704` |       `121` |         `25` |

14. `maxShots`, `sceneIndex`, and `shotIndex` can limit queueing to a subset for quick iteration.

15. Render targeting supports:

* selected `shotIds`
* one scene
* one shot
* broader project-wide queues

16. Scene and shot metadata can be edited after analysis. Scene order is read by `sceneIndex`; shot order is read by `shotIndex`.

---

## Worker Job Polling

17. The Python worker calls `POST /api/worker/jobs/next` every two seconds.

18. The API marks the oldest pending job as `Rendering` and returns its payload, including render preset settings.

19. Worker progress updates are posted to the API at key stages:

* `5`: accepted
* `10`: subprocess start
* `90`: subprocess exit
* `100`: output verified

20. The worker calls complete, fail, or progress endpoints.

---

## Wan2.2 Video Rendering

21. The worker renders through the selected Python Wan2.2 adapter.

22. For `TextToVideo`, the worker launches local Wan2.2 with subprocess:

* script: `generate.py`
* task: `ti2v-5B`
* output: `storage/renders/{projectId}/{renderJobId}.mp4`

23. Prompt-only T2V can drift on faces and costumes.

24. Character reference images can be uploaded per character and served from:

```text
/media/assets/{projectId}/characters/{characterId}/reference.ext
```

25. Character reference images are identity guidance for prompt compilation and UI preview.

26. Character reference images are not passed directly to Wan2.2 as `--image`.

27. Wan2.2 `--image` is treated as a shot start image / scene keyframe.

28. Shot start images can be uploaded per shot and served from:

```text
/media/assets/{projectId}/shots/{shotId}/start.ext
```

29. When `POST /api/projects/{id}/render` is called with `useShotStartImage=true` and a shot has `StartImagePath`, the API sets `RenderJob.InputImagePath` and marks the job as `ImageToVideo`.

30. For image-to-video jobs, the Python Wan2.2 TI2V adapter passes `--image {inputImagePath}` to `generate.py` while keeping the same `ti2v-5B` task.

31. If only character references exist and no shot start image exists, the render remains `TextToVideo`.

32. `useCharacterReference` is kept as a backward-compatible alias for `useCharacterReferenceInPrompt`.

33. For best I2V results, upload a scene-like `1280x704` image with the character already placed in the desired environment and composition.

34. Portrait-only references may produce static portrait videos if used as start frames.

### Wan2.2 Performance Diagnostics and Tuning

When worker performance diagnostics are enabled with `VIDEO_WORKER_PERF_LOG=true`, the worker sets `WAN_PERF_LOG=1` for the local Wan2.2 subprocess. Wan2.2 then emits grep-friendly timing lines:

```text
[wan-perf] event=vae_decode_chunk_completed ...
[wan-perf] event=vae_decode_concat_completed ...
[wan-perf] event=vae_decode_internal_summary ...
```

Deep per-chunk VAE profiling can add CUDA synchronization overhead. It should only be enabled with `WAN_DEEP_VAE_PROFILE=1` during targeted local tests. Normal `VIDEO_WORKER_PERF_LOG=true` diagnostics should remain coarse enough to avoid materially changing render timing.

Optional runtime switches are available for local A/B testing. Leave them unset or `false` to preserve current behavior:

| Setting | Default | Purpose |
| ------- | ------- | ------- |
| `WAN22_TORCH_OPTIMIZE=true` | `false` | Passes `WAN_TORCH_OPTIMIZE=1` to Wan2.2 so `generate.py` enables TF32 matmul, cuDNN TF32, cuDNN benchmark, and high float32 matmul precision when supported. |
| `WAN22_PERSISTENT_PIPELINE=true` | `false` | Uses an optional long-lived Wan2.2 TI2V subprocess so compatible renders can reuse the loaded T5/VAE/DiT pipeline. |
| `SDXL_UNLOAD_AFTER_JOB=true` | `false` | Releases the cached SDXL pipeline after character reference or shot start image jobs, then runs garbage collection and CUDA cache cleanup. This can reduce RAM/VRAM pressure before Wan2.2 jobs at the cost of reloading SDXL for the next image job. |
| `WAN22_VAE_DTYPE=fp16` / `bf16` / `fp32` | unset | Experimental VAE dtype override. Leave unset for Wan2.2 default behavior. |
| `WAN22_DEFAULT_OFFLOAD_MODEL=false` | `true` | Risky memory/performance test. Keeping more model state on GPU can reduce transfer overhead but may OOM on 16GB VRAM. |

`WAN22_PERSISTENT_PIPELINE=true` starts `warm_ti2v_server.py` inside the local Wan2.2 repository by using `WAN22_PYTHON_EXE` and `WAN22_REPO_DIR`. The first compatible render is still a cold render because the server must load WanTI2V. Later compatible renders can reuse the loaded pipeline and avoid paying the T5/VAE/DiT initialization cost for every shot.

Compatibility is based on checkpoint directory, task, `t5_cpu`, `convert_model_dtype`, VAE dtype, torch optimization mode, and process rank/device settings. If these change, the warm server reloads the pipeline before rendering. If the warm server crashes or times out, the worker fails the current job clearly and terminates the child process. Set `WAN22_PERSISTENT_PIPELINE=false` to return to the existing `generate.py` subprocess-per-job path.

Persistent mode does not change render quality settings and does not remove the VAE decode bottleneck. It only targets repeated model/pipeline loading between compatible shot renders.

### Current Best Observed Local Wan Runtime

The current best local Wan2.2 runtime configuration from testing is:

```env
WAN22_DEFAULT_OFFLOAD_MODEL=true
WAN22_DEFAULT_CONVERT_MODEL_DTYPE=true
WAN22_DEFAULT_T5_CPU=true
WAN22_TORCH_OPTIMIZE=false
WAN22_PERSISTENT_PIPELINE=true
WAN22_VAE_DTYPE=fp16
SDXL_UNLOAD_AFTER_JOB=true
```

Observed results for warm subsequent `FastPreview` image-to-video renders at 25 frames and 5 sample steps:

* First render is slower because the warm subprocess loads WanTI2V once.
* Later compatible renders reuse the persistent pipeline.
* Warm render duration is approximately 180-203 seconds.
* Sampling is approximately 12 seconds.
* VAE decode is approximately 160-180 seconds.
* `WAN22_VAE_DTYPE=fp16` works locally and reduces VAE decode time compared with default fp32 behavior.
* `WAN22_TORCH_OPTIMIZE=false` remains the best observed value for this local setup.
* The remaining dominant bottleneck is VAE decode, not model loading or sampling.

Keep these as local test settings, not global defaults. If instability appears, return to the safe defaults by setting `WAN22_PERSISTENT_PIPELINE=false`, clearing `WAN22_VAE_DTYPE`, and leaving `SDXL_UNLOAD_AFTER_JOB=false`.

Recommended local A/B tests, without changing defaults:

| Test | Worker environment | Record |
| ---- | ------------------ | ------ |
| A | baseline defaults | VAE decode duration, sampling duration, total render duration, peak allocated/reserved VRAM, valid output, OOM status |
| B | `WAN22_TORCH_OPTIMIZE=true` | Same metrics as A |
| C | `SDXL_UNLOAD_AFTER_JOB=true` and `WAN22_TORCH_OPTIMIZE=true` | Same metrics as A, plus SDXL reload cost for later image jobs |
| D | `WAN22_VAE_DTYPE=fp16` only after B/C | Same metrics as A |
| E | `WAN22_DEFAULT_OFFLOAD_MODEL=false` only as a risky test because 16GB VRAM may OOM | Same metrics as A |

Do not change quality or offload defaults until diagnostics show the real bottleneck and stability tradeoff.

---

## Assembly and Finalization

35. `POST /api/projects/{id}/assemble` creates an `AssembleVideo` job.

36. The API collects completed shot render outputs and orders them by scene index, then shot index.

37. The Python worker handles `AssembleVideo` by calling FFmpeg concat/stitch and writing:

```text
storage/finals/{projectId}/assembled.mp4
```

38. Dialogue lines are persisted and can be queued into `GenerateAudio` jobs for Edge TTS.

39. `POST /api/projects/{id}/finalize` prefers `assembled.mp4` when present.

40. If `assembled.mp4` is not present, finalize may fall back to the first completed shot render for development.

41. `POST /api/projects/{id}/finalize` creates a `MuxAudio` job.

42. `MuxAudio` jobs are executed by FFmpeg in the Python worker and write:

```text
storage/finals/{projectId}/final-preview.mp4
```

43. Final audio muxing must preserve input video duration.

44. If the audio duration is shorter than the assembled video duration, FFmpeg must pad audio with silence or finalize without trimming the video.

45. Final output duration must match the input video duration, not the audio duration.

---

## Artifact Types

Character reference images, shot start images, rendered shot videos, assembled movies, and final muxed videos are separate production artifacts.

* Character references guide identity in text prompts.
* Shot start images drive Wan2.2 image-to-video.
* Rendered shot videos are individual Wan2.2 outputs.
* Assembly combines already-rendered shot videos.
* Finalization muxes audio onto the chosen movie file.

---

## MagicLight-Style Staging

MagicLight-style staging means the user can pause between story analysis and rendering to improve identity and control:

1. Prepare prompts.
2. Generate or upload character references.
3. Generate or upload shot start images.
4. Render selected shots or scenes.
5. Assemble completed renders.
6. Generate audio.
7. Finalize the movie.

This does not use a node workflow system.

---

## Hard Rules

* The API never imports ML libraries.
* The API never loads checkpoints.
* The API never invokes GPU inference.
* Ollama is used only for planning and prompt generation.
* Wan2.2 is not an Ollama model.
* FFmpeg operations belong in the Python worker.
* SQL Server remains the database.
* ComfyUI is not used.
