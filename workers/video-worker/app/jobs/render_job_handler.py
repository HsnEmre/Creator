import logging
import json

from app.config import Settings
from app.models.base_image_model import ImageGenerationRequest
from app.models.base_video_model import RenderRequest
from app.models.image_adapter_factory import create_image_adapter
from app.models.wan22_animate_adapter import Wan22AnimateAdapter
from app.models.wan22_s2v_adapter import Wan22S2VAdapter
from app.models.wan22_ti2v_adapter import Wan22TI2VAdapter
from app.services.api_client import ApiClient
from app.services.ffmpeg_service import FFmpegService
from app.services.performance_diagnostics import cuda_memory_snapshot, elapsed_seconds, log_perf, now, timed, torch_diagnostics
from app.services.tts_service import TtsService


class RenderJobHandler:
    def __init__(self, api: ApiClient, settings: Settings) -> None:
        self.api = api
        self.settings = settings
        self.ti2v = Wan22TI2VAdapter(settings)
        self.s2v = Wan22S2VAdapter(settings)
        self.animate = Wan22AnimateAdapter(settings)
        self.image_adapter = create_image_adapter(settings)
        self.tts = TtsService()
        self.ffmpeg = FFmpegService(settings)

    def handle(self, job: dict) -> None:
        job_id = job["id"]
        job_start = now()
        request = RenderRequest.from_job(job)
        job_type = self._resolve_job_type(request.job_type)
        render_context = self._render_context(request)
        log_perf(
            "job_started",
            job_id=job_id,
            project_id=request.project_id,
            job_type=job_type,
            raw_job_type=request.job_type,
            preset=request.preset,
            generation_mode=request.generation_mode,
            character_id=request.character_id,
            has_input_image=bool(request.image_path),
            has_input_video=bool(request.video_path),
            has_input_audio=bool(request.audio_path),
            prompt_chars=len(request.prompt or ""),
            **render_context,
            **torch_diagnostics(),
            **cuda_memory_snapshot("job_start"),
        )
        log_perf(
            "worker_job_payload",
            job_id=job_id,
            project_id=request.project_id,
            job_type=job_type,
            output_path=request.output_path,
            input_image_path=request.image_path,
            input_video_present=bool(request.video_path),
            input_audio_path=request.audio_path,
        )
        try:
            self.api.start_job(job_id)
            self.api.update_progress(job_id, 5)
            if job_type in {"GenerateCharacterReferenceImage", "GenerateShotStartImage"}:
                image_request = ImageGenerationRequest.from_job(job)
                self.api.update_progress(job_id, 10)
                with timed(
                    "image_generation_duration",
                    job_id=job_id,
                    project_id=request.project_id,
                    job_type=job_type,
                    generation_type=image_request.generation_type,
                    output_resolution=f"{image_request.width}x{image_request.height}",
                    sample_steps=self.settings.sdxl_num_inference_steps,
                    guidance_cfg=self.settings.sdxl_guidance_scale,
                    dtype="float16",
                    device=self.settings.sdxl_device,
                    **cuda_memory_snapshot("before_image_generation"),
                ):
                    result = self.image_adapter.generate(image_request)
                self.api.update_progress(job_id, 90)
                if result.success:
                    self.api.update_progress(job_id, 100)
                    self.api.complete_job(job_id, result.output_path or image_request.output_path)
                else:
                    self.api.fail_job(job_id, result.error_message or "Image generation failed.")
                return

            if job_type == "GenerateAudio":
                self.api.update_progress(job_id, 10)
                output_path = request.output_path or ""
                voice = request.voice or self.settings.edge_tts_default_voice
                text = request.text_content or request.prompt
                with timed("tts_generation_duration", job_id=job_id, project_id=request.project_id, voice=voice):
                    self.tts.generate_edge_tts(text, voice, output_path)
                self.api.update_progress(job_id, 90)
                self.api.update_progress(job_id, 100)
                self.api.complete_job(job_id, output_path)
                return

            if job_type == "MuxAudio":
                if not request.video_path or not request.audio_path or not request.output_path:
                    raise RuntimeError("MuxAudio job missing inputVideoPath, inputAudioPath, or outputPath.")
                self.api.update_progress(job_id, 10)
                with timed("mux_finalize_duration", job_id=job_id, project_id=request.project_id, **cuda_memory_snapshot("before_mux_finalize")):
                    self.ffmpeg.mux_audio_preserve_video_duration(request.video_path, request.audio_path, request.output_path)
                self.api.update_progress(job_id, 90)
                self.api.update_progress(job_id, 100)
                self.api.complete_job(job_id, request.output_path)
                return

            if job_type == "AssembleVideo":
                if not request.video_path or not request.output_path:
                    raise RuntimeError("AssembleVideo job missing inputVideoPath or outputPath.")
                video_paths = json.loads(request.video_path) if request.video_path.strip().startswith("[") else [request.video_path]
                if not video_paths:
                    raise RuntimeError("AssembleVideo job has no input videos.")
                self.api.update_progress(job_id, 10)
                with timed("video_assembly_duration", job_id=job_id, project_id=request.project_id, video_count=len(video_paths), **cuda_memory_snapshot("before_video_assembly")):
                    self.ffmpeg.stitch_videos(video_paths, request.output_path)
                self.api.update_progress(job_id, 90)
                self.api.update_progress(job_id, 100)
                self.api.complete_job(job_id, request.output_path)
                return

            adapter = self._select_adapter(request.generation_mode)
            self.api.update_progress(job_id, 10)
            with timed(
                "per_shot_render_duration",
                job_id=job_id,
                project_id=request.project_id,
                preset=request.preset,
                generation_mode=request.generation_mode,
                **render_context,
                **cuda_memory_snapshot("before_per_shot_render"),
            ):
                result = adapter.render(request)
            self.api.update_progress(job_id, 90)
            if result.success:
                self.api.update_progress(job_id, 100)
                self.api.complete_job(job_id, result.output_path or "")
            else:
                self.api.fail_job(job_id, result.error_message or "Render failed.")
        except Exception as exc:
            logging.exception("Render job %s failed.", job_id)
            self.api.fail_job(job_id, str(exc))
        finally:
            log_perf(
                "job_finished",
                job_id=job_id,
                project_id=request.project_id,
                job_type=job_type,
                preset=request.preset,
                status="finished",
                total_duration_seconds=elapsed_seconds(job_start),
                **render_context,
                **cuda_memory_snapshot("job_finished"),
            )

    def _select_adapter(self, generation_mode: str):
        if generation_mode == "SpeechToVideo":
            return self.s2v
        if generation_mode in {"VideoToVideo", "Animate"}:
            return self.animate
        return self.ti2v

    @staticmethod
    def _resolve_job_type(job_type: str) -> str:
        if job_type.isdigit():
            mapping = {
                "0": "ShotRender",
                "1": "SceneAssembly",
                "2": "FinalAssembly",
                "3": "GenerateAudio",
                "4": "MuxAudio",
                "5": "RenderVideo",
                "6": "AssembleVideo",
                "7": "GenerateCharacterReferenceImage",
                "8": "GenerateShotStartImage",
            }
            return mapping.get(job_type, job_type)
        return job_type

    def _render_context(self, request: RenderRequest) -> dict:
        frame_count = request.frame_num or self.settings.wan22_default_frame_num
        output_resolution = request.size or self.settings.wan22_default_size
        sample_steps = request.sample_steps or self.settings.wan22_default_sample_steps
        return {
            "frame_count": frame_count,
            "output_resolution": output_resolution,
            "sample_steps": sample_steps,
            "guidance_cfg": "wan_generate_default",
            "dtype": "fp16_conversion_enabled" if self.settings.wan22_default_convert_model_dtype else "wan_default",
            "vae_dtype": self.settings.wan22_vae_dtype or "wan_default",
            "device": "external_wan_generate_py",
            "offload_model": self.settings.wan22_default_offload_model,
            "t5_cpu": self.settings.wan22_default_t5_cpu,
        }
