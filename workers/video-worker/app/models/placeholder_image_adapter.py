import logging
import textwrap
from pathlib import Path

from app.config import Settings
from app.models.base_image_model import ImageGenerationRequest, ImageResult


class PlaceholderImageAdapter:
    def __init__(self, settings: Settings) -> None:
        self.settings = settings

    def generate(self, request: ImageGenerationRequest) -> ImageResult:
        if not self.settings.placeholder_outputs:
            return ImageResult(
                success=False,
                error_message=(
                    "Image generation adapter is not configured yet. "
                    "Enable VIDEOSTUDIO_PLACEHOLDER_OUTPUTS=true for local placeholder PNGs, "
                    "or upload images manually."
                ),
            )

        try:
            from PIL import Image, ImageDraw
        except ImportError:
            return ImageResult(
                success=False,
                error_message="Pillow is not installed, so placeholder image output cannot be created.",
            )

        output_path = Path(request.output_path)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        logging.info("Creating placeholder image at %s", output_path)

        image = Image.new("RGB", (1280, 704), color=(18, 24, 32))
        draw = ImageDraw.Draw(image)
        title = "VideoStudio placeholder image"
        wrapped = textwrap.wrap(request.prompt or "No prompt supplied.", width=78)[:14]
        draw.text((48, 42), title, fill=(230, 235, 240))
        y = 96
        for line in wrapped:
            draw.text((48, y), line, fill=(190, 202, 214))
            y += 32
        image.save(output_path, format="PNG")

        if not output_path.exists() or output_path.stat().st_size == 0:
            return ImageResult(success=False, error_message=f"Placeholder image was not written: {output_path}")

        return ImageResult(success=True, output_path=str(output_path))
