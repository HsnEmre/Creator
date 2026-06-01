import logging
import subprocess
import tempfile
from pathlib import Path
from typing import List, Optional

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
