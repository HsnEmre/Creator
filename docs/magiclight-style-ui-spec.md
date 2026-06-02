# MagicLight-Style UI Target Specification

## Purpose

This document defines the target user-facing creator workflow for VideoStudio. The goal is to replace the current internal production-board feel with a simpler four-step creator wizard:

1. Content
2. Cast
3. Storyboard
4. Edit

This uses a similar workflow concept and interaction pattern to modern AI video creator tools, but it must not copy any third-party branding, logos, visual assets, exact copy, or proprietary text. The UI should feel native to VideoStudio and should continue to expose local-first generation constraints clearly.

## Non-Goals

- Do not change ASP.NET Core API architecture.
- Do not move inference, FFmpeg, or media processing into ASP.NET Core.
- Do not change SQL Server usage.
- Do not add Redis, Docker, PostgreSQL, SQLite, ComfyUI, or node workflow systems.
- Do not change Python worker render logic.
- Do not change Wan2.2 generation behavior, quality settings, or performance settings.
- Do not modify `C:\AI\Wan2.2`.
- Do not hide the fact that local renders may be slow.

## Target Information Architecture

The project detail page should become a wizard-like workspace with four top-level steps.

### Global Shell

- Top app bar:
  - Project title
  - Back to Projects
  - Save/refresh state
  - Compact job activity indicator
  - Final output status

- Top stepper:
  - Content
  - Cast
  - Storyboard
  - Edit

- Main content region:
  - Shows the active step.
  - Uses one dominant workflow surface at a time.

- Persistent right rail on desktop:
  - Preview panel
  - Current selected artifact details
  - Active job/progress summary
  - Collapsible full job monitor

- Mobile layout:
  - Stepper remains top.
  - Right rail collapses below the active step.
  - Shot thumbnail rail becomes horizontal.

## Page Layout

### Desktop

```text
+--------------------------------------------------------------+
| Project title                         Jobs | Refresh | Back   |
+--------------------------------------------------------------+
| Content | Cast | Storyboard | Edit                            |
+-------------------------------+------------------------------+
| Active step workspace          | Preview / selected details   |
|                               | Jobs summary / output paths   |
|                               |                              |
+-------------------------------+------------------------------+
```

### Storyboard Desktop Layout

```text
+-----------------+-----------------------------+--------------+
| Shot rail        | Keyframe / video preview    | Shot panel   |
| - Scene 1 Shot 1 |                             | Characters   |
| - Scene 1 Shot 2 | Selected narration/subtitle | Prompt edit  |
| - Scene 2 Shot 1 | Generation history          | Actions      |
+-----------------+-----------------------------+--------------+
```

## Stage-by-Stage UX

## 1. Content

Purpose: capture the story idea or full script and turn it into a structured production plan.

Primary elements:
- Project title and duration controls.
- Story/script textarea.
- Save Story button.
- Analyze Story button.
- Production plan summary after analysis.
- Scene, shot, narration, and dialogue review.

Primary actions:
- Create project if user enters from project list.
- Save story via `POST /api/projects/{id}/story`.
- Analyze via `POST /api/projects/{id}/analyze`.
- Refresh production plan via `GET /api/projects/{id}/production-plan`.

Desired behavior:
- If no story exists, show a blank content editor with a clear prompt.
- If story exists but no plan exists, show "Ready to analyze."
- If analysis is running, show a blocking planning state with readable progress text.
- If analysis succeeds, show:
  - title
  - logline
  - genre
  - scene count
  - shot count
  - dialogue count
- If analysis fails, show a clear error and keep the saved story editable.

## 2. Cast

Purpose: review detected characters and establish identity references.

Primary elements:
- Character cards/grid.
- Character details:
  - name
  - role
  - personality
  - visual prompt
  - voice style
  - continuity rules
- Reference image slot per character:
  - empty, queued, generating, generated, uploaded, failed
- Reference prompt editor.
- Upload reference image.
- Generate one reference.
- Generate all missing references.

Primary actions:
- Prepare visual prompts via `POST /api/projects/{id}/preproduction/prepare`.
- Update reference prompt via `PATCH /api/projects/{projectId}/characters/{characterId}/reference-prompt`.
- Upload image via `POST /api/projects/{projectId}/characters/{characterId}/reference-image`.
- Generate image jobs via `POST /api/projects/{id}/visuals/generate-character-references`.

