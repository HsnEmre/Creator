from app.config import Settings
from app.models.base_image_model import ImageGenerationRequest, ImageResult
from app.models.placeholder_image_adapter import PlaceholderImageAdapter
from app.models.sdxl_image_adapter import SDXLImageAdapter


class UnsupportedImageAdapter:
    def __init__(self, provider: str) -> None:
        self.provider = provider

    def generate(self, request: ImageGenerationRequest) -> ImageResult:
        return ImageResult(
            success=False,
            error_message=f"Image provider {self.provider} is not implemented yet. Use SDXL or PLACEHOLDER.",
        )


def create_image_adapter(settings: Settings):
    provider = settings.image_model_provider.upper()
    if provider == "SDXL":
        return SDXLImageAdapter(settings)
    if provider == "PLACEHOLDER":
        return PlaceholderImageAdapter(settings)
    return UnsupportedImageAdapter(provider)
