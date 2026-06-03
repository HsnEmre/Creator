import logging
import time
from contextlib import contextmanager
from typing import Any, Dict, Iterator, Optional


def log_perf(event: str, **fields: Any) -> None:
    parts = [f"event={event}"]
    for key, value in fields.items():
        parts.append(f"{key}={_format_value(value)}")
    logging.info("[perf] %s", " ".join(parts))


@contextmanager
def timed(event: str, **fields: Any) -> Iterator[None]:
    start = time.perf_counter()
    try:
        yield
    finally:
        elapsed = time.perf_counter() - start
        log_perf(event, duration_seconds=round(elapsed, 3), **fields)


def now() -> float:
    return time.perf_counter()


def elapsed_seconds(start: float) -> float:
    return round(time.perf_counter() - start, 3)


def torch_diagnostics() -> Dict[str, Any]:
    try:
        import torch
    except Exception as exc:
        return {
            "torch_available": False,
            "torch_error": str(exc),
            "cuda_available": False,
        }

    diagnostics: Dict[str, Any] = {
        "torch_available": True,
        "torch_version": getattr(torch, "__version__", None),
        "cuda_version": getattr(getattr(torch, "version", None), "cuda", None),
        "cuda_available": bool(torch.cuda.is_available()),
    }
    if diagnostics["cuda_available"]:
        try:
            device_index = torch.cuda.current_device()
            diagnostics.update(
                {
                    "cuda_device_index": device_index,
                    "gpu_name": torch.cuda.get_device_name(device_index),
                    "cuda_device_capability": ".".join(str(part) for part in torch.cuda.get_device_capability(device_index)),
                    "cuda_current_device": str(torch.device(f"cuda:{device_index}")),
                    "vram_allocated_mb": round(torch.cuda.memory_allocated(device_index) / 1024 / 1024, 2),
                    "vram_reserved_mb": round(torch.cuda.memory_reserved(device_index) / 1024 / 1024, 2),
                    "peak_vram_allocated_mb": round(torch.cuda.max_memory_allocated(device_index) / 1024 / 1024, 2),
                    "peak_vram_reserved_mb": round(torch.cuda.max_memory_reserved(device_index) / 1024 / 1024, 2),
                }
            )
        except Exception as exc:
            diagnostics["cuda_probe_error"] = str(exc)

    return diagnostics


def cuda_memory_snapshot(prefix: str) -> Dict[str, Any]:
    diagnostics = torch_diagnostics()
    result: Dict[str, Any] = {
        f"{prefix}_cuda_available": diagnostics.get("cuda_available"),
    }
    if diagnostics.get("cuda_available"):
        result[f"{prefix}_vram_allocated_mb"] = diagnostics.get("vram_allocated_mb")
        result[f"{prefix}_vram_reserved_mb"] = diagnostics.get("vram_reserved_mb")
        result[f"{prefix}_peak_vram_allocated_mb"] = diagnostics.get("peak_vram_allocated_mb")
        result[f"{prefix}_peak_vram_reserved_mb"] = diagnostics.get("peak_vram_reserved_mb")
    return result


def _format_value(value: Any) -> str:
    if value is None:
        return "null"
    text = str(value).replace("\n", "\\n").replace("\r", "")
    if " " in text:
        return f'"{text}"'
    return text
