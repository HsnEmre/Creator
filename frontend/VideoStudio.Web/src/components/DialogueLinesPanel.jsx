import { toAbsoluteApiUrl } from "../api/client";

export default function DialogueLinesPanel({ lines, onRefresh }) {
  return (
    <section className="card">
      <div className="row">
        <h2>Dialogue Lines</h2>
        <button onClick={onRefresh}>Refresh</button>
      </div>
      {!lines.length ? <p>No dialogue lines available.</p> : null}
      {lines.map((line) => (
        <div className="list-item" key={line.id}>
          <p>
            <b>{line.speaker || "Unknown"}</b> <span className="muted">[{line.estimatedStartSecond}-{line.estimatedEndSecond}]</span>
          </p>
          <p className="muted">Emotion: {line.emotion || "-"}</p>
          <p>{line.text}</p>
          {line.audioUrl ? <audio controls src={toAbsoluteApiUrl(line.audioUrl)} /> : null}
          {!line.audioUrl && line.audioPath ? <p className="path">Audio Path: {line.audioPath}</p> : null}
        </div>
      ))}
    </section>
  );
}
