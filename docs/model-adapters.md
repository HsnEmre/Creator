# Model Adapters

Model adapters live in `workers/video-worker/app/models`.
Ollama plans stories and prompts only. Wan2.2 video generation is connected through these Python adapters, not through the ASP.NET Core API.

## BaseVideoModel

Adapters implement `render(request) -> RenderResult`.

## BaseImageModel

Image adapters implement `generate(request) -> ImageResult`.

The image adapter is selected by `IMAGE_MODEL_PROVIDER`.

`SDXLImageAdapter` is the first local image model adapter. It loads `StableDiffusionXLPipeline` from `SDXL_MODEL_PATH`, uses local diffusers/torch only, writes PNGs under `storage/assets`, and caches the loaded pipeline in worker memory so repeated image jobs do not reload the model every time.

`PlaceholderImageAdapter` remains available with `IMAGE_MODEL_PROVIDER=PLACEHOLDER`. When `VIDEOSTUDIO_PLACEHOLDER_OUTPUTS=true` and Pillow is installed, it writes a simple local PNG so the rest of the media-serving and review pipeline can be tested. When placeholder output is disabled, it fails the job cleanly with a message telling the user to configure an image adapter or upload images manually.

Reserved provider names `FLUX_DEV`, `FLUX_KONTEXT`, and `HIDREAM` currently fail clearly until real adapters are implemented.

Future image adapters can generate:

- character reference images from `Character.ReferenceImagePrompt`
- shot start/keyframe images from `Shot.StartImagePrompt`

The worker updates the API through the same complete/fail/progress endpoints. It never connects directly to SQL Server.

## Wan22TI2VAdapter

The TI2V adapter now runs local Wan2.2 directly through subprocess:

- Executable: `WAN22_PYTHON_EXE`
- Working directory: `WAN22_REPO_DIR`
- Checkpoint: `WAN22_TI2V_CKPT_DIR`
- Output path: `{WAN22_OUTPUT_DIR}/{projectId}/{renderJobId}.mp4`
- Job-level settings from API are used when present:
  - `size`
  - `frameNum`
  - `sampleSteps`
  Fallback remains environment defaults.

Command shape:

`{WAN22_PYTHON_EXE} generate.py --task ti2v-5B --size {WAN22_DEFAULT_SIZE} --frame_num {WAN22_DEFAULT_FRAME_NUM} --sample_steps {WAN22_DEFAULT_SAMPLE_STEPS} --ckpt_dir {WAN22_TI2V_CKPT_DIR} --offload_model True --convert_model_dtype --t5_cpu --save_file {outputPath} --prompt {job.prompt}`

The adapter captures and logs subprocess output, verifies the output file exists and is non-empty, and returns a clear failure when the process exits non-zero or writes no output.

`negativePrompt` is passed only if `generate.py --help` shows support for `--negative_prompt`.

No ComfyUI integration is used.

Fast preview guidance:
- `FastPreview` is intended for pipeline testing only.
- Prompt-only T2V/TI2V can still produce inconsistent facial identity across shots until reference image / image-to-video continuity methods are added.
- Character references guide prompt consistency. Shot start images are what drive Wan2.2 `--image` / image-to-video.

## Wan22S2VAdapter

Speech-to-video is represented as a non-crashing placeholder. It accepts the same worker request object and returns a failed result with a clear message.

## Wan22AnimateAdapter

Animate/video-to-video is also represented as a non-crashing placeholder until the real invocation contract is finalized.