Representation rules:
- Character references are portrait/identity guides.
- Character references guide compiled prompts and continuity.
- Character references are not Wan2.2 `--image` inputs.
- The UI must avoid implying that a character portrait becomes the video start frame.

## 3. Storyboard

Purpose: let the user review each shot, manage keyframes, and animate shots through existing render jobs.

Primary layout:
- Left shot thumbnail rail:
  - grouped by scene
  - each item shows scene index, shot index, status, duration, keyframe thumbnail, render status
- Center preview:
  - selected shot keyframe if available
  - selected rendered clip if completed
  - placeholder when neither exists
  - narration/subtitle/dialogue for selected shot or scene segment
  - generation history for selected shot
- Right shot inspector:
  - on-screen characters
  - shot type
  - camera motion
  - action
  - prompt editor
  - negative prompt editor
  - continuity notes
  - upload custom keyframe
  - regenerate keyframe
  - animate selected shot
  - animate all shots

Primary actions:
- Update scene via `PATCH /api/projects/{projectId}/scenes/{sceneId}`.
- Update shot via `PATCH /api/projects/{projectId}/scenes/{sceneId}/shots/{shotId}`.
- Update keyframe prompt via `PATCH /api/projects/{projectId}/scenes/{sceneId}/shots/{shotId}/start-image-prompt`.
- Upload keyframe via `POST /api/projects/{projectId}/scenes/{sceneId}/shots/{shotId}/start-image`.
- Generate selected keyframe via `POST /api/projects/{id}/visuals/generate-shot-start-images` with `shotIds`.
- Generate all missing keyframes via `POST /api/projects/{id}/visuals/generate-shot-start-images`.
- Animate selected shot via `POST /api/projects/{id}/render`.
- Animate all shots via `POST /api/projects/{id}/render`.

Animate Selected mapping:

```json
{
  "preset": 0,
  "shotIds": ["selected-shot-id"],
  "force": true,
  "useCharacterReferenceInPrompt": true,
  "useShotStartImage": true
}
```

Animate All mapping:

```json
{
  "preset": 0,
  "maxShots": 9999,
  "force": false,
  "useCharacterReferenceInPrompt": true,
  "useShotStartImage": true
}
```

Important explanation:
- "Image-to-Video" means the selected shot keyframe is sent to Wan2.2 as the start frame.
- If no shot keyframe exists, the shot renders as Text-to-Video even if Image-to-Video is enabled.
- Character references remain identity guidance in prompts, not `--image` inputs.

## 4. Edit

Purpose: assemble rendered shots, generate audio, finalize the movie, and preview the output.

Primary elements:
- Rendered shot list with completion states.
- Assemble action.
- Dialogue/audio list.
- Generate audio action.
- Finalize action.
- Final video preview.
- Local path and media URL display.
- Collapsible detailed job monitor.

Primary actions:
- Assemble via `POST /api/projects/{id}/assemble`.
- Generate audio via `POST /api/projects/{id}/audio/generate`.
- Finalize via `POST /api/projects/{id}/finalize`.
- Re-finalize via `POST /api/projects/{id}/finalize` with `force=true`.
- Preview final via `GET /api/projects/{id}/final-video`.
- Monitor jobs via `GET /api/projects/{id}/render-jobs`.

Desired behavior:
- If no rendered shots exist, explain that the user should animate shots in Storyboard.
- If rendered shots exist but no assembled movie exists, show "Ready to assemble."
- If assembled movie exists, show assembled preview and proceed to audio/finalize.
- If dialogue lines exist without audio, show "Generate audio."
- If final exists, show final movie first.

## Component Mapping

