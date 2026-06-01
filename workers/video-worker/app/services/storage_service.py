from pathlib import Path


class StorageService:
    def ensure_directory(self, path: str) -> str:
        directory = Path(path)
        directory.mkdir(parents=True, exist_ok=True)
        return str(directory)
