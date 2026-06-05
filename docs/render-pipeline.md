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

5. Long-form plans are duration-validated before persistence. Ollama is prompted to create enough short shots, but the API also enforces deterministic guardrails so an undersized LLM response is repaired rather than silently accepted:

| Target duration | Minimum scenes | Target scenes | Minimum shots | Target shots | Shot duration | Required planned duration |
| --------------- | -------------: | ------------: | ------------: | -----------: | ------------: | ------------------------: |
| `180-299s`      |            `8` |       `10-14` |          `24` |      `30-36` |        `5-8s` |        at least `90%` |
| `300-419s`      |           `12` |       `14-18` |          `40` |      `45-60` |        `5-8s` |        at least `90%` |
| `420s+`         |           `14` |       `18-24` |          `50` |      `60-84` |        `5-8s` |        at least `85%` |

   For shorter projects below 180 seconds, the previous compact behavior is preserved, but the normalizer avoids absurd 1-scene/1-shot plans unless the story explicitly behaves like a single-shot piece.

6. If the returned plan is too short, the API expands it deterministically by duplicating and varying narrative beats into sequential scenes and shots, clamping long-form shots to `5-8s`, keeping character visual locks, and preserving dialogue from the original scene set. The analyze logs include:

   * `storyboard_duration_validation_started`
   * `storyboard_duration_validation_failed`
   * `storyboard_duration_repair_started`
   * `storyboard_duration_repair_completed`
   * `storyboard_duration_validation_completed`

7. The normalizer also enforces cinematic continuity from the fields already persisted in SQL Server:

   * each character gets a stable visual lock prompt and continuity rules
   * every relevant shot repeats involved character visual locks
   * recurring scene location, time of day, mood, lighting, and color are repeated in shot prompts
   * Turkish dialogue or narration stays in dialogue fields, not visual prompts
   * visual prompts and negative prompts are kept in English
   * generated start image prompts include character locks and location continuity

8. Negative prompts are composed per shot instead of copied as identical boilerplate. The deterministic negative prompt builder combines:

   * global technical negatives
   * character continuity negatives when characters are present
   * location/era/weather negatives
   * shot-context negatives for battle, palace, night mountain, village, forest, and fallback continuity cases

   Negative prompt validation logs:

   * `storyboard_negative_prompt_validation_started`
   * `storyboard_negative_prompt_validation_failed`
   * `storyboard_negative_prompt_repair_started`
   * `storyboard_negative_prompt_repair_completed`
   * `storyboard_negative_prompt_validation_completed`

9. `GET /api/projects/{id}/production-plan` reconstructs the saved production plan from SQL Server and includes duration and continuity health metadata:

   * `sceneCount`
   * `shotCount`
   * `totalPlannedDurationSeconds`
   * `targetDurationSeconds`
   * `plannedDurationCoveragePercent`
   * `isDurationPlanValid`
   * `durationPlanWarning`
   * `hasContinuityBible`
   * `characterVisualLocksApplied`
   * `distinctNegativePromptCount`
   * `duplicateNegativePromptGroups`
   * `continuityWarning`

10. Existing projects created before these continuity/duration rules can be repaired by running Analyze again. Analyze replaces storyboard plan records safely while preserving completed final videos and generated media files on disk.

11. `POST /api/projects/{id}/preproduction/prepare` creates or refreshes MagicLight-style visual preparation prompts:

   * Character reference image prompts in English
   * Character reference negative prompts
   * Shot start image / keyframe prompts in English
   * Shot start image negative prompts

12. The frontend lets the user review, edit, copy, generate, or manually upload these visual assets before rendering.

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

13a. Storyboard rendering also supports an optional render duration mode. The default is still `FastPreview`, preserving the existing 25-frame behavior for quick tests.

