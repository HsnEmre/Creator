from app.config import Settings
from app.models.base_video_model import RenderRequest, RenderResult


class Wan22AnimateAdapter:
    def __init__(self, settings: Settings) -> None:
        self.settings = settings

    def render(self, request: RenderRequest) -> RenderResult:
        return RenderResult(
            success=False,
            error_message="Wan2.2 animate/video-to-video adapter is not implemented yet. Expected reference video + prompt.",
        )
