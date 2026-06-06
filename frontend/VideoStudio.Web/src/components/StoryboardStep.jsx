import { useEffect, useMemo, useState } from "react";
import { toAbsoluteApiUrl } from "../api/client";

function statusLabel(value) {
  if (typeof value === "string") return value;
  if (value === 0) return "Pending";
  if (value === 1) return "Rendering";
  if (value === 2) return "Completed";
  if (value === 3) return "Failed";
  return value === undefined || value === null ? "Unknown" : `Unknown(${value})`;
}

function isRunningJob(job) {
  const status = String(statusLabel(job?.status)).toLowerCase();
  return status === "pending" || status === "rendering";
}

function isCompletedJob(job) {
  return String(statusLabel(job?.status)).toLowerCase() === "completed";
}

function isRenderVideoJob(job) {
  return job?.jobTypeName === "RenderVideo" || job?.jobType === "RenderVideo";
}

function compactText(value, maxLength = 180) {
  if (!value) return "";
  const normalized = String(value).replace(/\s+/g, " ").trim();
  if (normalized.length <= maxLength) return normalized;
  return `${normalized.slice(0, maxLength - 1).trim()}...`;
}

function containsGenericPromptText(value) {
  if (!value) return false;
  const text = String(value).toLowerCase();
  return [
    "cinematic story location",
    "motivated time of day",
    "cinematic mood",
    "characters: .",
    "visible characters: .",
    "wrong scene index",
    "wrong shot action"
  ].some((term) => text.includes(term));
}

function extractPromptField(prompt, label) {
  if (!prompt) return "";
  const pattern = new RegExp(`${label}:\\s*([^.]*)`, "i");
  const match = String(prompt).match(pattern);
  return match?.[1]?.trim() || "";
}

function copyText(value) {
  if (!value || !navigator.clipboard) return;
  navigator.clipboard.writeText(value).catch(() => {});
}

function getShotKey(shot) {
  return `${shot.sceneIndex ?? "-"}-${shot.index ?? shot.shotIndex ?? "-"}`;
}

function getShotStartUrl(shot) {
  return shot?.startImageUrl ? toAbsoluteApiUrl(shot.startImageUrl) : "";
}

function getJobTime(job) {
  return new Date(job?.finishedAt || job?.createdAt || 0).getTime();
}

function getLatestCompletedRender(jobs) {
  return (jobs || [])
    .filter((job) => isRenderVideoJob(job) && isCompletedJob(job) && (job.outputUrl || job.outputPath))
    .sort((a, b) => getJobTime(b) - getJobTime(a))[0] || null;
}

function formatDuration(seconds) {
  if (seconds === undefined || seconds === null || Number.isNaN(Number(seconds))) return "";
  const value = Number(seconds);
  if (value < 60) return `${value.toFixed(value >= 10 ? 0 : 1)}s`;
  const minutes = Math.floor(value / 60);
  const remainder = Math.round(value % 60);
  return `${minutes}m ${String(remainder).padStart(2, "0")}s`;
}

function rawClipSecondsFromJob(job) {
  if (job?.probedRawClipDurationSeconds !== undefined && job?.probedRawClipDurationSeconds !== null) {
    return Number(job.probedRawClipDurationSeconds);
  }
  if (job?.expectedRawClipDurationSeconds !== undefined && job?.expectedRawClipDurationSeconds !== null) {
    return Number(job.expectedRawClipDurationSeconds);
  }
  if (job?.actualFrameNum || job?.frameNum) {
    return Number(job.actualFrameNum || job.frameNum) / 24;
  }
  return null;
}

function latestLongMotionFailure(jobs) {
  return (jobs || [])
    .filter((job) => isRenderVideoJob(job) && String(job.renderDurationMode).toLowerCase() === "longmotion" && String(statusLabel(job.status)).toLowerCase() === "failed")
    .sort((a, b) => getJobTime(b) - getJobTime(a))[0] || null;
}

const RENDER_PROFILES = [
  ["FastPreview", "FastPreview", "Fastest pipeline test"],
  ["CinematicPreview", "CinematicPreview", "More raw motion"],
  ["LongMotion", "LongMotion", "121 frames, slower motion"],
  ["ComfyUIParity", "ComfyUIParity", "161 frames, 18 steps, closest parity"],
  ["AutoQuality", "AutoQuality", "Director strategy per shot"]
];

function RenderProfileSelector({ value, onChange, name = "renderDurationMode", className = "" }) {
  return (
    <div className={`segmented-control render-profile-options ${className}`} role="radiogroup" aria-label="Render duration profile">
      {RENDER_PROFILES.map(([profileValue, label, help]) => (
        <label key={profileValue} className={value === profileValue ? "selected" : ""}>
          <input
            type="radio"
            name={name}
            value={profileValue}
            checked={value === profileValue}
            onChange={(event) => onChange(event.target.value)}
          />
          <span>{label}</span>
          <small>{help}</small>
        </label>
      ))}
    </div>
  );
}