| Duration mode | Frame selection | Intended use |
| ------------- | --------------- | ------------ |
| `FastPreview` | uses the preset frame count, currently `25` for FastPreview | fastest pipeline validation; raw motion is about one second at 24fps |
| `CinematicPreview` | uses `73` frames with the selected preset's sample steps | slower review clips with more visible motion |
| `LongMotion` | derives frames from the shot target duration, normalizes to Wan-friendly `4n+1` counts, and clamps to `121` frames | slowest preview mode; intended for true generated motion rather than loop-stretched duration |

The API logs duration-mode decisions when render jobs are created:

* `wan_render_duration_mode_selected`
* `wan_render_frame_count_selected`
* `wan_render_frame_count_clamped`
* `wan_render_expected_duration`

These logs include project, scene, shot, render job id, target shot duration, requested frame count, actual frame count, and expected raw clip duration.

Render duration metadata is stored on every video `RenderJob`:

* `renderDurationMode`
* `requestedShotDurationSeconds`
* `requestedFrameNum`
* `actualFrameNum`
* `expectedRawClipDurationSeconds`
* `probedRawClipDurationSeconds`
* `rawDurationCoveragePercent`

The worker probes completed raw video duration with FFprobe and reports it back to the API when completing a video render job.

14. `maxShots`, `sceneIndex`, and `shotIndex` can limit queueing to a subset for quick iteration.

15. Render targeting supports:

* selected `shotIds`
* one scene
* one shot
* broader project-wide queues

When `force=false` or omitted, render creation skips shots that already have an active `Pending`/`Rendering` video job or a latest successful completed render with a valid output path. This makes Storyboard "Animate Missing" safe for repeated local testing. Use `force=true` only when intentionally regenerating a selected shot or all shots.

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

31b. LongMotion is stricter than preview modes. If `LongMotion` is requested with `useShotStartImage=true`, every selected shot must have a valid shot start image before the API queues render jobs. Missing keyframes are blocked with a clear error instead of silently falling back to Text-to-Video. The worker also verifies that an Image-to-Video start image path exists before invoking Wan2.2.

31a. Render-job creation logs the active image-conditioning path:

* `character_reference_lock_applied`
* `shot_reference_image_selected`
* `shot_start_image_selected`
* `shot_start_image_missing`
* `shot_start_image_used_for_video`
* `shot_start_image_not_supported_for_video`

Character reference images are logged as prompt identity guidance. They are not faked as Wan2.2 image conditioning. Shot start images are the only generated images passed as Wan2.2 `--image` in the current architecture.

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
| `WAN22_PREWARM_ON_START=true` | `false` | Starts the persistent Wan subprocess at worker startup and performs a health check without running inference. It is useful for surfacing warm-server startup problems early, but the first actual render may still pay the full model load cost. |
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

36. The API selects exactly one latest valid completed render for each storyboard shot, then orders those selected renders by scene index and shot index. Historical test renders remain in the database and on disk, but they are not assembled when a newer completed render exists for the same shot.

   * Missing shots are skipped and reported in the assemble response.
   * The API logs the selected render job IDs and shot IDs for traceability.
   * Assembly does not delete old render rows or media files.

37. The assemble job payload includes one segment per selected storyboard shot:

   * source render path
   * scene index
   * shot index
   * shot id
   * render job id
   * planned `targetDurationSeconds`

38. Before queueing assembly, the API validates long-form storyboard plans. A long-form project cannot assemble if the saved storyboard is below the bucket minimum, has missing/zero target durations, or has too little total planned duration. A 180-second project producing only a few seconds of target segments is rejected with a clear error instead of silently assembling.

39. The Python worker duration-locks assembly segments before concat. Short source clips are looped/held to the target shot duration, and long source clips are trimmed to the target duration. This preserves the storyboard timing while still using the latest completed render for each shot. FastPreview clips are short Wan renders; long previews may therefore loop/hold/extend short generated clips. This is a preview assembly strategy, not true long generated motion.

39a. Use `CinematicPreview` or `LongMotion` when the raw generated clip should contain more real motion before assembly. Assembly still uses the latest valid render per shot and still duration-locks segments to the storyboard timing; it does not turn a one-second FastPreview into true long generated motion.

