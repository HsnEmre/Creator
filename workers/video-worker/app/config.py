import os
from dataclasses import dataclass
from pathlib import Path
from typing import Optional


def _bool(value: str, default: bool = False) -> bool:
    if value is None:
        return default
    return value.lower() in {"1", "true", "yes", "on"}


@dataclass(frozen=True)
class Settings:
    api_base_url: str
    poll_interval_seconds: float
    wan22_python_exe: str
    wan22_repo_dir: str
    wan22_ti2v_ckpt_dir: str
    wan22_output_dir: str
    wan22_default_size: str
    wan22_default_frame_num: int
    wan22_default_sample_steps: int
    wan22_default_offload_model: bool
    wan22_default_convert_model_dtype: bool
    wan22_default_t5_cpu: bool
    wan22_vae_dtype: str
    wan22_torch_optimize: bool
    wan22_persistent_pipeline: bool
    wan22_prewarm_on_start: bool
    wan22_timeout_seconds: int
    ffmpeg_path: str
    placeholder_outputs: bool
    image_model_provider: str
    sdxl_model_path: str
    sdxl_device: str
    sdxl_width: int
    sdxl_height: int
    sdxl_num_inference_steps: int
    sdxl_guidance_scale: float
    sdxl_enable_cpu_offload: bool
    sdxl_unload_after_job: bool
    image_output_dir: str
    performance_diagnostics: bool
    tts_provider: str
    edge_tts_default_voice: str
    edge_tts_output_dir: str

    @staticmethod
    def from_env(project_root: Optional[Path] = None) -> "Settings":
        worker_root = project_root or Path(__file__).resolve().parents[1]
        default_output = str((worker_root.parent.parent / "storage" / "renders").resolve())

        api_base_url = (
            os.getenv("VIDEO_API_BASE_URL")
            or os.getenv("VIDEOSTUDIO_API_BASE_URL")
            or "http://localhost:5000"
        )

        return Settings(
            api_base_url=api_base_url,
            poll_interval_seconds=float(os.getenv("VIDEOSTUDIO_POLL_INTERVAL_SECONDS", "2")),
            wan22_python_exe=os.getenv("WAN22_PYTHON_EXE", ""),
            wan22_repo_dir=os.getenv("WAN22_REPO_DIR", os.getenv("WAN22_REPO_PATH", "")),
            wan22_ti2v_ckpt_dir=os.getenv("WAN22_TI2V_CKPT_DIR", os.getenv("WAN22_CHECKPOINT_PATH", "")),
            wan22_output_dir=os.getenv("WAN22_OUTPUT_DIR", os.getenv("VIDEOSTUDIO_OUTPUT_DIR", default_output)),
            wan22_default_size=os.getenv("WAN22_DEFAULT_SIZE", "1280*704"),
            wan22_default_frame_num=int(os.getenv("WAN22_DEFAULT_FRAME_NUM", "49")),
            wan22_default_sample_steps=int(os.getenv("WAN22_DEFAULT_SAMPLE_STEPS", "10")),
            wan22_default_offload_model=_bool(os.getenv("WAN22_DEFAULT_OFFLOAD_MODEL", "true"), True),
            wan22_default_convert_model_dtype=_bool(os.getenv("WAN22_DEFAULT_CONVERT_MODEL_DTYPE", "true"), True),
            wan22_default_t5_cpu=_bool(os.getenv("WAN22_DEFAULT_T5_CPU", "true"), True),
            wan22_vae_dtype=os.getenv("WAN22_VAE_DTYPE", ""),
            wan22_torch_optimize=_bool(os.getenv("WAN22_TORCH_OPTIMIZE", "false"), False),
            wan22_persistent_pipeline=_bool(os.getenv("WAN22_PERSISTENT_PIPELINE", "false"), False),
            wan22_prewarm_on_start=_bool(os.getenv("WAN22_PREWARM_ON_START", "false"), False),
            wan22_timeout_seconds=int(os.getenv("WAN22_TIMEOUT_SECONDS", "7200")),
            ffmpeg_path=os.getenv("FFMPEG_PATH", "ffmpeg"),
            placeholder_outputs=_bool(os.getenv("VIDEOSTUDIO_PLACEHOLDER_OUTPUTS", "false"), False),
            image_model_provider=os.getenv("IMAGE_MODEL_PROVIDER", "PLACEHOLDER").upper(),
            sdxl_model_path=os.getenv("SDXL_MODEL_PATH", "C:/AI/models/sdxl-base-1.0"),
            sdxl_device=os.getenv("SDXL_DEVICE", "cuda"),
            sdxl_width=int(os.getenv("SDXL_WIDTH", "1280")),
            sdxl_height=int(os.getenv("SDXL_HEIGHT", "704")),
            sdxl_num_inference_steps=int(os.getenv("SDXL_NUM_INFERENCE_STEPS", "20")),
            sdxl_guidance_scale=float(os.getenv("SDXL_GUIDANCE_SCALE", "7.0")),
            sdxl_enable_cpu_offload=_bool(os.getenv("SDXL_ENABLE_CPU_OFFLOAD", "true"), True),
            sdxl_unload_after_job=_bool(os.getenv("SDXL_UNLOAD_AFTER_JOB", "false"), False),
            image_output_dir=os.getenv("IMAGE_OUTPUT_DIR", str((worker_root.parent.parent / "storage" / "assets").resolve())),
            performance_diagnostics=_bool(os.getenv("VIDEO_WORKER_PERF_LOG", os.getenv("VIDEOSTUDIO_PERF_LOG", "false")), False),
            tts_provider=os.getenv("TTS_PROVIDER", "EdgeTTS"),
            edge_tts_default_voice=os.getenv("EDGE_TTS_DEFAULT_VOICE", "tr-TR-AhmetNeural"),
            edge_tts_output_dir=os.getenv("EDGE_TTS_OUTPUT_DIR", str((worker_root.parent.parent / "storage" / "audio").resolve())),
        )