function getShotStatus(shot, jobs) {
  const renderJob = getLatestCompletedRender(jobs);
  if (renderJob) return "Rendered";
  const runningRender = jobs.find((job) => isRenderVideoJob(job) && isRunningJob(job));
  if (runningRender) return "Rendering";
  const runningKeyframe = jobs.find((job) => job.jobTypeName === "GenerateShotStartImage" && isRunningJob(job));
  if (runningKeyframe) return "Keyframe queued";
  if (shot.startImageUrl || shot.startImagePath) return "Keyframe ready";
  return "Needs keyframe";
}

function getStatusClass(status) {
  if (status === "Rendered" || status === "Keyframe ready") return "badge-generated";
  if (status === "Rendering" || status === "Keyframe queued") return "badge-rendering";
  return "badge-pending";
}

function normalizePlanShots(plan) {
  return (plan?.scenes || []).flatMap((scene) =>
    (scene.shots || []).map((shot) => ({
      ...shot,
      sceneId: shot.sceneId || scene.id,
      sceneIndex: scene.index ?? scene.sceneIndex,
      sceneTitle: scene.title,
      sceneSummary: scene.summary,
      sceneLocation: scene.location,
      sceneMood: scene.mood,
      sceneRequiredCharacters: scene.requiredCharacters || [],
      sceneDialogueLines: scene.dialogueLines || [],
      index: shot.index ?? shot.shotIndex
    }))
  );
}

function selectedShotJobs(shot, jobs) {
  if (!shot) return [];
  return jobs
    .filter((job) => job.sceneIndex === shot.sceneIndex && job.shotIndex === shot.index)
    .sort((a, b) => new Date(b.createdAt || 0) - new Date(a.createdAt || 0));
}

function ShotRail({ shots, selectedShotId, jobsByKey, onSelectShot }) {
  if (!shots.length) {
    return (
      <section className="storyboard-rail">
        <h3>Shots</h3>
        <p className="muted">No shots generated yet.</p>
      </section>
    );
  }

  return (
    <section className="storyboard-rail" aria-label="Storyboard shots">
      <div className="row">
        <h3>Shots</h3>
        <span className="badge">{shots.length}</span>
      </div>
      <div className="storyboard-shot-rail-list">
        {shots.map((shot) => {
          const key = getShotKey(shot);
          const shotJobs = jobsByKey.get(key) || [];
          const latestRender = getLatestCompletedRender(shotJobs);
          const status = getShotStatus(shot, shotJobs);
          const isSelected = selectedShotId === shot.id;
          const startUrl = getShotStartUrl(shot);
          return (
            <button
              type="button"
              className={`storyboard-shot-thumb ${isSelected ? "selected" : ""}`}
              key={shot.id || key}
              onClick={() => onSelectShot(shot.id)}
            >
              <span className="storyboard-shot-image">
                {startUrl ? <img src={startUrl} alt={`Scene ${shot.sceneIndex} shot ${shot.index} keyframe`} /> : <span>Keyframe</span>}
              </span>
              <span className="storyboard-shot-meta">
                <b>Scene {shot.sceneIndex} / Shot {shot.index}</b>
                <small>{compactText(shot.action || shot.shotType || shot.sceneTitle || "Planned shot", 64)}</small>
                <span className={`badge ${getStatusClass(status)}`}>{status}</span>
                {latestRender ? (
                  <small className="storyboard-render-meta">
                    Latest render {formatDuration(latestRender.durationSeconds) || "ready"}
                    {rawClipSecondsFromJob(latestRender) ? `, raw ${formatDuration(rawClipSecondsFromJob(latestRender))}` : ""}
                  </small>
                ) : null}
              </span>
            </button>
          );
        })}
      </div>
    </section>
  );
}