39b. LongMotion is not allowed to fake long shots by severe loop/hold extension. The worker fails a LongMotion render if the probed raw output duration is below 75 percent of the target/expected raw duration. During assembly, LongMotion source clips must also cover at least 75 percent of their target shot duration or assembly fails with `ffmpeg_assembly_fake_duration_prevented`.

40. The worker validates final assembled duration against the sum of target shot durations. Tolerance is `max(2 seconds, 2 percent of total target duration)`. If a 180-second storyboard produces a 9.375-second output, the assembly job fails.

41. The API and worker log:

   * `ffmpeg_assembly_plan_validation_started`
   * `ffmpeg_assembly_plan_validation_failed`
   * `ffmpeg_assembly_plan_validation_completed`
* `ffmpeg_assembly_selected_shot`
* `ffmpeg_assembly_extension_policy_selected`
* `ffmpeg_assembly_source_too_short_for_longmotion`
* `ffmpeg_assembly_severe_extension_blocked`
* `ffmpeg_assembly_fake_duration_prevented`
* `ffmpeg_assembly_segment_normalized`
   * `ffmpeg_assembly_duration_plan`
   * `ffmpeg_assembly_output_duration_validation_failed`
   * `ffmpeg_assembly_output_duration_validation_completed`
   * `ffmpeg_assembly_completed`

42. The Python worker writes the assembled movie to:

```text
storage/finals/{projectId}/assembled.mp4
```

43. Dialogue lines are persisted and can be queued into `GenerateAudio` jobs for Edge TTS.

42. `POST /api/projects/{id}/finalize` prefers `assembled.mp4` when present.

43. If `assembled.mp4` is not present, finalize may fall back to the first completed shot render for development.

44. `POST /api/projects/{id}/finalize` creates a `MuxAudio` job.

45. `MuxAudio` jobs are executed by FFmpeg in the Python worker and write:

```text
storage/finals/{projectId}/final-preview.mp4
```

46. Final audio muxing must preserve input video duration.

47. If the audio duration is shorter than the assembled video duration, FFmpeg pads audio with silence so the video is not cut to the audio length.

50. Final output duration must match the input video duration, not the audio duration. Mux logs include the input video duration, audio duration, whether audio is shorter or longer, and the final output duration.

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

### Storyboard Render Observability

The Storyboard UI shows the latest render state per shot:

* completed render indicator
* latest render duration when `StartedAt` and `FinishedAt` are available
* target duration, planned duration, scene count, shot count, and coverage
* continuity bible status
* character visual lock status
* distinct negative prompt count
* character reference image availability
* shot keyframe availability
* whether completed renders used Image-to-Video or text-only fallback
* keyframe/Image-to-Video state
* whether "Animate Missing" will skip already completed shots
* selected render duration mode
* expected raw generated clip duration when frame metadata is available
* warnings when the raw generated clip is shorter than the planned shot duration
* LongMotion failures when raw output is too short and would require fake loop extension

"Animate Selected" is the explicit regeneration path for the selected shot. "Animate Missing" sends all storyboard shot IDs with `force=false`, allowing the backend to skip completed or already-active shots. "Regenerate All" sends the same shot IDs with `force=true` for intentional full rerenders.

VAE decode remains the dominant runtime bottleneck for Wan2.2 renders. The render reuse and assembly selection rules reduce unnecessary work, but they do not optimize VAE decode.

Future work:

* higher frame-count render profiles beyond the current safe 121-frame local ceiling
* deeper image-conditioned character reference support only if the architecture later supports it
* IP-Adapter-like or reference-conditioning workflows only through Python worker adapters, never through ComfyUI
* richer plan versioning for preserving multiple storyboard generations side-by-side

Character references alone are prompt guidance. Stronger character consistency requires shot keyframes/Image-to-Video or future real reference-conditioning adapters. The UI and logs should stay honest when a render is prompt-only.

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
