# MagicLight-Style UI Implementation Plan

## Scope

This plan converts the current Phase 1 internal production board into a four-step creator wizard:

1. Content
2. Cast
3. Storyboard
4. Edit

The implementation should keep the existing API calls, RenderJob flow, Python worker architecture, SQL Server database, Wan2.2 adapter behavior, render settings, and local media serving.

## Phase 2A: Wizard Shell and Top Stepper

Goal: replace the ten-lane production board shell with a four-step navigation shell.

Files:

- `frontend/VideoStudio.Web/src/pages/ProjectDetailPage.jsx`
  - Keep existing data loading and handlers.
  - Replace production lane rendering with active-step rendering.
  - Add `selectedStep` state.
  - Add derived readiness state for Content, Cast, Storyboard, Edit.
  - Keep `VideoPreviewPanel` and a compact job summary visible.

- `frontend/VideoStudio.Web/src/components/CreatorStepper.jsx` new
  - Four tabs: Content, Cast, Storyboard, Edit.
  - Shows ready/current/done/warning state.
  - Keyboard accessible buttons.

- `frontend/VideoStudio.Web/src/components/CreatorShell.jsx` new
  - Top bar, stepper, main content area, right preview rail.
  - Keeps layout separate from business handlers.

- `frontend/VideoStudio.Web/src/components/WorkflowStageBar.jsx`
  - Retire or keep temporarily behind a development-only flag.

- `frontend/VideoStudio.Web/src/components/WorkflowLane.jsx`
  - Retire or reuse as an internal section wrapper only if it remains visually light.

- `frontend/VideoStudio.Web/src/styles.css`
  - Add wizard shell, stepper, content area, preview rail styles.
  - Keep existing card/list styles where useful.

Acceptance:

- The project detail page shows four top-level steps.
- No backend calls change.
- User can move between steps without losing loaded data.
- `npm.cmd run build` passes.

## Phase 2B: Content Page

Goal: make story input, analysis, and plan review feel like a creator intake page.

Files:

- `frontend/VideoStudio.Web/src/components/ContentStep.jsx` new
  - Hosts story editor.
  - Shows project title, duration, story text, analyze controls.
  - Shows compact plan summary after analyze.
  - Shows scene/shot/dialogue summary.

- `frontend/VideoStudio.Web/src/components/StoryEditor.jsx`
  - Rename visible label from "Story Editor" to creator-facing "Story or Script" where appropriate.
  - Keep textarea and save behavior.
  - Add optional helper text via props.

- `frontend/VideoStudio.Web/src/components/ProductionPlanViewer.jsx`
  - Split plan summary into a compact component:
    - `PlanSummaryPanel.jsx`
    - `SceneOutlinePanel.jsx`
  - Avoid showing all character/keyframe controls on Content.

- `frontend/VideoStudio.Web/src/components/DialogueLinesPanel.jsx`
  - Add compact read-only mode for Content review.

API use:

- `GET /api/projects/{id}`
- `POST /api/projects/{id}/story`
- `POST /api/projects/{id}/analyze`
- `GET /api/projects/{id}/production-plan`
- `GET /api/projects/{id}/dialogue-lines`

Acceptance:

- Empty Content step explains what to enter.
- Analyze loading and error states are clear.
- Successful analyze pushes user toward Cast.

## Phase 2C: Cast Page

Goal: let the user review detected characters and create identity references.

Files:

- `frontend/VideoStudio.Web/src/components/CastStep.jsx` new
  - Character grid/list.
  - Reference generation actions.
  - Missing reference summary.

- `frontend/VideoStudio.Web/src/components/CharacterCard.jsx` new
  - Thumbnail slot.
  - Name, role, voice, visual lock.
  - Status badge.
  - Generate, upload, edit prompt actions.

- `frontend/VideoStudio.Web/src/components/CharacterList.jsx`
  - Either retire in favor of `CharacterCard` or convert to a thin grid wrapper.

- `frontend/VideoStudio.Web/src/components/VisualPreparationPanel.jsx`
  - Split character-reference editing into `CharacterReferencePromptEditor.jsx`.
  - Remove mixed character/shot responsibilities.