function ShotPreview({ shot, jobs, onUploadStartImage }) {
  const startUrl = getShotStartUrl(shot);
  const latestRender = getLatestCompletedRender(jobs);
  const latestKeyframeJob = jobs.find((job) => job.jobTypeName === "GenerateShotStartImage");

  if (!shot) {
    return (
      <section className="storyboard-preview-panel">
        <div className="storyboard-empty-preview">
          <h3>No shot selected</h3>
          <p>Select a shot from the rail to review its keyframe and animation controls.</p>
        </div>
      </section>
    );
  }

  return (
    <section className="storyboard-preview-panel">
      <div className="storyboard-selected-head">
        <div>
          <span className="badge badge-ready">Selected Shot</span>
          <h2>Scene {shot.sceneIndex} / Shot {shot.index}</h2>
          <p className="muted">{shot.sceneTitle || shot.sceneSummary || "Storyboard shot"}</p>
        </div>
        {shot.durationSeconds || shot.targetDurationSeconds ? <span className="badge">{shot.durationSeconds || shot.targetDurationSeconds}s</span> : null}
      </div>

      <div className="storyboard-keyframe-stage">
        {startUrl ? (
          <img src={startUrl} alt={`Scene ${shot.sceneIndex} shot ${shot.index} keyframe`} />
        ) : (
          <div className="storyboard-empty-preview">
            <h3>Generate or upload a shot keyframe before animating.</h3>
            <p>Keyframes control the first frame when Image-to-Video is enabled.</p>
          </div>
        )}
      </div>

      <label className="file-control storyboard-upload">
        Upload custom keyframe
        <input
          type="file"
          accept=".png,.jpg,.jpeg,.webp,image/png,image/jpeg,image/webp"
          onChange={(event) => {
            const file = event.target.files?.[0];
            if (file) {
              onUploadStartImage?.(shot.sceneId, shot.id, file);
              event.target.value = "";
            }
          }}
        />
      </label>

      <div className="storyboard-shot-notes">
        <div>
          <h3>Shot Direction</h3>
          <p>{shot.action || shot.description || "No shot action available yet."}</p>
          <p className="muted">
            {shot.shotType || "Shot"} {shot.cameraMotion || shot.cameraDirection ? `with ${shot.cameraMotion || shot.cameraDirection}` : ""}
          </p>
        </div>
        <div>
          <h3>Narration / Dialogue</h3>
          {shot.sceneDialogueLines?.length ? (
            shot.sceneDialogueLines.slice(0, 3).map((line, index) => (
              <p key={`${shot.id}-dialogue-${index}`}>
                <b>{line.speaker || "Line"}:</b> {line.text}
              </p>
            ))
          ) : (
            <p className="muted">{shot.audioCue || "No dialogue lines are assigned to this scene."}</p>
          )}
        </div>
      </div>

      <div className="storyboard-history">
        <h3>Generation History</h3>
        {latestKeyframeJob ? (
          <p>
            Keyframe: <span className={`badge badge-${String(statusLabel(latestKeyframeJob.status)).toLowerCase()}`}>{statusLabel(latestKeyframeJob.status)}</span>
          </p>
        ) : (
          <p className="muted">No keyframe generation job yet.</p>
        )}
        {latestRender ? (
          <p>
            Render: <a href={latestRender.outputUrl ? toAbsoluteApiUrl(latestRender.outputUrl) : undefined} target="_blank" rel="noreferrer">Completed video</a>
            {latestRender.durationSeconds ? <span className="muted"> (rendered in {formatDuration(latestRender.durationSeconds)})</span> : null}
            {rawClipSecondsFromJob(latestRender) ? <span className="muted"> raw clip {formatDuration(rawClipSecondsFromJob(latestRender))}</span> : null}
          </p>
        ) : (
          <p className="muted">No completed render for this shot yet.</p>
        )}
      </div>
    </section>
  );
}

