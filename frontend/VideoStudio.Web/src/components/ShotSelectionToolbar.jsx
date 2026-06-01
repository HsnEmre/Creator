export default function ShotSelectionToolbar({
  selectedCount,
  onRenderSelected,
  onRenderScene,
  onRenderAll,
  isBusy
}) {
  return (
    <section className="card compact-card">
      <div className="row">
        <h2>Shot Selection</h2>
        <span className="muted">{selectedCount} selected</span>
      </div>
      <div className="actions">
        <button disabled={isBusy || selectedCount === 0} onClick={onRenderSelected}>
          Render Selected
        </button>
        <button disabled={isBusy} onClick={onRenderScene}>
          Render Scene
        </button>
        <button disabled={isBusy} onClick={onRenderAll}>
          Render All
        </button>
      </div>
    </section>
  );
}
