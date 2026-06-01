# Audio Pipeline

The audio pipeline is queue-driven and runs entirely in the Python worker.

## Flow

1. `POST /api/projects/{id}/audio/generate` creates `GenerateAudio` render jobs from persisted dialogue lines.
2. Worker polls `POST /api/worker/jobs/next`.
3. For `GenerateAudio` jobs, worker uses Edge TTS (`edge-tts`) to generate MP3 files.
4. Worker marks job completed and API stores the resulting `AudioPath` on the `DialogueLine`.
5. `POST /api/projects/{id}/finalize` creates a `MuxAudio` job using a completed video and generated audio.
6. Worker runs FFmpeg mux (`-c:v copy -c:a aac -shortest`) and returns final MP4 output.

## Notes

- Wan2.2 generates video only.
- TTS is generated separately from dialogue lines.
- FFmpeg performs muxing into final videos.
- ASP.NET Core API coordinates jobs and status only; it does not run GPU inference or TTS synthesis directly.
- Future enhancements can add lip-sync and S2V alignment.