API use:

- `GET /api/projects/{id}/production-plan`
- `GET /api/projects/{id}/preproduction`
- `POST /api/projects/{id}/preproduction/prepare`
- `PATCH /api/projects/{projectId}/characters/{characterId}/reference-prompt`
- `POST /api/projects/{projectId}/characters/{characterId}/reference-image`
- `POST /api/projects/{id}/visuals/generate-character-references`
- `GET /api/projects/{id}/render-jobs`

Acceptance:

- Character reference images are clearly represented as identity guides.
- UI states cover missing, queued, generating, ready, uploaded, failed.
- The page does not imply character portraits are Wan2.2 start frames.

## Phase 2D: Storyboard Editor Page

Goal: create the main storyboard interaction surface.

Files:

- `frontend/VideoStudio.Web/src/components/StoryboardStep.jsx` new
  - Layout owner for shot rail, preview, inspector.

- `frontend/VideoStudio.Web/src/components/ShotThumbnailRail.jsx` new
  - Left rail grouped by scene.
  - Shows shot index, status, duration, thumbnail, selected state.

- `frontend/VideoStudio.Web/src/components/ShotPreviewPanel.jsx` new
  - Center keyframe/render preview.
  - Narration/subtitle area.
  - Generation history summary.

- `frontend/VideoStudio.Web/src/components/ShotInspectorPanel.jsx` new
  - Right panel with on-screen characters, prompt editor, negative prompt, continuity notes.
  - Upload custom keyframe.
  - Regenerate keyframe.
  - Animate selected shot.
  - Animate all shots.

- `frontend/VideoStudio.Web/src/components/ShotEditorPanel.jsx`
  - Split into prompt editing and inspector fields.
  - Keep current PATCH behavior.

- `frontend/VideoStudio.Web/src/components/ShotSelectionToolbar.jsx`
  - Retire or replace with Storyboard action bar.

- `frontend/VideoStudio.Web/src/components/ShotList.jsx`
  - Retire or use only for compact read-only plan display.

- `frontend/VideoStudio.Web/src/components/VisualPreparationPanel.jsx`
  - Split shot-keyframe editing into `ShotKeyframePromptEditor.jsx`.

API use:

- `GET /api/projects/{id}/scenes`
- `GET /api/projects/{id}/shots`
- `PATCH /api/projects/{projectId}/scenes/{sceneId}`
- `PATCH /api/projects/{projectId}/scenes/{sceneId}/shots/{shotId}`
- `PATCH /api/projects/{projectId}/scenes/{sceneId}/shots/{shotId}/start-image-prompt`
- `POST /api/projects/{projectId}/scenes/{sceneId}/shots/{shotId}/start-image`
- `POST /api/projects/{id}/visuals/generate-shot-start-images`
- `POST /api/projects/{id}/render`
- `GET /api/projects/{id}/render-jobs`

Animate selected request:

```json
{
  "preset": 0,
  "shotIds": ["selected-shot-id"],
  "force": true,
  "useCharacterReferenceInPrompt": true,
  "useShotStartImage": true
}
```

Animate all request:

```json
{
  "preset": 0,
  "maxShots": 9999,
  "force": false,
  "useCharacterReferenceInPrompt": true,
  "useShotStartImage": true
}
```

Acceptance:

- Selected shot is obvious.
- Center preview shows keyframe, render, or empty state.
- Image-to-Video explanation is attached to keyframe and animate actions.
- Animate actions create RenderJobs only through the API.
- No worker/media process starts from frontend code.

## Phase 2E: Edit / Render / Finalize Page

Goal: consolidate assembly, audio, finalization, and preview.

Files:

- `frontend/VideoStudio.Web/src/components/EditStep.jsx` new
  - Renders completed shot list.
  - Assembly controls.
  - Audio controls.
  - Finalization controls.
  - Final preview.

- `frontend/VideoStudio.Web/src/components/AssemblyPanel.jsx`
  - Keep but make it an Edit section.
  - Separate assemble and finalize controls.

- `frontend/VideoStudio.Web/src/components/DialogueLinesPanel.jsx`
  - Add Edit mode with audio generation statuses.

