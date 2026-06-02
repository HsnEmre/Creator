import ProductionPlanViewer from "./ProductionPlanViewer";

const MAX_SCENE_PREVIEW_COUNT = 5;
const MAX_SHOT_PREVIEW_COUNT = 4;

function compactText(value, maxLength = 180) {
  if (!value) return "";
  const normalized = String(value).replace(/\s+/g, " ").trim();
  if (normalized.length <= maxLength) return normalized;
  return `${normalized.slice(0, maxLength - 1).trim()}...`;
}

function countShots(scenes) {
  return scenes.reduce((count, scene) => count + (scene.shots?.length || 0), 0);
}

function countDialogueLines(plan, dialogueLines) {
  const sceneDialogueCount = (plan?.scenes || []).reduce(
    (count, scene) => count + (scene.dialogueLines?.length || 0),
    0
  );
  return Math.max(sceneDialogueCount, dialogueLines?.length || 0);
}

function getVisualStyleLabel(plan) {
  const style = plan?.visualStyle?.stylePrompt || plan?.visualStyle?.cameraStyle || "";
  return style ? compactText(style, 70) : "Auto style after analysis";
}

function PlanStat({ label, value }) {
  return (
    <div className="content-plan-stat">
      <strong>{value}</strong>
      <span>{label}</span>
    </div>
  );
}

function EmptyPlanState({ canAnalyze }) {
  return (
    <section className="content-plan-empty">
      <span className="badge badge-ready">Ready for planning</span>
      <h3>No production plan yet</h3>
      <p>Analyze will create scenes, shots, dialogue, characters, and visual prompts.</p>
      {!canAnalyze ? <p className="muted">Save the story first, then Analyze Story will become available.</p> : null}
    </section>
  );
}

function PlanPreview({ plan, dialogueLines }) {
  const scenes = plan?.scenes || [];
  const characters = plan?.characters || [];
  const shotTotal = countShots(scenes);
  const dialogueTotal = countDialogueLines(plan, dialogueLines);
  const synopsis = plan?.logline || scenes[0]?.summary || "Generated production plan is ready for review.";
  const previewScenes = scenes.slice(0, MAX_SCENE_PREVIEW_COUNT);
  const hiddenSceneCount = Math.max(0, scenes.length - previewScenes.length);

  return (
    <section className="content-plan-preview">
      <div className="content-plan-preview-head">
        <div>
          <span className="badge badge-generated">Plan generated</span>
          <h3>{plan.title || "Production Plan"}</h3>
          <p className="muted">{compactText(synopsis, 220)}</p>
        </div>
        {plan.genre ? <span className="badge">{plan.genre}</span> : null}
      </div>

      <div className="content-plan-stats">
        <PlanStat label="Scenes" value={scenes.length} />
        <PlanStat label="Shots" value={shotTotal} />
        <PlanStat label="Characters" value={characters.length} />
        <PlanStat label="Dialogue lines" value={dialogueTotal} />
      </div>

      <div className="content-script-outline">
        {previewScenes.map((scene) => {
          const shots = scene.shots || [];
          const hiddenShotCount = Math.max(0, shots.length - MAX_SHOT_PREVIEW_COUNT);
          return (
            <article className="content-script-scene" key={`content-scene-${scene.index}`}>
              <div className="content-script-scene-head">
                <h4>{scene.title || `Scene ${scene.index}`}</h4>
                {scene.estimatedDurationSeconds ? <span>{scene.estimatedDurationSeconds}s</span> : null}
              </div>
              <p className="content-scene-kicker">Scene {scene.index}</p>
              {scene.summary ? <p className="muted">{compactText(scene.summary, 180)}</p> : null}
              <div className="content-script-shot-list">
                {shots.slice(0, MAX_SHOT_PREVIEW_COUNT).map((shot) => (
                  <p key={`content-shot-${scene.index}-${shot.index}`}>
                    <b>Shot {shot.index}:</b> {compactText(shot.action || shot.audioCue || shot.wanPrompt || "Visual beat planned.", 150)}
                  </p>
                ))}
                {hiddenShotCount ? <p className="muted">+ {hiddenShotCount} more shot(s) in this scene</p> : null}
              </div>
              {scene.dialogueLines?.length ? (
                <div className="content-dialogue-snippet">
                  {scene.dialogueLines.slice(0, 2).map((line, index) => (
                    <p key={`content-dialogue-${scene.index}-${index}`}>
                      <b>{line.speaker || "Dialogue"}:</b> {compactText(line.text, 130)}
                    </p>
                  ))}
                </div>
              ) : null}
            </article>
          );
        })}
        {hiddenSceneCount ? <p className="muted">+ {hiddenSceneCount} more scene(s) in the detailed plan</p> : null}
      </div>
    </section>
  );
}

