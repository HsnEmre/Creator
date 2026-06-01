# Deployment

This skeleton is intentionally local-first.

## Development

- SQL Server database: `VideoStudioDb` on `(localdb)\MSSQLLocalDB` by default
- API: `backend/VideoStudio.Api`
- Worker: `workers/video-worker`
- Storage: local folders under `storage`
- Ollama: local service at `http://localhost:11434`

## Later Production Changes

- Override `ConnectionStrings__DefaultConnection` for a production SQL Server instance.
- Replace local filesystem paths with an S3-compatible storage implementation.
- Run Python workers on GPU-enabled hosts.
- Keep the API inference-free; it should continue to coordinate jobs only.