- `frontend/VideoStudio.Web/src/components/VideoPreviewPanel.jsx`
  - Make final movie first, then assembled movie, then latest shot render.
  - Keep media URL and local path display.

- `frontend/VideoStudio.Web/src/components/RenderJobsPanel.jsx`
  - Move to collapsible advanced monitor.
  - Keep detailed job rows for debugging.

API use:

- `GET /api/projects/{id}/render-jobs`
- `POST /api/projects/{id}/assemble`
- `GET /api/projects/{id}/dialogue-lines`
- `POST /api/projects/{id}/audio/generate`
- `POST /api/projects/{id}/finalize`
- `GET /api/projects/{id}/final-video`
- `GET /api/projects/{id}/media`

Acceptance:

- Edit step clearly shows whether the movie is ready to assemble, ready for audio, ready to finalize, or complete.
- Final preview uses backend media URLs.
- FFmpeg and TTS remain Python worker responsibilities.

## File-by-File Plan

Add:

- `CreatorShell.jsx`
- `CreatorStepper.jsx`
- `ContentStep.jsx`
- `CastStep.jsx`
- `CharacterCard.jsx`
- `StoryboardStep.jsx`
- `ShotThumbnailRail.jsx`
- `ShotPreviewPanel.jsx`
- `ShotInspectorPanel.jsx`
- `ShotKeyframePromptEditor.jsx`
- `CharacterReferencePromptEditor.jsx`
- `EditStep.jsx`

Modify:

- `ProjectDetailPage.jsx`
  - Keep data loading and handlers.
  - Delegate rendering to the four step components.

- `StoryEditor.jsx`
  - Make copy and layout configurable.

- `ProductionPlanViewer.jsx`
  - Split or reduce to read-only plan summary components.

- `CharacterList.jsx`
  - Convert to Cast grid wrapper or retire.

- `ShotList.jsx`
  - Convert to compact read-only list or retire.

- `VisualPreparationPanel.jsx`
  - Split into character and shot editors, then retire mixed panel.

- `AssemblyPanel.jsx`
  - Keep as Edit section.

- `VideoPreviewPanel.jsx`
  - Improve final-first preview hierarchy.

- `RenderJobsPanel.jsx`
  - Convert to collapsible advanced monitor.

- `styles.css`
  - Add shell, stepper, storyboard rail, keyframe preview, inspector, and edit styles.
  - Remove or downplay production lane styling after migration.

Keep:

- `api/client.js`
  - Keep endpoint functions.
  - Add small convenience wrappers only if useful; no endpoint behavior changes.

## Risk List

- The current `ProjectDetailPage.jsx` owns many handlers; splitting too quickly can cause prop drilling and state sync bugs.
- Storyboard needs a reliable selected shot model across `plan`, `preproduction`, `scenes`, `shots`, and `jobs`.
- Job status polling must refresh visual data when image jobs complete.
- Animate All can queue many slow Wan2.2 renders; the UI should warn before queueing.
- Character references and keyframes can be confused; copy and layout must keep them distinct.
- The full job monitor is useful for development but can overwhelm non-technical users if shown too prominently.
- Existing generated media paths are local; previews should prefer `/media/...` URLs.
- Mobile storyboard layout may need additional iteration because the desktop three-panel editor will not fit directly.

## Verification Commands

Run after code implementation phases:

```powershell
cd frontend/VideoStudio.Web
npm.cmd run build
```

```powershell
cd C:\Users\USER\Desktop\Creator
dotnet build
```

```powershell
git status --short --branch
```

Manual verification checklist:

- Create/select project.
- Save story.
- Analyze.
- Review Content plan summary.
- Review Cast characters.
- Generate/upload one character reference.
- Generate/upload one shot keyframe.
- Select a storyboard shot.
- Animate selected shot with Image-to-Video enabled.
- Confirm a RenderJob is queued.
- Assemble after render completion.
- Generate audio.
- Finalize.
- Preview final media URL.

Do not run SDXL, Wan2.2, or media jobs during pure UI layout verification unless explicitly testing the full local pipeline.

