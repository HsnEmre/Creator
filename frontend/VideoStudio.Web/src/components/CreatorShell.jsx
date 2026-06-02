import CreatorStepper from "./CreatorStepper";

export default function CreatorShell({
  title,
  projectStatus,
  activeJobCount,
  steps,
  activeStep,
  onStepChange,
  onBack,
  onRefresh,
  message,
  error,
  children,
  rightRail,
  railCollapsed = false
}) {
  return (
    <div className={`creator-shell ${railCollapsed ? "creator-shell-focused" : ""}`}>
      <header className="creator-topbar">
        <div className="creator-title-block">
          <p className="eyebrow">VideoStudio Creator</p>
          <h1>{title}</h1>
        </div>
        <div className="creator-topbar-actions">
          {projectStatus ? <span className="badge">{projectStatus}</span> : null}
          <span className={`badge ${activeJobCount ? "badge-rendering" : ""}`}>
            {activeJobCount ? `${activeJobCount} active job(s)` : "No active jobs"}
          </span>
          <button type="button" onClick={onRefresh}>Refresh</button>
          <button type="button" onClick={onBack}>Back to Projects</button>
        </div>
      </header>

      <CreatorStepper steps={steps} activeStep={activeStep} onStepChange={onStepChange} />

      {message ? <p className="msg ok">{message}</p> : null}
      {error ? <p className="msg error">{error}</p> : null}

      {railCollapsed && rightRail ? (
        <details className="creator-monitor-drawer">
          <summary>
            <span>Monitor</span>
            <small>Preview and job details</small>
          </summary>
          <div className="creator-monitor-drawer-body">{rightRail}</div>
        </details>
      ) : null}

      <div className={`creator-workspace ${railCollapsed ? "creator-workspace-focused" : ""}`}>
        <main className={`creator-main ${railCollapsed ? "creator-main-focused" : ""}`}>{children}</main>
        {!railCollapsed ? <aside className="creator-rail">{rightRail}</aside> : null}
      </div>
    </div>
  );
}
