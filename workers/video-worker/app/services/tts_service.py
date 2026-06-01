import asyncio
from pathlib import Path

import edge_tts


class TtsService:
    def generate_edge_tts(self, text: str, voice: str, output_path: str) -> str:
        if not text or not text.strip():
            raise ValueError("TTS text is empty.")

        target = Path(output_path)
        target.parent.mkdir(parents=True, exist_ok=True)
        asyncio.run(self._generate(text.strip(), voice, str(target)))
        if not target.exists() or target.stat().st_size <= 0:
            raise RuntimeError(f"Edge TTS did not produce a valid file at {target}")
        return str(target)

    @staticmethod
    async def _generate(text: str, voice: str, output_path: str) -> None:
        communicate = edge_tts.Communicate(text=text, voice=voice)
        await communicate.save(output_path)