| Current Component | Target Role |
| --- | --- |
| `ProjectDetailPage.jsx` | Becomes wizard shell and state coordinator. |
| `WorkflowStageBar.jsx` | Replace with four-step top stepper. |
| `WorkflowLane.jsx` | Not used as primary layout after Phase 2; can be retired or adapted as step section wrapper. |
| `StoryEditor.jsx` | Becomes Content story/script editor. |
| `ProductionPlanViewer.jsx` | Split into `PlanSummary`, `SceneOutline`, and compact review panels. |
| `CharacterList.jsx` | Becomes Cast character grid. |
| `VisualPreparationPanel.jsx` | Split into Cast reference tools and Storyboard keyframe tools. |
| `ShotList.jsx` | Becomes Storyboard shot rail/card source. |
| `ShotEditorPanel.jsx` | Becomes right-side shot inspector and prompt editor. |
| `ShotSelectionToolbar.jsx` | Becomes Storyboard animate action bar. |
| `AssemblyPanel.jsx` | Moves into Edit step. |
| `DialogueLinesPanel.jsx` | Moves into Edit audio section and Content review section. |
| `VideoPreviewPanel.jsx` | Persistent preview rail and Edit final preview. |
| `RenderJobsPanel.jsx` | Collapsible job monitor, not primary workflow. |
| `ActionToolbar.jsx` | Retire; actions move into step-specific components. |
| `styles.css` | Replace board/lane emphasis with wizard, shot rail, inspector, and preview styles. |

## Data and API Mapping

| UX Data | Source/API |
| --- | --- |
| Project title/story/status | `GET /api/projects/{id}` |
| Save story | `POST /api/projects/{id}/story` |
| Analyze story | `POST /api/projects/{id}/analyze` |
| Production plan | `GET /api/projects/{id}/production-plan` |
| Preproduction prompts and visual status | `GET /api/projects/{id}/preproduction` |
| Prepare visual prompts | `POST /api/projects/{id}/preproduction/prepare` |
| Scenes | `GET /api/projects/{id}/scenes` |
| Shots | `GET /api/projects/{id}/shots` |
| Update scene | `PATCH /api/projects/{projectId}/scenes/{sceneId}` |
| Update shot | `PATCH /api/projects/{projectId}/scenes/{sceneId}/shots/{shotId}` |
| Character reference prompt | `PATCH /api/projects/{projectId}/characters/{characterId}/reference-prompt` |
| Upload character reference | `POST /api/projects/{projectId}/characters/{characterId}/reference-image` |
| Generate character references | `POST /api/projects/{id}/visuals/generate-character-references` |
| Shot keyframe prompt | `PATCH /api/projects/{projectId}/scenes/{sceneId}/shots/{shotId}/start-image-prompt` |
| Upload shot keyframe | `POST /api/projects/{projectId}/scenes/{sceneId}/shots/{shotId}/start-image` |
| Generate shot keyframes | `POST /api/projects/{id}/visuals/generate-shot-start-images` |
| Animate/render shots | `POST /api/projects/{id}/render` |
| Job monitor | `GET /api/projects/{id}/render-jobs` |
| Dialogue/audio lines | `GET /api/projects/{id}/dialogue-lines` |
| Generate audio | `POST /api/projects/{id}/audio/generate` |
| Assemble movie | `POST /api/projects/{id}/assemble` |
| Finalize movie | `POST /api/projects/{id}/finalize` |
| Final video | `GET /api/projects/{id}/final-video` |
| Media summary | `GET /api/projects/{id}/media` |

## State Model

Project detail state should be normalized around the four user-facing steps.

```text
project
plan
preproduction
scenes
shots
dialogueLines
jobs
finalVideo
media
selectedStep
selectedSceneId
selectedShotId
selectedCharacterId
selectedJobId
renderOptions
busyAction
message
error
```

Derived state:

- `hasStory`
- `hasPlan`
- `charactersReady`
- `characterReferencesReadyCount`
- `shotKeyframesReadyCount`
- `selectedShot`
- `selectedShotKeyframe`
- `selectedShotRenderJobs`
- `selectedShotLatestRender`
- `activeJobsByType`
- `isAnalyzing`
- `isGeneratingReferences`
- `isGeneratingKeyframes`
- `isRenderingSelectedShot`
- `isRenderingAnyShot`
- `isAssembling`
- `isGeneratingAudio`
- `isFinalizing`

Render options:

```text
preset
force
useCharacterReferenceInPrompt
useShotStartImage
maxShots
selectedShotIds
```

## Empty States

Content:
- "Start with a story idea, full script, or scene description."
- Analyze disabled until story text is saved or present.

