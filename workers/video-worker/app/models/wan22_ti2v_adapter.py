import logging
import subprocess
import sys
from os import environ
from pathlib import Path
from typing import List

from app.config import Settings
from app.models.base_video_model import RenderRequest, RenderResult
from app.models.wan22_persistent_client import Wan22PersistentClient
from app.services.ffmpeg_service import FFmpegService
from app.services.performance_diagnostics import cuda_memory_snapshot, elapsed_seconds, log_perf, now, torch_diagnostics, timed


class Wan22TI2VAdapter:
    def __init__(self, settings: Settings) -> None:
        self.settings = settings
        self._supports_negative_prompt = None
        self._persistent_client = None
        self._ffmpeg = FFmpegService(settings)

    def render(self, request: RenderRequest) -> RenderResult:
        render_start = now()
        output_dir = Path(self.settings.wan22_output_dir) / request.project_id
        output_dir.mkdir(parents=True, exist_ok=True)
        output_path = output_dir / f"{request.job_id}.mp4"

        mode = "ImageToVideo using shot start image" if request.image_path else "TextToVideo without start image"
        logging.info("Wan2.2 TI2V render mode for job %s: %s", request.job_id, mode)
        command_build_start = now()
        command = self._build_command(request, output_path)
        size = request.size or self.settings.wan22_default_size
        frame_num = self._effective_frame_num(request)
        sample_steps = request.sample_steps or self.settings.wan22_default_sample_steps
        expected_raw_duration = request.expected_raw_clip_duration_seconds or self._expected_duration(frame_num)
        log_perf(
            "wan_worker_duration_mode_received",
            job_id=request.job_id,
            project_id=request.project_id,
            shot_id=self._safe_value(request.shot_id),
            scene_index=request.scene_index,
            shot_index=request.shot_index,
            render_duration_mode=request.render_duration_mode or "FastPreview",
            target_shot_duration_seconds=request.requested_shot_duration_seconds,
            requested_frame_num=request.requested_frame_num,
            actual_frame_num=request.actual_frame_num,
            expected_raw_clip_duration_seconds=expected_raw_duration,
        )
        log_perf(
            "wan_worker_frame_num_received",
            job_id=request.job_id,
            project_id=request.project_id,
            render_duration_mode=request.render_duration_mode or "FastPreview",
            request_frame_num=request.frame_num,
            actual_frame_num=request.actual_frame_num,
            command_frame_num=self._command_value(command, "--frame_num"),
        )
        if request.image_path:
            image_exists = Path(request.image_path).exists()
            log_perf(
                "wan_i2v_start_image_path_received",
                job_id=request.job_id,
                project_id=request.project_id,
                shot_id=self._safe_value(request.shot_id),
                render_duration_mode=request.render_duration_mode or "FastPreview",
                start_image_path=request.image_path,
                exists=image_exists,
            )
            log_perf(
                "wan_i2v_start_image_exists",
                job_id=request.job_id,
                project_id=request.project_id,
                start_image_path=request.image_path,
                exists=image_exists,
            )
            if not image_exists:
                log_perf(
                    "wan_i2v_start_image_missing_blocked",
                    job_id=request.job_id,
                    project_id=request.project_id,
                    start_image_path=request.image_path,
                    exists=False,
                )
                return RenderResult(False, error_message=f"Image-to-Video start image does not exist: {request.image_path}")
            log_perf(
                "wan_i2v_start_image_applied_to_command",
                job_id=request.job_id,
                project_id=request.project_id,
                start_image_path=request.image_path,
                command_uses_image="--image" in command,
            )
        log_perf(
            "wan_render_config",
            job_id=request.job_id,
            project_id=request.project_id,
            mode=mode,
            preset=request.preset,
            frame_count=frame_num,
            output_resolution=size,
            sample_steps=sample_steps,
            guidance_cfg="wan_generate_default",
            dtype="fp16_conversion_enabled" if self.settings.wan22_default_convert_model_dtype else "wan_default",
            device="external_wan_generate_py",
            t5_cpu=self.settings.wan22_default_t5_cpu,
            offload_model=self.settings.wan22_default_offload_model,
            vae_dtype=self.settings.wan22_vae_dtype or "wan_default",
            wan_torch_optimize=self.settings.wan22_torch_optimize,
            command_build_duration_seconds=elapsed_seconds(command_build_start),
            render_duration_mode=request.render_duration_mode or "FastPreview",
            target_shot_duration_seconds=request.requested_shot_duration_seconds,
            requested_frame_num=request.requested_frame_num,
            actual_frame_num=request.actual_frame_num,
            expected_raw_clip_duration_seconds=expected_raw_duration,
            wan_pipeline_initialization_duration_seconds="unavailable_external_subprocess",
            wan_model_load_duration_seconds="included_in_subprocess_duration",
            **torch_diagnostics(),
            **cuda_memory_snapshot("before_render"),
        )
        if self.settings.placeholder_outputs or not self._wan_generate_exists():
            output_path.write_text(
                "Placeholder video artifact. Install Wan2.2 and disable VIDEOSTUDIO_PLACEHOLDER_OUTPUTS for real inference.\n",
                encoding="utf-8",
            )
            log_perf(
                "wan_placeholder_render_completed",
                job_id=request.job_id,
                project_id=request.project_id,
                duration_seconds=elapsed_seconds(render_start),
            )
            return RenderResult(True, str(output_path), stdout=" ".join(command))

        if self.settings.wan22_persistent_pipeline:
            result = self._persistent().render(request, output_path)
            if not result.success:
                return result
            return self._verified_result(request, output_path, result, render_start)

        try:
            with timed(
                "wan_subprocess_duration",
                job_id=request.job_id,
                project_id=request.project_id,
                frame_count=frame_num,
                output_resolution=size,
                sample_steps=sample_steps,
            ):
                completed = self._run_with_logging(command)
        except subprocess.TimeoutExpired as exc:
            return RenderResult(
                False,
                error_message=f"Wan2.2 timed out after {self.settings.wan22_timeout_seconds} seconds.",
                stdout=(exc.stdout or ""),
                stderr=(exc.stderr or ""),
            )
        except Exception as exc:
            return RenderResult(False, error_message=f"Wan2.2 execution failed: {exc}")

        if completed.returncode != 0:
            return RenderResult(False, error_message="Wan2.2 generation failed.", stdout=completed.stdout, stderr=completed.stderr)

        return self._verified_result(request, output_path, RenderResult(True, str(output_path), stdout=completed.stdout, stderr=completed.stderr), render_start)

    def close(self) -> None:
        if self._persistent_client is not None:
            self._persistent_client.close()
            self._persistent_client = None

    def prewarm(self) -> None:
        if not self.settings.wan22_persistent_pipeline:
            logging.warning("WAN22_PREWARM_ON_START requested but WAN22_PERSISTENT_PIPELINE is disabled.")
            return
        if self.settings.placeholder_outputs or not self._wan_generate_exists():
            logging.warning("WAN22_PREWARM_ON_START skipped because Wan2.2 is unavailable or placeholder outputs are enabled.")
            return
        self._persistent().prewarm()

    def _persistent(self) -> Wan22PersistentClient:
        if self._persistent_client is None:
            self._persistent_client = Wan22PersistentClient(self.settings)
        return self._persistent_client

    def _verified_result(self, request: RenderRequest, output_path: Path, result: RenderResult, render_start) -> RenderResult:
        if not output_path.exists():
            return RenderResult(False, error_message=f"Wan2.2 exited successfully but output was not found: {output_path}", stdout=result.stdout, stderr=result.stderr)
        if output_path.stat().st_size <= 0:
            return RenderResult(False, error_message=f"Wan2.2 wrote an empty output file: {output_path}", stdout=result.stdout, stderr=result.stderr)

        probed_duration = self._probe_output_duration(request, output_path)
        validation_error = self._validate_longmotion_duration(request, probed_duration)
        if validation_error:
            return RenderResult(False, error_message=validation_error, stdout=result.stdout, stderr=result.stderr, probed_raw_clip_duration_seconds=probed_duration)

        log_perf(
            "wan_render_completed",
            job_id=request.job_id,
            project_id=request.project_id,
            output_path=str(output_path),
            output_size_bytes=output_path.stat().st_size,
            total_duration_seconds=elapsed_seconds(render_start),
            persistent_pipeline=self.settings.wan22_persistent_pipeline,
            render_duration_mode=request.render_duration_mode or "FastPreview",
            expected_raw_clip_duration_seconds=request.expected_raw_clip_duration_seconds,
            probed_raw_clip_duration_seconds=probed_duration,
            **cuda_memory_snapshot("after_render"),
        )
        return RenderResult(True, str(output_path), stdout=result.stdout, stderr=result.stderr, probed_raw_clip_duration_seconds=probed_duration)

    def _build_command(self, request: RenderRequest, output_path: Path) -> List[str]:
        python_exe = self.settings.wan22_python_exe or "python"
        size = request.size or self.settings.wan22_default_size
        frame_num = self._effective_frame_num(request)
        sample_steps = request.sample_steps or self.settings.wan22_default_sample_steps
        prompt = request.compiled_prompt or request.prompt
        command = [
            python_exe,
            "generate.py",
            "--task",
            "ti2v-5B",
            "--size",
            size,
            "--frame_num",
            str(frame_num),
            "--sample_steps",
            str(sample_steps),
            "--ckpt_dir",
            self.settings.wan22_ti2v_ckpt_dir,
            "--offload_model",
            "True" if self.settings.wan22_default_offload_model else "False",
            "--save_file",
            str(output_path),
            "--prompt",
            prompt,
        ]

        if self.settings.wan22_default_convert_model_dtype:
            command.append("--convert_model_dtype")
        if self.settings.wan22_default_t5_cpu:
            command.append("--t5_cpu")

        if request.negative_prompt and self._supports_negative_prompt_arg():
            command.extend(["--negative_prompt", request.negative_prompt])

        if request.image_path:
            command.extend(["--image", request.image_path])

        log_perf(
            "wan_worker_command_frame_num_applied",
            job_id=request.job_id,
            project_id=request.project_id,
            render_duration_mode=request.render_duration_mode or "FastPreview",
            target_shot_duration_seconds=request.requested_shot_duration_seconds,
            requested_frame_num=request.requested_frame_num,
            actual_frame_num=request.actual_frame_num,
            command_frame_num=frame_num,
        )
        log_perf(
            "wan_worker_command_arguments_built",
            job_id=request.job_id,
            project_id=request.project_id,
            render_duration_mode=request.render_duration_mode or "FastPreview",
            command_frame_num=frame_num,
            command_uses_image="--image" in command,
            has_negative_prompt="--negative_prompt" in command,
            output_path=str(output_path),
        )
        log_perf(
            "wan_worker_expected_raw_duration",
            job_id=request.job_id,
            project_id=request.project_id,
            render_duration_mode=request.render_duration_mode or "FastPreview",
            target_shot_duration_seconds=request.requested_shot_duration_seconds,
            requested_frame_num=request.requested_frame_num,
            actual_frame_num=request.actual_frame_num,
            expected_raw_clip_duration_seconds=request.expected_raw_clip_duration_seconds or self._expected_duration(frame_num),
        )
        return command

    def _probe_output_duration(self, request: RenderRequest, output_path: Path) -> float:
        probed = self._ffmpeg.get_duration_seconds(str(output_path))
        log_perf(
            "wan_worker_output_duration_probed",
            job_id=request.job_id,
            project_id=request.project_id,
            shot_id=self._safe_value(request.shot_id),
            scene_index=request.scene_index,
            shot_index=request.shot_index,
            render_duration_mode=request.render_duration_mode or "FastPreview",
            target_shot_duration_seconds=request.requested_shot_duration_seconds,
            requested_frame_num=request.requested_frame_num,
            actual_frame_num=request.actual_frame_num,
            expected_raw_clip_duration_seconds=request.expected_raw_clip_duration_seconds,
            probed_raw_clip_duration_seconds=probed,
        )
        return float(probed or 0)

    def _validate_longmotion_duration(self, request: RenderRequest, probed_duration: float) -> str:
        if (request.render_duration_mode or "FastPreview") != "LongMotion":
            return ""
        target = float(request.requested_shot_duration_seconds or 0)
        expected = float(request.expected_raw_clip_duration_seconds or self._expected_duration(self._effective_frame_num(request)))
        threshold = min(target * 0.75, expected * 0.75) if target > 0 and expected > 0 else expected * 0.75
        if probed_duration + 0.01 < threshold:
            log_perf(
                "wan_longmotion_raw_duration_validation_failed",
                job_id=request.job_id,
                project_id=request.project_id,
                shot_id=self._safe_value(request.shot_id),
                scene_index=request.scene_index,
                shot_index=request.shot_index,
                render_duration_mode=request.render_duration_mode,
                target_shot_duration_seconds=target,
                expected_raw_clip_duration_seconds=expected,
                probed_raw_clip_duration_seconds=probed_duration,
                threshold_seconds=threshold,
            )
            return (
                "LongMotion raw output duration is too short. "
                f"Expected about {expected:.2f}s, got {probed_duration:.2f}s. "
                "Frame count may not have been applied by worker/Wan command."
            )
        log_perf(
            "wan_longmotion_raw_duration_validation_completed",
            job_id=request.job_id,
            project_id=request.project_id,
            shot_id=self._safe_value(request.shot_id),
            scene_index=request.scene_index,
            shot_index=request.shot_index,
            render_duration_mode=request.render_duration_mode,
            target_shot_duration_seconds=target,
            expected_raw_clip_duration_seconds=expected,
            probed_raw_clip_duration_seconds=probed_duration,
            threshold_seconds=threshold,
        )
        return ""

    def _effective_frame_num(self, request: RenderRequest) -> int:
        return request.actual_frame_num or request.frame_num or self.settings.wan22_default_frame_num

    @staticmethod
    def _expected_duration(frame_num: int) -> float:
        return round(max(1, frame_num) / 24.0, 3)

    @staticmethod
    def _safe_value(value):
        return value or ""

    def _wan_generate_exists(self) -> bool:
        repo_dir = Path(self.settings.wan22_repo_dir)
        return bool(self.settings.wan22_repo_dir) and (repo_dir / "generate.py").exists()

    def _run_with_logging(self, command: List[str]) -> subprocess.CompletedProcess:
        logging.info("Running Wan2.2 command: %s", " ".join(command))
        process_env = dict(environ)
        process_env["PYTHONIOENCODING"] = "utf-8"
        process_env["PYTHONUTF8"] = "1"
        if self.settings.performance_diagnostics:
            process_env["WAN_PERF_LOG"] = "1"
        if self.settings.wan22_vae_dtype:
            process_env["WAN_VAE_DTYPE"] = self.settings.wan22_vae_dtype
        if self.settings.wan22_torch_optimize:
            process_env["WAN_TORCH_OPTIMIZE"] = "1"
        subprocess_start = now()
        log_perf(
            "wan_subprocess_starting",
            task=self._command_value(command, "--task"),
            output_resolution=self._command_value(command, "--size"),
            frame_count=self._command_value(command, "--frame_num"),
            sample_steps=self._command_value(command, "--sample_steps"),
            guidance_cfg=self._command_value(command, "--sample_guide_scale") or "wan_generate_default",
            has_input_image="--image" in command,
            offload_model=self._command_value(command, "--offload_model"),
            convert_model_dtype="--convert_model_dtype" in command,
            t5_cpu="--t5_cpu" in command,
            wan_perf_log=process_env.get("WAN_PERF_LOG", "0"),
            wan_vae_dtype=process_env.get("WAN_VAE_DTYPE", "wan_default"),
            wan_torch_optimize=process_env.get("WAN_TORCH_OPTIMIZE", "0"),
            cwd=self.settings.wan22_repo_dir,
            **torch_diagnostics(),
            **cuda_memory_snapshot("before_wan_subprocess"),
        )
        process = subprocess.Popen(
            command,
            cwd=self.settings.wan22_repo_dir,
            env=process_env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        lines = []
        assert process.stdout is not None
        for line in process.stdout:
            clean = line.rstrip()
            lines.append(clean)
            if clean:
                logging.info("[wan22] %s", self._safe_for_console(clean))

        return_code = process.wait(timeout=self.settings.wan22_timeout_seconds)
        output = "\n".join(lines)
        log_perf(
            "wan_subprocess_completed",
            return_code=return_code,
            duration_seconds=elapsed_seconds(subprocess_start),
            output_line_count=len(lines),
            **cuda_memory_snapshot("after_wan_subprocess"),
        )
        return subprocess.CompletedProcess(command, return_code, stdout=output, stderr=output)

    def _supports_negative_prompt_arg(self) -> bool:
        if self._supports_negative_prompt is not None:
            return self._supports_negative_prompt

        try:
            python_exe = self.settings.wan22_python_exe or "python"
            with timed("wan_negative_prompt_probe_duration"):
                help_proc = subprocess.run(
                    [python_exe, "generate.py", "--help"],
                    cwd=self.settings.wan22_repo_dir,
                    env={
                        **dict(environ),
                        "PYTHONIOENCODING": "utf-8",
                        "PYTHONUTF8": "1",
                    },
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    timeout=30,
                    check=False,
                )
            help_text = f"{help_proc.stdout}\n{help_proc.stderr}"
            self._supports_negative_prompt = "--negative_prompt" in help_text
        except Exception:
            self._supports_negative_prompt = False

        return self._supports_negative_prompt

    @staticmethod
    def _safe_for_console(value: str) -> str:
        output_encoding = getattr(sys.stdout, "encoding", None) or "utf-8"
        try:
            return value.encode(output_encoding, errors="replace").decode(output_encoding, errors="replace")
        except Exception:
            return value.encode("utf-8", errors="replace").decode("utf-8", errors="replace")

    @staticmethod
    def _command_value(command: List[str], option: str) -> str:
        try:
            index = command.index(option)
            return command[index + 1]
        except (ValueError, IndexError):
            return ""
