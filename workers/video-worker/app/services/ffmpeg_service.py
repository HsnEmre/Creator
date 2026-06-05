import logging
import subprocess
import tempfile
from pathlib import Path
from typing import Any, Dict, List, Optional

from app.config import Settings
from app.services.performance_diagnostics import elapsed_seconds, log_perf, now


class FFmpegService:
    def __init__(self, settings: Optional[Settings] = None) -> None:
        self.settings = settings or Settings.from_env()

    def extract_last_frame(self, video_path: str, output_image_path: str) -> None:
        self._ensure_parent_dir(output_image_path)
        self._run([self.settings.ffmpeg_path, "-y", "-sseof", "-1", "-i", video_path, "-frames:v", "1", output_image_path])

    def stitch_videos(self, video_paths: List[str], output_path: str) -> None:
        assembly_start = now()
        if not video_paths:
            raise RuntimeError("No video paths were provided for stitching.")
        missing = [video_path for video_path in video_paths if not Path(video_path).exists()]
        if missing:
            raise RuntimeError(f"Cannot stitch missing video file(s): {missing}")

        self._ensure_parent_dir(output_path)
        input_durations = [self.get_duration_seconds(video_path) for video_path in video_paths]
        log_perf(
            "ffmpeg_assembly_inputs",
            video_count=len(video_paths),
            input_duration_seconds=sum(duration or 0 for duration in input_durations),
            output_path=output_path,
        )
        list_path = ""
        try:
            with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8") as list_file:
                for video_path in video_paths:
                    escaped = Path(video_path).as_posix().replace("'", "'\\''")
                    list_file.write(f"file '{escaped}'\n")
                list_path = list_file.name

            self._run([self.settings.ffmpeg_path, "-y", "-f", "concat", "-safe", "0", "-i", list_path, "-c", "copy", output_path], output_path=output_path)
        finally:
            if list_path:
                Path(list_path).unlink(missing_ok=True)

        if not Path(output_path).exists() or Path(output_path).stat().st_size <= 0:
            raise RuntimeError(f"ffmpeg stitch did not create a valid output: {output_path}")
        output_duration = self.get_duration_seconds(output_path)
        log_perf(
            "ffmpeg_assembly_completed",
            video_count=len(video_paths),
            output_path=output_path,
            ffprobe_output_duration_seconds=output_duration,
            duration_seconds=elapsed_seconds(assembly_start),
        )

    def stitch_video_segments(self, segments: List[Dict[str, Any]], output_path: str) -> None:
        assembly_start = now()
        if not segments:
            raise RuntimeError("No video segments were provided for stitching.")

        self._ensure_parent_dir(output_path)
        normalized_segments = [self._normalize_segment(segment) for segment in segments]
        missing = [segment["video_path"] for segment in normalized_segments if not Path(segment["video_path"]).exists()]
        if missing:
            raise RuntimeError(f"Cannot assemble missing video segment file(s): {missing}")

        source_durations = [self.get_duration_seconds(segment["video_path"]) for segment in normalized_segments]
        total_source_duration = sum(duration or 0 for duration in source_durations)
        total_target_duration = sum(segment["target_duration_seconds"] for segment in normalized_segments)
        log_perf(
            "ffmpeg_assembly_plan_validation_started",
            video_count=len(normalized_segments),
            total_target_duration_seconds=round(total_target_duration, 3),
            output_path=output_path,
        )
        if total_target_duration <= 0:
            log_perf(
                "ffmpeg_assembly_plan_validation_failed",
                video_count=len(normalized_segments),
                total_target_duration_seconds=round(total_target_duration, 3),
                output_path=output_path,
            )
            raise RuntimeError("Assembly plan has no positive target duration.")
        log_perf(
            "ffmpeg_assembly_plan_validation_completed",
            video_count=len(normalized_segments),
            total_target_duration_seconds=round(total_target_duration, 3),
            output_path=output_path,
        )
        log_perf(
            "ffmpeg_assembly_duration_plan",
            video_count=len(normalized_segments),
            total_source_duration_seconds=round(total_source_duration, 3),
            total_target_duration_seconds=round(total_target_duration, 3),
            output_path=output_path,
        )

        temp_dir = tempfile.TemporaryDirectory(prefix="videostudio_assembly_")
        list_path = ""
        segment_paths: List[str] = []
        try:
            for index, segment in enumerate(normalized_segments):
                source_duration = source_durations[index]
                segment_output = str(Path(temp_dir.name) / f"segment_{index + 1:04d}.mp4")
                segment_paths.append(segment_output)
                self._validate_extension_policy(segment, source_duration)
                log_perf(
                    "ffmpeg_assembly_selected_shot",
                    scene_index=segment.get("scene_index"),
                    shot_index=segment.get("shot_index"),
                    shot_id=segment.get("shot_id"),
                    render_job_id=segment.get("render_job_id"),
                    render_duration_mode=segment.get("render_duration_mode"),
                    video_path=segment["video_path"],
                    source_duration_seconds=source_duration,
                    target_duration_seconds=segment["target_duration_seconds"],
                    output_segment_path=segment_output,
                )
                self._run(
                    [
                        self.settings.ffmpeg_path,
                        "-y",
                        "-stream_loop",
                        "-1",
                        "-i",
                        segment["video_path"],
                        "-t",
                        f"{segment['target_duration_seconds']:.3f}",
                        "-an",
                        "-c:v",
                        "libx264",
                        "-preset",
                        "veryfast",
                        "-pix_fmt",
                        "yuv420p",
                        segment_output,
                    ],
                    video_path=segment["video_path"],
                    output_path=segment_output,
                )
                segment_duration = self.get_duration_seconds(segment_output)
                log_perf(
                    "ffmpeg_assembly_segment_normalized",
                    scene_index=segment.get("scene_index"),
                    shot_index=segment.get("shot_index"),
                    shot_id=segment.get("shot_id"),
                    render_job_id=segment.get("render_job_id"),
                    source_duration_seconds=source_duration,
                    target_shot_duration_seconds=segment["target_duration_seconds"],
                    output_segment_duration_seconds=segment_duration,
                )

            with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8") as list_file:
                for segment_path in segment_paths:
                    escaped = Path(segment_path).as_posix().replace("'", "'\\''")
                    list_file.write(f"file '{escaped}'\n")
                list_path = list_file.name

            self._run([self.settings.ffmpeg_path, "-y", "-f", "concat", "-safe", "0", "-i", list_path, "-c", "copy", output_path], output_path=output_path)
        finally:
            if list_path:
                Path(list_path).unlink(missing_ok=True)
            temp_dir.cleanup()

        if not Path(output_path).exists() or Path(output_path).stat().st_size <= 0:
            raise RuntimeError(f"ffmpeg duration-locked stitch did not create a valid output: {output_path}")
        output_duration = self.get_duration_seconds(output_path)
        tolerance_seconds = max(2.0, total_target_duration * 0.02)
        if output_duration is not None and abs(output_duration - total_target_duration) > tolerance_seconds:
            log_perf(
                "ffmpeg_assembly_output_duration_validation_failed",
                video_count=len(normalized_segments),
                total_target_duration_seconds=round(total_target_duration, 3),
                output_duration_seconds=output_duration,
                tolerance_seconds=round(tolerance_seconds, 3),
                output_path=output_path,
            )
            raise RuntimeError(
                "Assembled video duration does not match storyboard target duration. "
                f"target={total_target_duration:.3f}s output={output_duration:.3f}s tolerance={tolerance_seconds:.3f}s"
            )
        log_perf(
            "ffmpeg_assembly_output_duration_validation_completed",
            video_count=len(normalized_segments),
            total_target_duration_seconds=round(total_target_duration, 3),
            output_duration_seconds=output_duration,
            tolerance_seconds=round(tolerance_seconds, 3),
            output_path=output_path,
        )
        log_perf(
            "ffmpeg_assembly_completed",
            video_count=len(normalized_segments),
            output_path=output_path,
            total_target_duration_seconds=round(total_target_duration, 3),
            ffprobe_output_duration_seconds=output_duration,
            duration_seconds=elapsed_seconds(assembly_start),
        )

    def mux_audio(self, video_path: str, audio_path: str, output_path: str) -> None:
        self.mux_audio_preserve_video_duration(video_path, audio_path, output_path)

    def mux_audio_preserve_video_duration(self, video_path: str, audio_path: str, output_path: str) -> None:
        if not Path(video_path).exists():
            raise RuntimeError(f"Cannot mux missing video file: {video_path}")
        if not Path(audio_path).exists():
            raise RuntimeError(f"Cannot mux missing audio file: {audio_path}")

        self._ensure_parent_dir(output_path)
        video_duration = self.get_duration_seconds(video_path)
        audio_duration = self.get_duration_seconds(audio_path)
        mux_start = now()
        log_perf(
            "ffmpeg_mux_inputs",
            video_path=video_path,
            ffprobe_video_duration_seconds=video_duration,
            audio_path=audio_path,
            ffprobe_audio_duration_seconds=audio_duration,
            audio_shorter_than_video=self._is_audio_shorter(video_duration, audio_duration),
            audio_longer_than_video=self._is_audio_longer(video_duration, audio_duration),
            output_path=output_path,
        )
        self._run(
            [
                self.settings.ffmpeg_path,
                "-y",
                "-i",
                video_path,
                "-i",
                audio_path,
                "-filter_complex",
                "[1:a]apad",
                "-c:v",
                "copy",
                "-c:a",
                "aac",
                "-shortest",
                output_path,
            ],
            video_path=video_path,
            audio_path=audio_path,
            output_path=output_path,
        )

        if not Path(output_path).exists() or Path(output_path).stat().st_size <= 0:
            raise RuntimeError(f"ffmpeg mux did not create a valid output: {output_path}")

        output_duration = self.get_duration_seconds(output_path)
        log_perf(
            "ffmpeg_mux_completed",
            output_path=output_path,
            ffprobe_video_duration_seconds=video_duration,
            ffprobe_audio_duration_seconds=audio_duration,
            audio_shorter_than_video=self._is_audio_shorter(video_duration, audio_duration),
            audio_longer_than_video=self._is_audio_longer(video_duration, audio_duration),
            ffprobe_output_duration_seconds=output_duration,
            duration_seconds=elapsed_seconds(mux_start),
        )
        if video_duration is not None and output_duration is not None and output_duration + 0.5 < video_duration:
            raise RuntimeError(
                "Final video duration is shorter than assembled video. Audio padding/mux failed. "
                f"video_duration={video_duration:.3f}s output_duration={output_duration:.3f}s"
            )

    def burn_subtitles(self, video_path: str, srt_path: str, output_path: str) -> None:
        self._ensure_parent_dir(output_path)
        self._run([self.settings.ffmpeg_path, "-y", "-i", video_path, "-vf", f"subtitles={srt_path}", output_path])

    @staticmethod
    def _run(command: List[str], video_path: Optional[str] = None, audio_path: Optional[str] = None, output_path: Optional[str] = None) -> None:
        completed = subprocess.run(command, capture_output=True, text=True, check=False)
        if completed.returncode != 0:
            details = []
            if video_path is not None:
                details.append(f"video={video_path}")
            if audio_path is not None:
                details.append(f"audio={audio_path}")
            if output_path is not None:
                output_parent = Path(output_path).parent
                details.append(f"output={output_path}")
                details.append(f"output_parent_exists={output_parent.exists()}")
            detail_text = ", ".join(details)
            stderr = (completed.stderr or "").strip()
            stdout = (completed.stdout or "").strip()
            raise RuntimeError(f"ffmpeg failed ({detail_text}). stderr={stderr} stdout={stdout}")

    def get_duration_seconds(self, media_path: str) -> Optional[float]:
        ffprobe_path = self._ffprobe_path()
        command = [
            ffprobe_path,
            "-v",
            "error",
            "-show_entries",
            "format=duration",
            "-of",
            "default=noprint_wrappers=1:nokey=1",
            media_path,
        ]
        completed = subprocess.run(command, capture_output=True, text=True, check=False)
        if completed.returncode != 0:
            logging.warning("ffprobe failed for %s. stderr=%s", media_path, (completed.stderr or "").strip())
            return None

        try:
            return float((completed.stdout or "").strip())
        except ValueError:
            logging.warning("ffprobe returned invalid duration for %s: %s", media_path, completed.stdout)
            return None

    def _ffprobe_path(self) -> str:
        ffmpeg_path = Path(self.settings.ffmpeg_path)
        if ffmpeg_path.name.lower() in {"ffmpeg", "ffmpeg.exe"}:
            return str(ffmpeg_path.with_name("ffprobe.exe" if ffmpeg_path.suffix.lower() == ".exe" else "ffprobe"))
        return "ffprobe"

    @staticmethod
    def _ensure_parent_dir(output_path: str) -> None:
        Path(output_path).parent.mkdir(parents=True, exist_ok=True)

    @staticmethod
    def _normalize_segment(segment: Dict[str, Any]) -> Dict[str, Any]:
        video_path = segment.get("videoPath") or segment.get("video_path")
        if not video_path:
            raise RuntimeError(f"Assembly segment is missing videoPath: {segment}")

        target_duration = segment.get("targetDurationSeconds", segment.get("target_duration_seconds"))
        try:
            target_duration_seconds = float(target_duration)
        except (TypeError, ValueError):
            target_duration_seconds = 0.0
        if target_duration_seconds <= 0:
            raise RuntimeError(f"Assembly segment has invalid targetDurationSeconds: {segment}")

        return {
            "video_path": str(video_path),
            "project_id": segment.get("projectId") or segment.get("project_id"),
            "target_duration_seconds": target_duration_seconds,
            "shot_id": segment.get("shotId") or segment.get("shot_id"),
            "scene_id": segment.get("sceneId") or segment.get("scene_id"),
            "scene_index": segment.get("sceneIndex") or segment.get("scene_index"),
            "shot_index": segment.get("shotIndex") or segment.get("shot_index"),
            "render_job_id": segment.get("renderJobId") or segment.get("render_job_id"),
            "render_duration_mode": segment.get("renderDurationMode") or segment.get("render_duration_mode") or "FastPreview",
            "expected_raw_clip_duration_seconds": segment.get("expectedRawClipDurationSeconds") or segment.get("expected_raw_clip_duration_seconds"),
            "probed_raw_clip_duration_seconds": segment.get("probedRawClipDurationSeconds") or segment.get("probed_raw_clip_duration_seconds"),
            "raw_duration_coverage_percent": segment.get("rawDurationCoveragePercent") or segment.get("raw_duration_coverage_percent"),
        }

    @staticmethod
    def _validate_extension_policy(segment: Dict[str, Any], source_duration: Optional[float]) -> None:
        mode = str(segment.get("render_duration_mode") or "FastPreview")
        target_duration = float(segment["target_duration_seconds"])
        ratio = (source_duration / target_duration) if source_duration is not None and target_duration > 0 else 0.0
        if mode == "LongMotion":
            policy = "fail_severe_extension"
        elif mode == "CinematicPreview":
            policy = "allow_with_warning"
        else:
            policy = "allow_preview_extension"
        log_perf(
            "ffmpeg_assembly_extension_policy_selected",
            project_id=segment.get("project_id"),
            shot_id=segment.get("shot_id"),
            render_job_id=segment.get("render_job_id"),
            render_duration_mode=mode,
            source_duration_seconds=source_duration,
            target_shot_duration_seconds=target_duration,
            extension_ratio=round(ratio, 3),
            policy=policy,
        )
        if mode != "LongMotion" or ratio >= 0.75:
            return
        log_perf(
            "ffmpeg_assembly_source_too_short_for_longmotion",
            project_id=segment.get("project_id"),
            shot_id=segment.get("shot_id"),
            render_job_id=segment.get("render_job_id"),
            render_duration_mode=mode,
            source_duration_seconds=source_duration,
            target_shot_duration_seconds=target_duration,
            extension_ratio=round(ratio, 3),
            policy=policy,
        )
        log_perf(
            "ffmpeg_assembly_severe_extension_blocked",
            project_id=segment.get("project_id"),
            shot_id=segment.get("shot_id"),
            render_job_id=segment.get("render_job_id"),
            render_duration_mode=mode,
            source_duration_seconds=source_duration,
            target_shot_duration_seconds=target_duration,
            extension_ratio=round(ratio, 3),
            policy=policy,
        )
        log_perf(
            "ffmpeg_assembly_fake_duration_prevented",
            project_id=segment.get("project_id"),
            shot_id=segment.get("shot_id"),
            render_job_id=segment.get("render_job_id"),
            render_duration_mode=mode,
            source_duration_seconds=source_duration,
            target_shot_duration_seconds=target_duration,
            extension_ratio=round(ratio, 3),
            policy=policy,
        )
        raise RuntimeError(
            "LongMotion source clip is too short for duration-locked assembly. "
            f"source={source_duration or 0:.3f}s target={target_duration:.3f}s ratio={ratio:.3f}. "
            "Refusing to create fake loop-stretched long motion."
        )

    @staticmethod
    def _is_audio_shorter(video_duration: Optional[float], audio_duration: Optional[float]) -> bool:
        return video_duration is not None and audio_duration is not None and audio_duration + 0.5 < video_duration

    @staticmethod
    def _is_audio_longer(video_duration: Optional[float], audio_duration: Optional[float]) -> bool:
        return video_duration is not None and audio_duration is not None and audio_duration > video_duration + 0.5