Cast:
- If no plan: "Analyze your story to detect characters."
- If characters exist but no references: "Generate or upload character references."

Storyboard:
- If no plan: "Analyze your story to create shots."
- If shots exist but no keyframes: "Generate keyframes or upload custom images."
- If selected shot has no keyframe: show a neutral placeholder and explain Text-to-Video fallback.

Edit:
- If no renders: "Animate shots in Storyboard before assembling."
- If no audio: "Generate dialogue audio after reviewing dialogue lines."
- If no final: "Assemble and finalize to preview the movie."

## Loading States

- Analyze: modal or prominent inline "Planning story..."
- Prepare visuals: "Preparing reference and keyframe prompts..."
- Generate character reference: per-character skeleton or spinner.
- Generate keyframe: per-shot thumbnail spinner.
- Animate selected shot: selected shot rail item shows queued/rendering/progress.
- Animate all: rail shows distributed job progress.
- Assemble/audio/finalize: Edit step shows progress cards.

## Job Progress States

Job states should be translated into user language.

| Internal State | User Label |
| --- | --- |
| Pending | Queued |
| Rendering | Generating |
| Completed | Ready |
| Failed | Failed |

Render job grouping:

- Character images
- Shot keyframes
- Shot animations
- Assembly
- Audio
- Final movie

The full `RenderJobsPanel` should be collapsible or in an advanced monitor drawer.

## Error States

- Analyze/Ollama error:
  - Keep story text.
  - Show backend error.
  - Offer Retry Analyze.

- Character reference generation error:
  - Show failed badge on character card.
  - Keep prompt editable.
  - Offer Regenerate and Upload.

- Keyframe generation error:
  - Show failed badge on shot rail item.
  - Keep start image prompt editable.
  - Offer Regenerate and Upload.

- Render error:
  - Show failed badge on shot.
  - Show error in generation history.
  - Offer Animate Again.

- Finalize error:
  - Show finalization error.
  - Keep assembled preview if available.
  - Offer Re-finalize.

## Character Reference Representation

Character cards should show:

- Reference image thumbnail or empty image slot.
- Name and role.
- Visual description lock.
- Voice style.
- Status badge.
- Generate reference button.
- Upload button.
- Prompt editor in expandable advanced area.

Copy guidance:
- "Character reference helps keep identity consistent in prompts."
- "It is not used as a Wan2.2 start frame."

## Shot Start Image / Keyframe Representation

Shot keyframes should show:

- Thumbnail in shot rail.
- Large preview in center panel.
- Status badge.
- Prompt and negative prompt editor.
- Upload custom image.
- Regenerate keyframe.
- Generation history.

Copy guidance:
- "Keyframes can be sent to Wan2.2 as the first frame for Image-to-Video."
- "If a shot has no keyframe, it will render as Text-to-Video."

## Image-to-Video Explanation

Use plain creator-facing text:

- "Image-to-Video uses the selected shot keyframe as the start frame."
- "Character references are separate: they guide identity in the written prompt."
- "Turn this on when you want Wan2.2 to animate from the shot keyframe."

Avoid:

- Low-level references as primary UI labels, such as `InputImagePath`, `RenderJob`, or `--image`.
- Implying character portraits become start frames.

## Animate Selected and Animate All

Animate Selected:

- Uses selected shot IDs.
- Should default to `useShotStartImage=true` when selected shots have keyframes.
- Should default to `useCharacterReferenceInPrompt=true`.
- Creates RenderJobs through existing `/render` endpoint.

Animate All:

- Uses `maxShots=9999` or all shot IDs.
- Should warn before queueing many shots because local rendering is slow.
- Should default to `force=false` to avoid duplicate queued/running jobs.

Both actions must continue to rely on the Python worker polling the API. The frontend must not call Wan2.2 directly.

## What Not To Change

- Do not add new backend endpoints unless a later phase explicitly approves them.
- Do not change RenderJob schema for this UI redesign.
- Do not change Python worker polling.
- Do not add browser-side media processing.
- Do not hide local filesystem/media URL limitations.
- Do not remove Swagger or existing development tools.
- Do not change current SQL Server storage.
- Do not copy third-party branding or text.

