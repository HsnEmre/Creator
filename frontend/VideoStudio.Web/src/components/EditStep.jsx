import { toAbsoluteApiUrl } from "../api/client";
import DialogueLinesPanel from "./DialogueLinesPanel";
import RenderJobsPanel from "./RenderJobsPanel";
import VideoPreviewPanel from "./VideoPreviewPanel";

function statusLabel({ ready, running, waitingLabel = "Missing" }) {
  if (running) return "Running";
  if (ready) return "Ready";
  return waitingLabel;
}

function statusClass(label) {
  if (label === "Ready") return "badge-generated";
  if (label === "Running") return "badge-rendering";
  return "badge-pending";
}

function ChecklistCard({ title, status, description, actionLabel, onAction, disabled }) {
  return (
    <article className="edit-check-card">
      <div className="row">
        <h3>{title}</h3>
        <span className={`badge ${statusClass(status)}`}>{status}</span>
      </div>
      <p className="muted">{description}</p>
      {actionLabel ? (
        <button type="button" disabled={disabled} onClick={onAction}>
          {actionLabel}
        </button>
      ) : null}
    </article>
  );
}

export default function EditStep({
  finalVideo,
  latestCompletedRender,
  jobs,
  dialogueLines,
  shotCount,
  completedRenderCount,
  completedAudioCount,
  hasAssembly,
  hasFinal,
  hasRunningRenderVideo,
  hasRunningAudio,
  hasRunningFinalize,
  busyAction,
  onAssemble,
  onGenerateAudio,
  onRegenerateAudio,
  onFinalize,
  onRefinalize,
  onRefresh,
  onRefreshJobs,
  onRefreshDialogueLines,
  onResetStale,
  onCleanupJobs,
  onGoStoryboard
}) {
  const isBusy = Boolean(busyAction);
  const missingRenderCount = Math.max(0, (shotCount || 0) - completedRenderCount);
  const previewUrl = finalVideo?.mediaUrl || finalVideo?.assembledMediaUrl || latestCompletedRender?.outputUrl || null;
  const renderStatus = statusLabel({
    ready: completedRenderCount > 0,
    running: hasRunningRenderVideo,
    waitingLabel: "Missing"
  });
  const assemblyStatus = statusLabel({
    ready: hasAssembly,
    running: jobs.some((job) => job.jobTypeName === "AssembleVideo" && [0, 1, "Pending", "Rendering"].includes(job.status)),
    waitingLabel: completedRenderCount ? "Ready next" : "Missing"
  });
  const audioStatus = statusLabel({
    ready: completedAudioCount > 0,
    running: hasRunningAudio,
    waitingLabel: dialogueLines.length ? "Ready next" : "Missing"
  });
  const finalStatus = statusLabel({
    ready: hasFinal,
    running: hasRunningFinalize,
    waitingLabel: hasAssembly || completedRenderCount ? "Ready next" : "Missing"
  });

  return (
    <div className="creator-step-panel edit-step">
      <section className="edit-hero">
        <div>
          <span className="badge badge-ready">Edit</span>
          <h2>Finalize your video</h2>
          <p>Assemble rendered shots, add voiceover or dialogue audio, and create the final preview for review.</p>
        </div>
        <div className="edit-hero-actions">
          <button type="button" disabled={isBusy} onClick={onRefresh}>
            Refresh Status
          </button>
          {previewUrl ? (
            <a className="button-link" href={toAbsoluteApiUrl(previewUrl)} target="_blank" rel="noreferrer">
              Open Media URL
            </a>
          ) : null}
        </div>
      </section>

      <section className="edit-preview-grid">
        <div className="edit-preview-card">
          <VideoPreviewPanel
            finalMediaUrl={finalVideo?.mediaUrl || null}
            assembledMediaUrl={finalVideo?.assembledMediaUrl || null}
            renderMediaUrl={latestCompletedRender?.outputUrl || null}
            outputPath={finalVideo?.localPath || latestCompletedRender?.outputPath || null}
            assembledPath={finalVideo?.assembledLocalPath || null}
          />
          {!previewUrl ? (
            <div className="edit-preview-empty">
              <h3>Animate and assemble shots to create a final preview.</h3>
              <p className="muted">The final movie appears here after rendered shots are assembled and finalized.</p>
            </div>
          ) : null}
        </div>

        <aside className="edit-summary-card">
          <h3>Rendered Shots</h3>
          <p>
            <b>{completedRenderCount}</b> of <b>{shotCount || 0}</b> planned shot(s) have completed renders.
          </p>
          {missingRenderCount ? (
            <p className="muted">Return to Storyboard and animate the remaining {missingRenderCount} shot(s) before building the full movie.</p>
          ) : (
            <p className="muted">All planned shots with known count are ready for assembly.</p>
          )}
          <div className="actions">
            <button type="button" onClick={onGoStoryboard}>
              Back to Storyboard
            </button>
          </div>
        </aside>
      </section>

      <section className="edit-checklist">
        <ChecklistCard
          title="Shots rendered"
          status={renderStatus}
          description={completedRenderCount ? `${completedRenderCount} rendered shot(s) are available.` : "Animate shots from the Storyboard step first."}
          actionLabel={completedRenderCount ? null : "Go to Storyboard"}
          onAction={onGoStoryboard}
          disabled={false}
        />
        <ChecklistCard
          title="Movie assembled"
          status={assemblyStatus}
          description={hasAssembly ? "An assembled movie is available." : "Assemble rendered shots into a movie."}
          actionLabel={hasAssembly ? "Reassemble Movie" : "Assemble Movie"}
          onAction={onAssemble}
          disabled={isBusy || !completedRenderCount}
        />
        <ChecklistCard
          title="Audio generated"
          status={audioStatus}
          description={completedAudioCount ? `${completedAudioCount} dialogue audio file(s) are ready.` : "Generate dialogue or voiceover audio for the final preview."}
          actionLabel={completedAudioCount ? "Regenerate Audio" : "Generate Voiceover / Audio"}
          onAction={completedAudioCount ? onRegenerateAudio : onGenerateAudio}
          disabled={isBusy || hasRunningAudio || !dialogueLines.length}
        />
        <ChecklistCard
          title="Final preview ready"
          status={finalStatus}
          description={hasFinal ? "The final preview is ready to watch." : "Finalize to mux audio and create the preview."}
          actionLabel={hasFinal ? "Re-finalize Preview" : "Finalize Preview"}
          onAction={hasFinal ? onRefinalize : onFinalize}
          disabled={isBusy || hasRunningFinalize || (!hasAssembly && !completedRenderCount)}
        />
      </section>

      <section className="edit-voiceover-section">
        <div className="row">
          <div>
            <h2>Voiceover / Dialogue</h2>
            <p className="muted">{dialogueLines.length ? `${dialogueLines.length} line(s) available for audio generation.` : "No dialogue lines are available yet."}</p>
          </div>
          <div className="actions">
            <button type="button" disabled={isBusy || hasRunningAudio || !dialogueLines.length} onClick={onGenerateAudio}>
              Generate Audio
            </button>
            <button type="button" disabled={isBusy || hasRunningAudio || !dialogueLines.length} onClick={onRegenerateAudio}>
              Regenerate Audio
            </button>
          </div>
        </div>
        <DialogueLinesPanel lines={dialogueLines} onRefresh={onRefreshDialogueLines} />
      </section>

      <details className="edit-monitor-details">
        <summary>Job monitor</summary>
        <RenderJobsPanel jobs={jobs} onRefresh={onRefreshJobs} />
      </details>

      <details className="edit-developer-tools">
        <summary>Developer tools</summary>
        <p className="muted">Local queue recovery tools for development only.</p>
        <div className="actions">
          <button type="button" disabled={isBusy} onClick={onResetStale}>
            {busyAction === "reset-stale" ? "Resetting..." : "Reset Stale Jobs (30m)"}
          </button>
          <button type="button" disabled={isBusy} onClick={onCleanupJobs}>
            {busyAction === "cleanup" ? "Cleaning..." : "Cleanup Jobs"}
          </button>
        </div>
      </details>
    </div>
  );
}
