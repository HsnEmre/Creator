import json
import logging
import subprocess
import sys
import threading
import time
from os import environ
from pathlib import Path
from queue import Empty, Queue
from typing import Any, Dict, List, Optional

from app.config import Settings
from app.models.base_video_model import RenderRequest, RenderResult
from app.services.performance_diagnostics import cuda_memory_snapshot, elapsed_seconds, log_perf, now


class Wan22PersistentClient:
    def __init__(self, settings: Settings) -> None:
        self.settings = settings
        self._process: Optional[subprocess.Popen] = None
        self._reader_thread: Optional[threading.Thread] = None
        self._lines: "Queue[str]" = Queue()
        self._lock = threading.Lock()

    def render(self, request: RenderRequest, output_path: Path) -> RenderResult:
        with self._lock:
            try:
                self._ensure_started()
                payload = self._build_render_payload(request, output_path)
                start = now()
                self._send(payload)
                log_perf(
                    "wan_persistent_render_request_sent",
                    request_id=payload["request_id"],
                    job_id=request.job_id,
                    project_id=request.project_id,
                    output_path=str(output_path),
                    **cuda_memory_snapshot("before_wan_persistent_render"),
                )
                return self._wait_for_result(payload["request_id"], start)
            except Exception as exc:
                logging.exception("Persistent Wan2.2 render failed for job %s.", request.job_id)
                self._terminate()
                return RenderResult(False, error_message=f"Persistent Wan2.2 render failed: {exc}")

    def close(self) -> None:
        with self._lock:
            if self._process is None:
                return
            try:
                if self._process.poll() is None:
                    self._send({"command": "shutdown"})
                    try:
                        self._process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        self._terminate()
            except Exception:
                self._terminate()
            finally:
                self._process = None

    def _ensure_started(self) -> None:
        if self._process is not None and self._process.poll() is None:
            return

        server_path = Path(self.settings.wan22_repo_dir) / "warm_ti2v_server.py"
        if not server_path.exists():
            raise RuntimeError(f"Wan2.2 warm server not found: {server_path}")

        python_exe = self.settings.wan22_python_exe or "python"
        process_env = self._process_env()
        command = [python_exe, "warm_ti2v_server.py"]
        logging.info("Starting persistent Wan2.2 server: %s", " ".join(command))
        self._process = subprocess.Popen(
            command,
            cwd=self.settings.wan22_repo_dir,
            env=process_env,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        self._lines = Queue()
        self._reader_thread = threading.Thread(target=self._read_stdout, daemon=True)
        self._reader_thread.start()
        self._wait_for_ready()

    def _process_env(self) -> Dict[str, str]:
        process_env = dict(environ)
        process_env["PYTHONIOENCODING"] = "utf-8"
        process_env["PYTHONUTF8"] = "1"
        if self.settings.performance_diagnostics:
            process_env["WAN_PERF_LOG"] = "1"
        if self.settings.wan22_vae_dtype:
            process_env["WAN_VAE_DTYPE"] = self.settings.wan22_vae_dtype
        if self.settings.wan22_torch_optimize:
            process_env["WAN_TORCH_OPTIMIZE"] = "1"
        return process_env

    def _read_stdout(self) -> None:
        process = self._process
        if process is None or process.stdout is None:
            return
        for line in process.stdout:
            clean = line.rstrip()
            self._lines.put(clean)

    def _wait_for_ready(self) -> None:
        start = now()
        while elapsed_seconds(start) < 30:
            self._raise_if_exited()
            line = self._next_line(timeout=1)
            if line is None:
                continue
            event = self._handle_line(line)
            if event and event.get("type") == "ready":
                log_perf(
                    "wan_persistent_server_ready",
                    duration_seconds=elapsed_seconds(start),
                    wan_perf_log=self._process_env().get("WAN_PERF_LOG", "0"),
                    wan_vae_dtype=self._process_env().get("WAN_VAE_DTYPE", "wan_default"),
                    wan_torch_optimize=self._process_env().get("WAN_TORCH_OPTIMIZE", "0"),
                )
                return
        raise TimeoutError("Persistent Wan2.2 server did not become ready within 30 seconds.")

    def _wait_for_result(self, request_id: str, start) -> RenderResult:
        collected: List[str] = []
        while elapsed_seconds(start) < self.settings.wan22_timeout_seconds:
            self._raise_if_exited()
            line = self._next_line(timeout=1)
            if line is None:
                continue
            collected.append(line)
            event = self._handle_line(line)
            if not event or event.get("type") != "result":
                continue
            if event.get("request_id") != request_id:
                logging.warning("Ignoring Wan2.2 result for unexpected request_id=%s", event.get("request_id"))
                continue

            log_perf(
                "wan_persistent_render_result_received",
                request_id=request_id,
                ok=event.get("ok"),
                duration_seconds=elapsed_seconds(start),
                **cuda_memory_snapshot("after_wan_persistent_render"),
            )
            if event.get("ok"):
                return RenderResult(True, output_path=event.get("save_file"), stdout="\n".join(collected))
            return RenderResult(False, error_message=event.get("error") or "Persistent Wan2.2 render failed.", stdout="\n".join(collected), stderr="\n".join(collected))

        self._terminate()
        return RenderResult(
            False,
            error_message=f"Persistent Wan2.2 render timed out after {self.settings.wan22_timeout_seconds} seconds.",
            stdout="\n".join(collected),
            stderr="\n".join(collected),
        )

    def _next_line(self, timeout: float) -> Optional[str]:
        try:
            return self._lines.get(timeout=timeout)
        except Empty:
            return None

    def _handle_line(self, line: str) -> Optional[Dict[str, Any]]:
        if not line:
            return None
        if line.startswith("{"):
            try:
                event = json.loads(line)
            except json.JSONDecodeError:
                logging.info("[wan22-warm] %s", self._safe_for_console(line))
                return None
            if event.get("type") == "log":
                logging.info("[wan22-warm] %s", self._safe_for_console(str(event.get("message") or "")))
            elif event.get("type") not in {"ready", "result"}:
                logging.info("[wan22-warm] %s", self._safe_for_console(line))
            return event
        logging.info("[wan22-warm] %s", self._safe_for_console(line))
        return None

    def _send(self, payload: Dict[str, Any]) -> None:
        if self._process is None or self._process.stdin is None:
            raise RuntimeError("Persistent Wan2.2 server is not running.")
        self._raise_if_exited()
        self._process.stdin.write(json.dumps(payload, ensure_ascii=False) + "\n")
        self._process.stdin.flush()

    def _raise_if_exited(self) -> None:
        if self._process is not None and self._process.poll() is not None:
            raise RuntimeError(f"Persistent Wan2.2 server exited with code {self._process.returncode}.")

    def _terminate(self) -> None:
        process = self._process
        self._process = None
        if process is None:
            return
        if process.poll() is not None:
            return
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)

    def _build_render_payload(self, request: RenderRequest, output_path: Path) -> Dict[str, Any]:
        return {
            "command": "render_ti2v",
            "request_id": request.job_id,
            "job_id": request.job_id,
            "project_id": request.project_id,
            "task": "ti2v-5B",
            "ckpt_dir": self.settings.wan22_ti2v_ckpt_dir,
            "prompt": request.compiled_prompt or request.prompt,
            "image": request.image_path,
            "size": request.size or self.settings.wan22_default_size,
            "frame_num": request.frame_num or self.settings.wan22_default_frame_num,
            "sample_steps": request.sample_steps or self.settings.wan22_default_sample_steps,
            "sample_shift": None,
            "sample_guide_scale": None,
            "offload_model": self.settings.wan22_default_offload_model,
            "convert_model_dtype": self.settings.wan22_default_convert_model_dtype,
            "t5_cpu": self.settings.wan22_default_t5_cpu,
            "vae_dtype": self.settings.wan22_vae_dtype or None,
            "save_file": str(output_path),
            "base_seed": request.seed if request.seed is not None else -1,
        }

    @staticmethod
    def _safe_for_console(value: str) -> str:
        output_encoding = getattr(sys.stdout, "encoding", None) or "utf-8"
        try:
            return value.encode(output_encoding, errors="replace").decode(output_encoding, errors="replace")
        except Exception:
            return value.encode("utf-8", errors="replace").decode("utf-8", errors="replace")
