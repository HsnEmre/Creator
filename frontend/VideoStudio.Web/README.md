# VideoStudio.Web

React + Vite frontend for the local AI video generation platform.

## Prerequisites

- Backend API running at `http://localhost:5281`
- Python worker running for render/audio/finalize job processing

## Environment

Copy `.env.example` to `.env`:

```env
VITE_API_BASE_URL=http://localhost:5281
```

## Run

```bash
npm install
npm run dev
```

Vite dev server will print a local URL, usually `http://localhost:5173`.

## Supported flow

1. Create project
2. Edit/save story
3. Analyze
4. View production plan (characters/scenes/shots)
5. Render FastPreview (`preset=0`, `maxShots=1`, `force=true`)
6. Generate audio (`force=false` by default) or regenerate (`force=true`)
7. Finalize video (`force=false` by default) or re-finalize (`force=true`)
8. Observe render job status updates

Generated files are served by backend media routes and can be previewed in browser players:

- `/media/renders/{projectId}/{fileName}`
- `/media/audio/{projectId}/{fileName}`
- `/media/finals/{projectId}/{fileName}`

If a media URL is present (`outputUrl`, `audioUrl`, `mediaUrl`), the UI uses it for playback.
