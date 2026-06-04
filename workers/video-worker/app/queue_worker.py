import logging
import time
from typing import Optional

from app.config import Settings
from app.jobs.render_job_handler import RenderJobHandler
from app.services.performance_diagnostics import elapsed_seconds, log_perf, now
from app.services.api_client import ApiClient


logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")


class QueueWorker:
    def __init__(self, settings: Optional[Settings] = None) -> None:
        self.settings = settings or Settings.from_env()
        self.api = ApiClient(self.settings.api_base_url)
        self.handler = RenderJobHandler(self.api, self.settings)
        self._log_configuration()
        self._prewarm_wan_if_requested()

    def run(self) -> None:
        logging.info("Video worker polling %s", self.settings.api_base_url)
        while True:
            try:
                job = self.api.next_job()
                if job is None:
                    time.sleep(self.settings.poll_interval_seconds)
                    continue

                self.handler.handle(job)
            except KeyboardInterrupt:
                self.close()
                raise
            except Exception:
                logging.exception("Worker loop recovered from an unexpected error.")
                time.sleep(self.settings.poll_interval_seconds)

    def close(self) -> None:
        self.handler.close()

    def _log_configuration(self) -> None:
        logging.info("VIDEO_API_BASE_URL=%s", self.settings.api_base_url)
        logging.info("WAN22_REPO_DIR=%s", self.settings.wan22_repo_dir)
        logging.info("WAN22_TI2V_CKPT_DIR=%s", self.settings.wan22_ti2v_ckpt_dir)
        logging.info("WAN22_OUTPUT_DIR=%s", self.settings.wan22_output_dir)
        logging.info("WAN22_DEFAULT_FRAME_NUM=%s", self.settings.wan22_default_frame_num)
        logging.info("WAN22_DEFAULT_SAMPLE_STEPS=%s", self.settings.wan22_default_sample_steps)
        logging.info("WAN22_VAE_DTYPE=%s", self.settings.wan22_vae_dtype or "(default)")
        logging.info("WAN22_TORCH_OPTIMIZE=%s", self.settings.wan22_torch_optimize)
        logging.info("WAN22_PERSISTENT_PIPELINE=%s", self.settings.wan22_persistent_pipeline)
        logging.info("WAN22_PREWARM_ON_START=%s", self.settings.wan22_prewarm_on_start)
        logging.info("VIDEOSTUDIO_PLACEHOLDER_OUTPUTS=%s", self.settings.placeholder_outputs)
        logging.info("IMAGE_MODEL_PROVIDER=%s", self.settings.image_model_provider)
        logging.info("SDXL_MODEL_PATH=%s", self.settings.sdxl_model_path)
        logging.info("SDXL_DEVICE=%s", self.settings.sdxl_device)
        logging.info("SDXL_SIZE=%sx%s", self.settings.sdxl_width, self.settings.sdxl_height)
        logging.info("SDXL_NUM_INFERENCE_STEPS=%s", self.settings.sdxl_num_inference_steps)
        logging.info("SDXL_UNLOAD_AFTER_JOB=%s", self.settings.sdxl_unload_after_job)
        logging.info("IMAGE_OUTPUT_DIR=%s", self.settings.image_output_dir)
        logging.info("VIDEO_WORKER_PERF_LOG=%s", self.settings.performance_diagnostics)

    def _prewarm_wan_if_requested(self) -> None:
        if not self.settings.wan22_prewarm_on_start:
            return

        start = now()
        log_perf(
            "wan_prewarm_started",
            persistent_pipeline=self.settings.wan22_persistent_pipeline,
            prewarm_mode="persistent_server_health",
            loads_pipeline=False,
        )
        try:
            prewarm = getattr(self.handler.ti2v, "prewarm", None)
            if callable(prewarm):
                prewarm()
            else:
                logging.warning("WAN22_PREWARM_ON_START requested but TI2V adapter has no prewarm hook.")
        except Exception:
            logging.warning("Wan2.2 prewarm failed; normal render startup will retry later.", exc_info=True)
            log_perf("wan_prewarm_failed", duration_seconds=elapsed_seconds(start))
