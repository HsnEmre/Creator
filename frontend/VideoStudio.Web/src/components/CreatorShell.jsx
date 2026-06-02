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
  rightRail
}) {
  return (
    <div className="creator-shell">
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

      <div className="creator-workspace">
        <main className="creator-main">{children}</main>
        <aside className="creator-rail">{rightRail}</aside>
      </div>
    </div>
  );
}
