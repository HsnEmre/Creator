from pathlib import Path

from dotenv import load_dotenv


def _load_worker_dotenv() -> None:
    worker_root = Path(__file__).resolve().parent
    dotenv_path = worker_root / ".env"
    load_dotenv(dotenv_path=dotenv_path, override=False)


if __name__ == "__main__":
    _load_worker_dotenv()
    from app.queue_worker import QueueWorker

    QueueWorker().run()
