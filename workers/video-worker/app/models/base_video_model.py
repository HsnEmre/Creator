from dataclasses import dataclass
from typing import Optional, Protocol


@dataclass(frozen=True)
class RenderRequest:
    job_id: str
    project_id: str
    job_type: str
    generation_mode: str
    scene_id: Optional[str]
    shot_id: Optional[str]
    character_id: Optional[str]
    prompt: str
    negative_prompt: Optional[str]
    image_path: Optional[str]
    video_path: Optional[str]
    audio_path: Optional[str]
    output_path: Optional[str]
    size: Optional[str]
    frame_num: Optional[int]
    sample_steps: Optional[int]
    seed: Optional[int]
    scene_index: Optional[int]
    shot_index: Optional[int]
    render_duration_mode: Optional[str]
    requested_shot_duration_seconds: Optional[int]
    requested_frame_num: Optional[int]
    actual_frame_num: Optional[int]
    expected_raw_clip_duration_seconds: Optional[float]
    probed_raw_clip_duration_seconds: Optional[float]
    raw_duration_coverage_percent: Optional[int]
    compiled_prompt: Optional[str]
    preset: Optional[str]
    text_content: Optional[str]
    speaker: Optional[str]
    emotion: Optional[str]
    language: Optional[str]
    voice: Optional[str]

    @staticmethod
    def from_job(job: dict) -> "RenderRequest":
        return RenderRequest(
            job_id=job["id"],
            project_id=job["projectId"],
            job_type=str(job.get("jobType") or ""),
            generation_mode=job["generationMode"],
            scene_id=job.get("sceneId"),
            shot_id=job.get("shotId"),
            character_id=job.get("characterId"),
            prompt=job.get("prompt") or "",
            negative_prompt=job.get("negativePrompt"),
            image_path=job.get("inputImagePath"),
            video_path=job.get("inputVideoPath"),
            audio_path=job.get("inputAudioPath"),
            output_path=job.get("outputPath"),
            size=job.get("size"),
            frame_num=job.get("frameNum"),
            sample_steps=job.get("sampleSteps"),
            seed=job.get("seed"),
            scene_index=job.get("sceneIndex"),
            shot_index=job.get("shotIndex"),
            render_duration_mode=job.get("renderDurationMode"),
            requested_shot_duration_seconds=job.get("requestedShotDurationSeconds"),
            requested_frame_num=job.get("requestedFrameNum"),
            actual_frame_num=job.get("actualFrameNum"),
            expected_raw_clip_duration_seconds=job.get("expectedRawClipDurationSeconds"),
            probed_raw_clip_duration_seconds=job.get("probedRawClipDurationSeconds"),
            raw_duration_coverage_percent=job.get("rawDurationCoveragePercent"),
            compiled_prompt=job.get("compiledPrompt"),
            preset=job.get("preset"),
            text_content=job.get("textContent"),
            speaker=job.get("speaker"),
            emotion=job.get("emotion"),
            language=job.get("language"),
            voice=job.get("voice"),
        )


@dataclass(frozen=True)
class RenderResult:
    success: bool
    output_path: Optional[str] = None
    error_message: Optional[str] = None
    stdout: str = ""
    stderr: str = ""
    probed_raw_clip_duration_seconds: Optional[float] = None


class BaseVideoModel(Protocol):
    def render(self, request: RenderRequest) -> RenderResult:
        ...
