import gc
import logging
from pathlib import Path
from typing import Any, Optional

from app.config import Settings
from app.models.base_image_model import ImageGenerationRequest, ImageResult
from app.services.performance_diagnostics import cuda_memory_snapshot, elapsed_seconds, log_perf, now, torch_diagnostics, timed


class SDXLImageAdapter:
    _pipeline: Any = None

    def __init__(self, settings: Settings) -> None:
        self.settings = settings

    def generate(self, request: ImageGenerationRequest) -> ImageResult:
        generation_start = now()
        try:
            output_path = Path(request.output_path)
            output_path.parent.mkdir(parents=True, exist_ok=True)

            width = request.width or self.settings.sdxl_width
            height = request.height or self.settings.sdxl_height
            logging.info(
                "Generating %s image with SDXL model=%s size=%sx%s steps=%s guidance=%s output=%s",
                request.generation_type,
                self.settings.sdxl_model_path,
                width,
                height,
                self.settings.sdxl_num_inference_steps,
                self.settings.sdxl_guidance_scale,
                output_path,
            )

            log_perf(
                "sdxl_generation_config",
                job_id=request.job_id,
                project_id=request.project_id,
                generation_type=request.generation_type,
                model_path=self.settings.sdxl_model_path,
                output_resolution=f"{width}x{height}",
                sample_steps=self.settings.sdxl_num_inference_steps,
                guidance_cfg=self.settings.sdxl_guidance_scale,
                dtype="float16",
                device=self.settings.sdxl_device,
                cpu_offload=self.settings.sdxl_enable_cpu_offload,
                **torch_diagnostics(),
                **cuda_memory_snapshot("before_render"),
            )
            pipe = self._load_pipeline(request)
            generator = self._build_generator(request.seed)
            with timed("sdxl_reference_generation_duration", job_id=request.job_id, project_id=request.project_id, generation_type=request.generation_type):
                result = pipe(
                    prompt=request.prompt,
                    negative_prompt=request.negative_prompt or "",
                    width=width,
                    height=height,
                    num_inference_steps=self.settings.sdxl_num_inference_steps,
                    guidance_scale=self.settings.sdxl_guidance_scale,
                    generator=generator,
                )
            image = result.images[0]
            image.save(output_path, format="PNG")

            if not output_path.exists() or output_path.stat().st_size == 0:
                return ImageResult(success=False, error_message=f"SDXL did not write an output image: {output_path}")

            log_perf(
                "sdxl_image_completed",
                job_id=request.job_id,
                project_id=request.project_id,
                output_path=str(output_path),
                output_size_bytes=output_path.stat().st_size,
                total_duration_seconds=elapsed_seconds(generation_start),
                **cuda_memory_snapshot("after_render"),
            )
            return ImageResult(success=True, output_path=str(output_path), logs="SDXL generation completed.")
        except Exception as exc:
            logging.exception("SDXL image generation failed for job %s", request.job_id)
            return ImageResult(success=False, error_message=f"SDXL image generation failed: {exc}")

    def _load_pipeline(self, request: ImageGenerationRequest):
        if SDXLImageAdapter._pipeline is not None:
            log_perf("sdxl_model_load_duration", job_id=request.job_id, project_id=request.project_id, cached=True, duration_seconds=0)
            return SDXLImageAdapter._pipeline

        load_start = now()
        import torch
        from diffusers import StableDiffusionXLPipeline

        model_path = Path(self.settings.sdxl_model_path)
        if not model_path.exists():
            raise RuntimeError(f"SDXL model path does not exist: {model_path}")

        kwargs = {
            "torch_dtype": torch.float16,
            "use_safetensors": True,
            "local_files_only": True,
        }
        try:
            pipe = StableDiffusionXLPipeline.from_pretrained(str(model_path), variant="fp16", **kwargs)
        except (TypeError, OSError, ValueError):
            pipe = StableDiffusionXLPipeline.from_pretrained(str(model_path), **kwargs)

        if self.settings.sdxl_enable_cpu_offload:
            pipe.enable_model_cpu_offload()
        else:
            pipe = pipe.to(self.settings.sdxl_device)

        SDXLImageAdapter._pipeline = pipe
        log_perf(
            "sdxl_model_load_duration",
            job_id=request.job_id,
            project_id=request.project_id,
            cached=False,
            duration_seconds=elapsed_seconds(load_start),
        )
        return pipe

    def _build_generator(self, seed: Optional[int]):
        if seed is None:
            return None

        import torch

        device = self.settings.sdxl_device if not self.settings.sdxl_enable_cpu_offload else "cpu"
        return torch.Generator(device=device).manual_seed(seed)

    @classmethod
    def unload_pipeline(cls) -> None:
        had_pipeline = cls._pipeline is not None
        cls._pipeline = None
        gc.collect()

        cuda_available = False
        try:
            import torch

            cuda_available = torch.cuda.is_available()
            if cuda_available:
                torch.cuda.empty_cache()
        except Exception as exc:
            logging.warning("SDXL cache unload completed with CUDA cleanup warning: %s", exc)
            log_perf("sdxl_pipeline_unload_warning", had_pipeline=had_pipeline, error_message=str(exc))
            return

        log_perf(
            "sdxl_pipeline_unloaded",
            had_pipeline=had_pipeline,
            cuda_available=cuda_available,
            **cuda_memory_snapshot("after_sdxl_unload"),
        )
