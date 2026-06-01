# Architecture

VideoStudio is a local-first AI video generation platform skeleton.

## Responsibilities

- ASP.NET Core Web API: project metadata, story planning, production plans, assets, final-video metadata, and the database-backed render queue.
- SQL Server with EF Core: development defaults to SQL Server LocalDB.
- Python video worker: polls the API, selects model adapters, runs Wan2.2/FFmpeg work, and reports status back to the API.
- Ollama: story and production-plan planning through `/api/chat`.
- Local filesystem: development storage for assets, renders, audio, and finals.

## Non-goals

- No node-based workflow files.
- No GPU inference in the ASP.NET Core API.
- No direct database access from Python workers.

## Production Direction

The application starts on SQL Server, and storage is behind explicit options. This keeps the skeleton ready for later S3-compatible object storage without changing the worker contract.
