export default function ActionToolbar({
  isBusy,
  busyAction,
  renderBusy,
  audioBusy,
  finalizeBusy,
  useCharacterReferenceInPrompt,
  useShotStartImage,
  hasAnyShotStartImage,
  onUseCharacterReferenceInPromptChange,
  onUseShotStartImageChange,
  onAnalyze,
  onPrepareVisuals,
  onGenerateCharacterReferences,
  onGenerateShotStartImages,
  onRegenerateMissingVisuals,
  onRenderFastPreview,
  onGenerateAudio,
  onRegenerateAudio,
  onFinalize,
  onRefinalize,
  onResetStale,
  onCleanupJobs
}) {
  return (
    <section className="card">
      <h2>Actions</h2>
      <div className="actions column">
        <label className="check-control">
          <input
            type="checkbox"
            checked={useCharacterReferenceInPrompt}
            onChange={(event) => onUseCharacterReferenceInPromptChange?.(event.target.checked)}
          />
          Use character references in prompt
        </label>
        <label className="check-control">
          <input
            type="checkbox"
            checked={useShotStartImage}
            onChange={(event) => onUseShotStartImageChange?.(event.target.checked)}
          />
          Enable Image-to-Video with shot start images
        </label>
        {hasAnyShotStartImage ? (
          useShotStartImage ? (
            <p className="msg ok compact-msg">
              Shot start images are available. Image-to-Video is enabled, so Wan2.2 will use them as start frames.
            </p>
          ) : (
            <p className="msg error compact-msg">
              Shot start images are available. Enable Image-to-Video to use them as Wan2.2 start frames.
            </p>
          )
        ) : (
          <p className="muted compact-msg">
            No shot start images are available yet. Video renders will use Text-to-Video until keyframes are generated or uploaded.
          </p>
        )}
        <button disabled={isBusy} onClick={onAnalyze}>
          {busyAction === "analyze" ? "Analyzing..." : "Analyze Story"}
        </button>
        <button disabled={isBusy} onClick={onPrepareVisuals}>
          {busyAction === "prepare-visuals" ? "Preparing..." : "Prepare Visuals"}
        </button>
        <button disabled={isBusy} onClick={onGenerateCharacterReferences}>
          {busyAction === "generate-character-references" ? "Queueing..." : "Generate Character References"}
        </button>
        <button disabled={isBusy} onClick={onGenerateShotStartImages}>
          {busyAction === "generate-shot-start-images" ? "Queueing..." : "Generate Shot Start Images"}
        </button>
        <button disabled={isBusy} onClick={onRegenerateMissingVisuals}>
          {busyAction === "regenerate-missing-visuals" ? "Queueing..." : "Regenerate Missing Visuals"}
        </button>
        <button disabled={isBusy || renderBusy} onClick={onRenderFastPreview}>
          {busyAction === "render" ? "Queueing..." : "Render FastPreview (1 Shot)"}
        </button>
        <button disabled={isBusy || audioBusy} onClick={onGenerateAudio}>
          {busyAction === "audio" ? "Queueing..." : "Generate Audio"}
        </button>
        <button disabled={isBusy || audioBusy} onClick={onRegenerateAudio}>
          {busyAction === "audio-force" ? "Queueing..." : "Regenerate Audio"}
        </button>
        <button disabled={isBusy || finalizeBusy} onClick={onFinalize}>
          {busyAction === "finalize" ? "Queueing..." : "Finalize Video"}
        </button>
        <button disabled={isBusy || finalizeBusy} onClick={onRefinalize}>
          {busyAction === "refinalize" ? "Queueing..." : "Re-finalize Video"}
        </button>
        <button disabled={isBusy} onClick={onResetStale}>
          {busyAction === "reset-stale" ? "Resetting..." : "Reset Stale Jobs (30m)"}
        </button>
        <button disabled={isBusy} onClick={onCleanupJobs}>
          {busyAction === "cleanup" ? "Cleaning..." : "Cleanup Jobs"}
        </button>
      </div>
    </section>
  );
}