export default function ContentStep({
  project,
  plan,
  storyText,
  dialogueLines,
  isBusy,
  isSaving,
  isAnalyzing,
  canAnalyze,
  canGoNext,
  onStoryChange,
  onSaveStory,
  onAnalyze,
  onRefresh,
  onNext
}) {
  const targetDuration = project?.targetDurationSeconds || plan?.targetDurationSeconds || 60;
  const characterCount = storyText?.length || 0;
  const hasPlan = Boolean(plan);
  const isStoryDirty = (project?.storyText || "") !== (storyText || "");
  const canAnalyzeSavedStory = canAnalyze && !isStoryDirty;

  return (
    <div className="creator-step-panel content-step">
      <section className="content-step-head">
        <span className="badge badge-ready">Content</span>
        <h2>Shape the story before production starts</h2>
        <p>
          Write the idea, save it, then let the planner turn it into a reviewable video structure before you move into cast and storyboard work.
        </p>
      </section>

      <section className="content-story-card">
        <div className="content-story-head">
          <div>
            <span className="eyebrow">Creator Intake</span>
            <h2>Story to Video</h2>
            <p className="muted">Enter a story idea or paste a full script.</p>
          </div>
          <span className="badge">{characterCount.toLocaleString()} characters</span>
        </div>

        <textarea
          className="content-story-input"
          rows={16}
          value={storyText}
          onChange={(event) => onStoryChange(event.target.value)}
          placeholder="A detective follows a signal through a rain-soaked neon district..."
        />

        <div className="content-option-grid" aria-label="Story planning options">
          <span className="content-option-chip">
            <b>Duration</b>
            {targetDuration}s target
          </span>
          <span className="content-option-chip">
            <b>Language</b>
            English / Turkish ready
          </span>
          <span className="content-option-chip">
            <b>Aspect</b>
            16:9 / 1280x704
          </span>
          <span className="content-option-chip">
            <b>Visual style</b>
            {getVisualStyleLabel(plan)}
          </span>
        </div>

        <div className="content-primary-actions">
          <button className="primary-button" type="button" disabled={isBusy} onClick={onSaveStory}>
            {isSaving ? "Saving..." : "Save Story"}
          </button>
          <button className="primary-button" type="button" disabled={isBusy || !canAnalyzeSavedStory} onClick={onAnalyze}>
            {isAnalyzing ? "Analyzing..." : "Analyze Story"}
          </button>
          <button className="next-button" type="button" disabled={!canGoNext} onClick={onNext}>
            Next: Cast
          </button>
          <button className="quiet-button" type="button" disabled={isBusy} onClick={onRefresh}>
            Refresh
          </button>
        </div>
      </section>

      {isStoryDirty ? <p className="msg compact-msg content-unsaved-note">Save the latest story changes before running Analyze.</p> : null}

      {hasPlan ? <PlanPreview plan={plan} dialogueLines={dialogueLines} /> : <EmptyPlanState canAnalyze={canAnalyzeSavedStory} />}

      {hasPlan ? (
        <details className="content-detailed-plan">
          <summary>Detailed plan</summary>
          <ProductionPlanViewer plan={plan} showCharacters={false} showShotUploads={false} />
        </details>
      ) : null}
    </div>
  );
}