function ShotInspector({
  shot,
  jobs,
  useShotStartImage,
  renderDurationMode,
  useCharacterReferenceInPrompt,
  hasAnyShotStartImage,
  durationPlanSummary,
  missingKeyframeCount,
  isDurationPlanInvalid,
  isBusy,
  hasRunningRenderVideo,
  onUseShotStartImageChange,
  onRenderDurationModeChange,
  onUseCharacterReferenceChange,
  onSaveShotPrompt,
  onGenerateShotStartImage,
  onAnimateSelected,
  onAnimateAll,
  onRegenerateAll,
  onRepairStoryboardPrompts
}) {
  const [draft, setDraft] = useState({ startImagePrompt: "", startImageNegativePrompt: "" });

  useEffect(() => {
    setDraft({
      sceneId: shot?.sceneId,
      startImagePrompt: shot?.startImagePrompt || "",
      startImageNegativePrompt: shot?.startImageNegativePrompt || ""
    });
  }, [shot]);

  if (!shot) {
    return (
      <aside className="storyboard-inspector">
        <h3>Shot Inspector</h3>
        <p className="muted">Select a shot to edit prompts and animate it.</p>
      </aside>
    );
  }

  const runningJob = jobs.find(isRunningJob);
  const completedRender = getLatestCompletedRender(jobs);
  const longMotionFailure = latestLongMotionFailure(jobs);
  const hasKeyframe = Boolean(shot.startImageUrl || shot.startImagePath);
  const characters = shot.requiredCharacters || shot.sceneRequiredCharacters || [];
  const targetDurationSeconds = Number(shot.durationSeconds || shot.targetDurationSeconds || 0);
  const rawClipDurationSeconds = rawClipSecondsFromJob(completedRender);
  const rawClipIsShort = Boolean(rawClipDurationSeconds && targetDurationSeconds && rawClipDurationSeconds + 0.5 < targetDurationSeconds);
  const isLongMotion = renderDurationMode === "LongMotion";
  const isComfyUIParity = renderDurationMode === "ComfyUIParity";
  const isAutoQuality = renderDurationMode === "AutoQuality";
  const requiresKeyframe = isLongMotion || isComfyUIParity || isAutoQuality;
  const completedRenderMode = completedRender?.renderDurationMode || "FastPreview";
  const selectedProfileMissing = requiresKeyframe && completedRender && completedRenderMode !== renderDurationMode;
  const selectedProfileBlocked = requiresKeyframe && !hasKeyframe;
  const animateMissingBlocked = requiresKeyframe && missingKeyframeCount > 0;
  const promptHasGenericText = containsGenericPromptText(draft.startImagePrompt) || containsGenericPromptText(draft.startImageNegativePrompt);
  const canonicalCharacterLock = shot.characterLockPrompt || extractPromptField(draft.startImagePrompt, "Visible characters") || (characters.length ? characters.join(", ") : "No visible character lock.");
  const concreteLocationLock = shot.locationLockPrompt || extractPromptField(draft.startImagePrompt, "Concrete location") || shot.sceneLocation || "No concrete location lock.";
  const lightingSetup = extractPromptField(draft.startImagePrompt, "Lighting") || "Not available";

  return (
    <aside className="storyboard-inspector">
      <div className="storyboard-inspector-section">
        <div className="row">
          <h3>Inspector</h3>
          <span className={`badge ${hasKeyframe ? "badge-generated" : "badge-pending"}`}>{hasKeyframe ? "Keyframe ready" : "Needs keyframe"}</span>
        </div>
        <p className="muted">Wan2.2 animates from this shot keyframe when Image-to-Video is enabled.</p>
      </div>

      <div className="storyboard-inspector-section">
        <h3>On-screen Characters</h3>
        {characters.length ? (
          <div className="storyboard-character-chips">
            {characters.map((character) => <span key={character}>{character}</span>)}
          </div>
        ) : (
          <p className="muted">No character list is attached to this shot.</p>
        )}
      </div>

      <div className="storyboard-inspector-section">
        <h3>Continuity Locks Used</h3>
        <p>
          <b>Character lock:</b> {compactText(canonicalCharacterLock, 260)}
        </p>
        <p>
          <b>Location lock:</b> {compactText(concreteLocationLock, 260)}
        </p>
        <p>
          <b>Lighting:</b> {compactText(lightingSetup, 180)}
        </p>
        {promptHasGenericText ? (
          <p className="msg error compact-msg">This keyframe prompt contains generic placeholder continuity text. Repair prompts before rendering.</p>
        ) : null}
      </div>

      <div className="storyboard-inspector-section">
        <label className="check-control">
          <input
            type="checkbox"
            checked={useCharacterReferenceInPrompt}
            onChange={(event) => onUseCharacterReferenceChange(event.target.checked)}
          />
          Use character references in prompts
        </label>
        <label className="check-control">
          <input
            type="checkbox"
            checked={useShotStartImage}
            onChange={(event) => onUseShotStartImageChange(event.target.checked)}
          />
          Image-to-Video from keyframes
        </label>
        {hasAnyShotStartImage ? (
          <p className={useShotStartImage ? "msg ok compact-msg" : "msg error compact-msg"}>
            {useShotStartImage ? "Keyframes will be used for shots that have them." : "Keyframes exist, but rendering will stay Text-to-Video until this is enabled."}
          </p>
        ) : (
          <p className="muted">Generate keyframes to control the first frame of each video shot.</p>
        )}
      </div>

      <div className="storyboard-inspector-section">
        <h3>Keyframe Prompt</h3>
        <textarea
          rows={6}
          value={draft.startImagePrompt}
          onChange={(event) => setDraft((current) => ({ ...current, startImagePrompt: event.target.value }))}
          placeholder="Describe the shot keyframe..."
        />
        <textarea
          rows={3}
          value={draft.startImageNegativePrompt}
          onChange={(event) => setDraft((current) => ({ ...current, startImageNegativePrompt: event.target.value }))}
          placeholder="Negative prompt"
        />
        <div className="actions">
          <button type="button" onClick={() => onSaveShotPrompt(shot.sceneId, shot.id, draft)}>
            Save Prompt
          </button>
          <button type="button" disabled={!draft.startImagePrompt} onClick={() => copyText(draft.startImagePrompt)}>
            Copy Prompt
          </button>
        </div>
      </div>

      <div className="storyboard-inspector-section">
        <h3>Render Profile</h3>
        <RenderProfileSelector
          value={renderDurationMode}
          onChange={onRenderDurationModeChange}
          name="renderDurationModeInspector"
        />
        <p className="muted">
          FastPreview creates about a 1 second raw clip. Assembly can extend it to the shot timing, but that is not true generated long motion.
        </p>
        {requiresKeyframe ? (
          <p className="msg compact-msg storyboard-longmotion-note">
            {isAutoQuality
              ? "AutoQuality chooses existing render profiles per shot from director intent. Final-quality choices require keyframes and must not fake long motion."
              : isComfyUIParity
              ? "ComfyUIParity uses 161 frames, 18 steps, CFG 5.0, UniPC, and shift 8.0 where the Wan CLI supports them. It is the slowest/best consistency profile."
              : "LongMotion requires keyframes and attempts real longer raw motion. It is much slower."}
          </p>
        ) : null}
        {selectedProfileBlocked ? (
          <p className="msg error compact-msg">Generate keyframes before {renderDurationMode} render.</p>
        ) : null}
        {selectedProfileMissing ? (
          <p className="msg error compact-msg">This shot only has a {completedRenderMode} render. {renderDurationMode} render is still missing.</p>
        ) : null}
      </div>

      <div className="storyboard-inspector-section">
        <h3>Actions</h3>
        <div className="actions column">
          <button type="button" disabled={isBusy || isDurationPlanInvalid} onClick={() => onGenerateShotStartImage(shot.id)}>
            Regenerate Keyframe
          </button>
          <button type="button" disabled={isBusy || isDurationPlanInvalid || hasRunningRenderVideo || selectedProfileBlocked} onClick={() => onAnimateSelected(shot)}>
            Animate Selected ({renderDurationMode})
          </button>
          <button type="button" disabled={isBusy || isDurationPlanInvalid || hasRunningRenderVideo || animateMissingBlocked} onClick={onAnimateAll}>
            Animate Missing ({renderDurationMode})
          </button>
          {onRegenerateAll ? (
            <button type="button" disabled={isBusy || isDurationPlanInvalid || hasRunningRenderVideo || animateMissingBlocked} onClick={onRegenerateAll}>
              Regenerate All ({renderDurationMode})
            </button>
          ) : null}
          {onRepairStoryboardPrompts ? (
            <button type="button" disabled={isBusy} onClick={onRepairStoryboardPrompts}>
              Repair Storyboard Prompts
            </button>
          ) : null}
          <small className="muted">Animate Missing skips shots with completed renders. Regenerate All queues every shot again.</small>
          {isDurationPlanInvalid ? <small className="msg error compact-msg">Repair the director plan before rendering this storyboard.</small> : null}
          {animateMissingBlocked ? <small className="msg error compact-msg">{missingKeyframeCount} shot(s) need keyframes before LongMotion can queue all missing renders.</small> : null}
        </div>
      </div>

      <div className="storyboard-inspector-section">
        <h3>Job Status</h3>
        {runningJob ? <p>Current: <span className="badge badge-rendering">{statusLabel(runningJob.status)}</span></p> : null}
        {completedRender ? (
          <>
            <p>
              Rendered: <span className="badge badge-completed">Ready</span>
              {completedRender.durationSeconds ? <span className="muted"> processing {formatDuration(completedRender.durationSeconds)}</span> : null}
            </p>
            <div className="storyboard-render-duration-grid">
              <span>Target shot</span>
              <b>{targetDurationSeconds ? formatDuration(targetDurationSeconds) : "Not available"}</b>
              <span>Raw generated clip</span>
              <b>{rawClipDurationSeconds ? formatDuration(rawClipDurationSeconds) : "Unknown"}</b>
              <span>Render mode</span>
              <b>{completedRender.renderDurationMode || "FastPreview"}</b>
              <span>Frames</span>
              <b>{completedRender.actualFrameNum || completedRender.frameNum || "Unknown"} / requested {completedRender.requestedFrameNum || "Unknown"}</b>
              <span>Assembly timing</span>
              <b>{targetDurationSeconds ? formatDuration(targetDurationSeconds) : "Not available"}</b>
            </div>
            {rawClipIsShort ? (
              <p className="msg error compact-msg">
                Raw generated motion is shorter than the planned shot. Assembly will extend it, but use CinematicPreview or LongMotion for more real motion.
              </p>
            ) : null}
          </>
        ) : null}
        {longMotionFailure ? (
          <p className="msg error compact-msg">
            LongMotion failed: raw render is too short. This shot was not accepted because it would require fake loop extension.
            {longMotionFailure.errorMessage ? ` ${longMotionFailure.errorMessage}` : ""}
          </p>
        ) : null}
        {!runningJob && !completedRender ? <p className="muted">No render job for this shot yet.</p> : null}
      </div>
    </aside>
  );
}

