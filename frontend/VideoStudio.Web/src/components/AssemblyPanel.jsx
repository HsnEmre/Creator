export default function AssemblyPanel({ onAssemble, onFinalize, isBusy, finalVideo }) {
  return (
    <section className="card">
      <h2>Movie Assembly</h2>
      <p className="muted">Assemble completed shot renders in scene/shot order, then mux audio into the final preview.</p>
      <div className="actions">
        <button disabled={isBusy} onClick={onAssemble}>
          Assemble Movie
        </button>
        <button disabled={isBusy} onClick={onFinalize}>
          Finalize Movie
        </button>
      </div>
      {finalVideo?.assembledMediaUrl ? <p className="path">Assembled: {finalVideo.assembledMediaUrl}</p> : null}
      {finalVideo?.mediaUrl ? <p className="path">Final: {finalVideo.mediaUrl}</p> : null}
    </section>
  );
}
