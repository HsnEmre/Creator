from dataclasses import dataclass
from typing import Optional, Protocol, Tuple


@dataclass(frozen=True)
class ImageGenerationRequest:
    job_id: str
    project_id: str
    prompt: str
    negative_prompt: Optional[str]
    output_path: str
    width: Optional[int] = None
    height: Optional[int] = None
    seed: Optional[int] = None
    model_name: Optional[str] = None
    generation_type: str = "Image"
    character_id: Optional[str] = None
    shot_id: Optional[str] = None

    @staticmethod
    def from_job(job: dict) -> "ImageGenerationRequest":
        output_path = job.get("outputPath")
        if not output_path:
            raise RuntimeError("Image generation job is missing outputPath.")

        width, height = _parse_size(job.get("size"))
        return ImageGenerationRequest(
            job_id=job["id"],
            project_id=job["projectId"],
            prompt=job.get("prompt") or "",
            negative_prompt=job.get("negativePrompt"),
            output_path=output_path,
            width=width,
            height=height,
            seed=job.get("seed"),
            model_name=job.get("modelName"),
            generation_type=_generation_type(job.get("jobType")),
            character_id=job.get("characterId"),
            shot_id=job.get("shotId"),
        )


@dataclass(frozen=True)
class ImageResult:
    success: bool
    output_path: Optional[str] = None
    error_message: Optional[str] = None
    logs: str = ""


class BaseImageModel(Protocol):
    def generate(self, request: ImageGenerationRequest) -> ImageResult:
        ...


def _parse_size(value: Optional[str]) -> Tuple[Optional[int], Optional[int]]:
    if not value:
        return None, None
    normalized = str(value).lower().replace("x", "*")
    parts = normalized.split("*")
    if len(parts) != 2:
        return None, None
    try:
        return int(parts[0]), int(parts[1])
    except ValueError:
        return None, None


def _generation_type(job_type: object) -> str:
    value = str(job_type or "")
    if value == "7" or value == "GenerateCharacterReferenceImage":
        return "CharacterReference"
    if value == "8" or value == "GenerateShotStartImage":
        return "ShotStartImage"
    return "Image"