export default function StoryboardStep({
  plan,
  jobs,
  selectedShotIds,
  useShotStartImage,
  renderDurationMode,
  useCharacterReferenceInPrompt,
  hasAnyShotStartImage,
  durationPlanSummary,
  continuitySummary,
  isBusy,
  hasRunningRenderVideo,
  onSelectShot,
  onUseShotStartImageChange,
  onRenderDurationModeChange,
  onUseCharacterReferenceChange,
  onSaveShotPrompt,
  onUploadStartImage,
  onGenerateShotStartImages,
  onGenerateShotStartImage,
  onAnimateSelected,
  onAnimateAll,
  onRegenerateAll,
  onRegeneratePlan,
  onRepairDirectorPlan,
  onRegenerateDirectorPlan,
  onRepairStoryboardPrompts
}) {
  const shots = useMemo(() => normalizePlanShots(plan), [plan]);
  const selectedShotId = selectedShotIds[0] || shots[0]?.id || null;
  const selectedShot = shots.find((shot) => shot.id === selectedShotId) || shots[0] || null;
  const jobsByKey = useMemo(() => {
    const map = new Map();
    for (const job of jobs || []) {
      const key = `${job.sceneIndex ?? "-"}-${job.shotIndex ?? "-"}`;
      const list = map.get(key) || [];
      list.push(job);
      map.set(key, list);
    }
    return map;
  }, [jobs]);
  const selectedJobs = selectedShotJobs(selectedShot, jobs || []);
  const completedShotCount = useMemo(
    () => shots.filter((shot) => getLatestCompletedRender(jobsByKey.get(getShotKey(shot)) || [])).length,
    [shots, jobsByKey]
  );
  const missingRenderCount = Math.max(0, shots.length - completedShotCount);
  const summary = durationPlanSummary || {};
  const continuity = continuitySummary || {};
  const shotHasKeyframe = (shot) => Boolean(shot?.startImageUrl || shot?.startImagePath);
  const keyframeReadyCount = shots.filter(shotHasKeyframe).length;
  const missingKeyframeCount = Math.max(0, shots.length - keyframeReadyCount);
  const selectedHasKeyframe = shotHasKeyframe(selectedShot);
  const isLongMotion = renderDurationMode === "LongMotion";
  const isComfyUIParity = renderDurationMode === "ComfyUIParity";
  const isAutoQuality = renderDurationMode === "AutoQuality";
  const requiresKeyframe = isLongMotion || isComfyUIParity || isAutoQuality;
  const targetDurationSeconds = Number(summary.targetDurationSeconds || plan?.targetDurationSeconds || 0);
  const recommendLongMotion = targetDurationSeconds >= 60 && renderDurationMode !== "LongMotion";
  const blockStrictBulkRender = requiresKeyframe && missingKeyframeCount > 0;
  const blockStrictSelectedRender = requiresKeyframe && selectedShot && !selectedHasKeyframe;
  const isDurationPlanInvalid = summary.isDurationPlanValid === false;

  if (!plan) {
    return (
      <section className="storyboard-empty-state">
        <h2>Analyze your story first.</h2>
        <p>The storyboard appears after Content creates scenes and shots.</p>
      </section>
    );
  }

  if (!shots.length) {
    return (
      <section className="storyboard-empty-state">
        <h2>No shots generated yet.</h2>
        <p>Refresh the production plan or analyze the story again to create storyboard shots.</p>
      </section>
    );
  }

  return (
    <div className="creator-step-panel storyboard-step">
      <section className="storyboard-step-head">
        <div>
          <span className="badge badge-ready">Storyboard</span>
          <h2>Review keyframes and animate shots</h2>
          <p>Choose a shot, refine its keyframe prompt, then animate selected shots through the existing render queue.</p>
        </div>
        <div className="storyboard-header-render-controls">
          <div className="row">
            <div>
              <span className="badge">Render Profile</span>
              <h3>{renderDurationMode}</h3>
            </div>
            <span className="badge">{keyframeReadyCount}/{shots.length} keyframes</span>
          </div>
          <RenderProfileSelector
            value={renderDurationMode}
            onChange={onRenderDurationModeChange}
            name="renderDurationModeHeader"
            className="header-profile-options"
          />
          {requiresKeyframe ? (
            <p className="msg compact-msg storyboard-longmotion-note">
              {isAutoQuality
                ? "AutoQuality follows the director render strategy per shot. It requires keyframes because final-quality choices use Image-to-Video and must not fake long motion."
                : isComfyUIParity
                ? "ComfyUIParity uses 161 frames, 18 steps, CFG 5.0, UniPC, and shift 8.0 where supported. It is closest to the reference workflow and is much slower."
                : "LongMotion requires keyframes and attempts real longer raw motion. It is much slower."}
            </p>
          ) : null}
          {recommendLongMotion ? (
            <p className="msg compact-msg storyboard-longmotion-recommendation">For final-quality film render, select LongMotion.</p>
          ) : null}
          {blockStrictBulkRender ? (
            <p className="msg error compact-msg">Generate keyframes before {renderDurationMode} render. {missingKeyframeCount} shot(s) are missing keyframes.</p>
          ) : null}
          {blockStrictSelectedRender ? (
            <p className="msg error compact-msg">The selected shot needs a keyframe before Animate Selected ({renderDurationMode}).</p>
          ) : null}
        </div>
        <div className="storyboard-head-actions">
          {onRepairDirectorPlan ? (
            <button type="button" disabled={isBusy || !isDurationPlanInvalid} onClick={onRepairDirectorPlan}>
              Repair Duration Plan
            </button>
          ) : null}
          {onRegenerateDirectorPlan ? (
            <button type="button" disabled={isBusy} onClick={onRegenerateDirectorPlan}>
              Regenerate Director Plan
            </button>
          ) : null}
          {onRegeneratePlan ? (
            <button type="button" disabled={isBusy} onClick={onRegeneratePlan}>
              Analyze Again
            </button>
          ) : null}
          {onRepairStoryboardPrompts ? (
            <button type="button" disabled={isBusy} onClick={onRepairStoryboardPrompts}>
              Repair Storyboard Prompts
            </button>
          ) : null}
          <button type="button" disabled={isBusy || !plan || isDurationPlanInvalid} onClick={onGenerateShotStartImages}>
            Generate Keyframes
          </button>
          <button type="button" disabled={isBusy || isDurationPlanInvalid || hasRunningRenderVideo || blockStrictBulkRender} onClick={onAnimateAll}>
            Animate Missing ({renderDurationMode})
          </button>
          {onRegenerateAll ? (
            <button type="button" disabled={isBusy || isDurationPlanInvalid || hasRunningRenderVideo || blockStrictBulkRender} onClick={onRegenerateAll}>
              Regenerate All ({renderDurationMode})
            </button>
          ) : null}
        </div>
      </section>

      <p className="msg compact-msg storyboard-render-reuse-note">
        {completedShotCount > 0
          ? `Animate Missing will skip ${completedShotCount} completed shot(s) and queue ${missingRenderCount} missing shot(s).`
          : "Animate Missing will queue storyboard shots that do not have completed renders yet."}
      </p>

      <section className={`storyboard-duration-summary ${summary.isDurationPlanValid === false ? "invalid" : ""}`}>
        <div>
          <span className="muted">Target duration</span>
          <b>{formatDuration(summary.targetDurationSeconds || 0) || "Not set"}</b>
        </div>
        <div>
          <span className="muted">Planned duration</span>
          <b>{formatDuration(summary.totalPlannedDurationSeconds || 0) || "0s"}</b>
        </div>
        <div>
          <span className="muted">Scenes</span>
          <b>{summary.sceneCount ?? shots.reduce((count, shot) => Math.max(count, shot.sceneIndex || 0), 0)}</b>
        </div>
        <div>
          <span className="muted">Shots</span>
          <b>{summary.shotCount ?? shots.length}</b>
        </div>
        <div>
          <span className="muted">Coverage</span>
          <b>{summary.plannedDurationCoveragePercent ?? 0}%</b>
        </div>
      </section>

      {summary.isDurationPlanValid === false || summary.durationPlanWarning ? (
        <div className="msg error compact-msg storyboard-warning-action">
          <span>{summary.durationPlanWarning || "Storyboard is too short for the target duration. Regenerate or repair plan before rendering."}</span>
          {onRepairDirectorPlan ? (
            <button type="button" disabled={isBusy} onClick={onRepairDirectorPlan}>
              Repair Duration Plan
            </button>
          ) : null}
          {onRegenerateDirectorPlan ? (
            <button type="button" disabled={isBusy} onClick={onRegenerateDirectorPlan}>
              Regenerate Director Plan
            </button>
          ) : null}
        </div>
      ) : null}

      <section className={`storyboard-continuity-summary ${continuity.characterVisualLocksApplied === false ? "invalid" : ""}`}>
        <div>
          <span className="muted">Director plan</span>
          <b>{continuity.hasDirectorPlan ? "Ready" : "Needs analyze"}</b>
        </div>
        <div>
          <span className="muted">Story structure</span>
          <b>{continuity.storyStructureValid ? "Valid" : "Weak"}</b>
        </div>
        <div>
          <span className="muted">Character bible</span>
          <b>{continuity.hasContinuityBible ? "Ready" : "Needs analyze"}</b>
        </div>
        <div>
          <span className="muted">Visual locks</span>
          <b>{continuity.characterVisualLocksApplied ? "Applied" : "Check prompts"}</b>
        </div>
        <div>
          <span className="muted">Negative prompts</span>
          <b>{continuity.distinctNegativePromptCount ?? 0} distinct</b>
        </div>
        <div>
          <span className="muted">References</span>
          <b>{continuity.characterReferenceCount ?? 0}/{continuity.characterCount ?? 0}</b>
        </div>
        <div>
          <span className="muted">Keyframes</span>
          <b>{continuity.shotStartImageCount ?? 0}/{continuity.shotCount ?? shots.length}</b>
        </div>
        <div>
          <span className="muted">Location continuity</span>
          <b>{continuity.locationContinuityValid ? "Ready" : "Check"}</b>
        </div>
        <div>
          <span className="muted">Keyframe continuity</span>
          <b>{continuity.keyframeContinuityValid ? "Connected" : "Needs prompts"}</b>
        </div>
        <div>
          <span className="muted">Render strategy</span>
          <b>{continuity.renderStrategyName || "Manual"}</b>
        </div>
        <div>
          <span className="muted">Assembly extension</span>
          <b>{continuity.assemblyExtensionPolicy || "Preview-only when short"}</b>
        </div>
        <div>
          <span className="muted">I2V renders</span>
          <b>{continuity.startImagesUsedByVideoCount ?? 0}</b>
        </div>
      </section>

      {continuity.continuityWarning ? <p className="msg error compact-msg">{continuity.continuityWarning}</p> : null}
      {(continuity.characterReferenceCount ?? 0) > 0 && (continuity.startImagesUsedByVideoCount ?? 0) === 0 ? (
        <p className="msg compact-msg storyboard-keyframe-note">
          Character reference images are available, but this render backend is using text-only conditioning unless a shot keyframe is used as Image-to-Video.
        </p>
      ) : null}

      {!hasAnyShotStartImage ? (
        <p className="msg compact-msg storyboard-keyframe-note">Generate keyframes to control the first frame of each video shot.</p>
      ) : null}

      <div className="storyboard-editor-layout">
        <ShotRail shots={shots} selectedShotId={selectedShot?.id} jobsByKey={jobsByKey} onSelectShot={onSelectShot} />
        <ShotPreview shot={selectedShot} jobs={selectedJobs} onUploadStartImage={onUploadStartImage} />
        <ShotInspector
          shot={selectedShot}
          jobs={selectedJobs}
          useShotStartImage={useShotStartImage}
          renderDurationMode={renderDurationMode}
          useCharacterReferenceInPrompt={useCharacterReferenceInPrompt}
          hasAnyShotStartImage={hasAnyShotStartImage}
          missingKeyframeCount={missingKeyframeCount}
          isDurationPlanInvalid={isDurationPlanInvalid}
          isBusy={isBusy}
          hasRunningRenderVideo={hasRunningRenderVideo}
          onUseShotStartImageChange={onUseShotStartImageChange}
          onRenderDurationModeChange={onRenderDurationModeChange}
          onUseCharacterReferenceChange={onUseCharacterReferenceChange}
          onSaveShotPrompt={onSaveShotPrompt}
          onGenerateShotStartImage={onGenerateShotStartImage}
          onAnimateSelected={onAnimateSelected}
          onAnimateAll={onAnimateAll}
          onRegenerateAll={onRegenerateAll}
          onRepairStoryboardPrompts={onRepairStoryboardPrompts}
        />
      </div>

      <details className="storyboard-monitor-details">
        <summary>Advanced render job monitor</summary>
        <div className="storyboard-job-list">
          {selectedJobs.length ? (
            selectedJobs.map((job) => (
              <div className="job" key={job.jobId || job.id}>
                <div className="row">
                  <b>{job.jobTypeName || job.jobType}</b>
                  <span className={`badge badge-${String(statusLabel(job.status)).toLowerCase()}`}>{statusLabel(job.status)}</span>
                </div>
                <p>Progress: {job.progress ?? 0}%</p>
                {job.outputUrl ? (
                  <a href={toAbsoluteApiUrl(job.outputUrl)} target="_blank" rel="noreferrer">
                    Open output
                  </a>
                ) : null}
                {job.durationSeconds ? <p className="muted">Duration: {formatDuration(job.durationSeconds)}</p> : null}
                <p className="muted">
                  Mode: {job.renderDurationMode || "FastPreview"} | target {formatDuration(job.requestedShotDurationSeconds) || "n/a"} | frames {job.actualFrameNum || job.frameNum || "n/a"} / requested {job.requestedFrameNum || "n/a"} | raw {formatDuration(rawClipSecondsFromJob(job)) || "unknown"}
                </p>
                {job.errorMessage ? <p className="msg error">{job.errorMessage}</p> : null}
              </div>
            ))
          ) : (
            <p className="muted">No jobs for the selected shot yet.</p>
          )}
        </div>
      </details>
    </div>
  );
}
