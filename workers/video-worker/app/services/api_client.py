import json
import urllib.error
import urllib.request
from typing import Optional, Tuple


class ApiClient:
    def __init__(self, base_url: str) -> None:
        self.base_url = base_url.rstrip("/")

    def next_job(self) -> Optional[dict]:
        status, data = self._request("POST", "/api/worker/jobs/next")
        if status == 204:
            return None
        return data

    def get_job(self, job_id: str) -> dict:
        _, data = self._request("GET", f"/api/worker/jobs/{job_id}")
        return data

    def start_job(self, job_id: str) -> dict:
        _, data = self._request("POST", f"/api/worker/jobs/{job_id}/start")
        return data

    def complete_job(self, job_id: str, output_path: str) -> dict:
        _, data = self._request("POST", f"/api/worker/jobs/{job_id}/complete", {"outputPath": output_path})
        return data

    def fail_job(self, job_id: str, error_message: str) -> dict:
        _, data = self._request("POST", f"/api/worker/jobs/{job_id}/fail", {"errorMessage": error_message})
        return data

    def update_progress(self, job_id: str, progress: int) -> dict:
        _, data = self._request("POST", f"/api/worker/jobs/{job_id}/progress", {"progress": progress})
        return data

    def _request(self, method: str, path: str, body: Optional[dict] = None) -> Tuple[int, Optional[dict]]:
        payload = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(
            f"{self.base_url}{path}",
            data=payload,
            method=method,
            headers={"Content-Type": "application/json"},
        )
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                if response.status == 204:
                    return response.status, None
                text = response.read().decode("utf-8")
                return response.status, json.loads(text) if text else None
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8")
            raise RuntimeError(f"API request failed {exc.code}: {detail}") from exc
