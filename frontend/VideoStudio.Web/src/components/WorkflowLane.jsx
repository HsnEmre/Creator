export default function WorkflowLane({ number, title, status, summary, children, className = "" }) {
  return (
    <section className={`workflow-lane ${className}`} aria-labelledby={`workflow-lane-${number}`}>
      <div className="workflow-lane-header">
        <div className="workflow-lane-title">
          <span className="workflow-lane-number">{number}</span>
          <div>
            <h2 id={`workflow-lane-${number}`}>{title}</h2>
            {summary ? <p className="muted">{summary}</p> : null}
          </div>
        </div>
        <span className={`badge lane-status lane-status-${String(status || "waiting").toLowerCase()}`}>{status || "Waiting"}</span>
      </div>
      <div className="workflow-lane-body">{children}</div>
    </section>
  );
}
