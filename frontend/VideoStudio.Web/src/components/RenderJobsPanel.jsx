import { toAbsoluteApiUrl } from "../api/client";

function statusLabel(value) {
  if (typeof value === "string") {
    return value;
  }
  if (value === 0) return "Pending";
  if (value === 1) return "Rendering";
  if (value === 2) return "Completed";
  if (value === 3) return "Failed";
  return `Unknown(${value})`;
}

export default function RenderJobsPanel({ jobs, onRefresh }) {
  const groups = {
    Video: jobs.filter((j) => (j.jobTypeName || "").toLowerCase().includes("render")),
    Visuals: jobs.filter((j) => ["GenerateCharacterReferenceImage", "GenerateShotStartImage"].includes(j.jobTypeName)),
    Audio: jobs.filter((j) => (j.jobTypeName || "").toLowerCase().includes("audio") && (j.jobTypeName || "") !== "MuxAudio"),
    Assembly: jobs.filter((j) => (j.jobTypeName || "") === "AssembleVideo"),
    Final: jobs.filter((j) => (j.jobTypeName || "") === "MuxAudio"),
    Other: jobs.filter((j) => !["RenderVideo", "GenerateAudio", "AssembleVideo", "MuxAudio", "GenerateCharacterReferenceImage", "GenerateShotStartImage"].includes(j.jobTypeName))
  };
  const latestFinal = groups.Final.find((j) => statusLabel(j.status).toLowerCase() === "completed");

  return (
    <section className="card">
      <div className="row">
        <h2>Render Jobs</h2>
        <button onClick={onRefresh}>Refresh</button>
      </div>
      {latestFinal ? (
        <div className="job final-highlight">
          <p><b>Latest Final Video Ready</b></p>
          {latestFinal.outputUrl ? (
            <a href={toAbsoluteApiUrl(latestFinal.outputUrl)} target="_blank" rel="noreferrer">
              Open final media
            </a>
          ) : null}
          <p className="path">{latestFinal.outputPath || "-"}</p>
        </div>
      ) : null}
      {!jobs.length ? <p>No jobs yet.</p> : null}
      {Object.entries(groups).map(([groupName, groupJobs]) =>
        groupJobs.length ? (
          <div key={groupName} className="subcard">
            <h3>{groupName}</h3>
            {groupJobs.map((job) => (
              <div className="job" key={job.jobId || job.id}>
                <div className="row">
                  <b>{job.jobId || job.id}</b>
                  <span className={`badge badge-${String(statusLabel(job.status)).toLowerCase()}`}>{statusLabel(job.status)}</span>
                </div>
                <p>
                  Type: <b>{job.jobTypeName || job.jobType || "-"}</b>
                </p>
                <p>Mode: {job.generationModeName || job.generationMode || "-"}</p>
                <p>
                  Scene: {job.sceneIndex ?? "-"} | Shot: {job.shotIndex ?? "-"}
                </p>
                <p>Progress: {job.progress ?? 0}%</p>
                <p>Preset: {job.preset ?? "-"}</p>
                {job.outputUrl ? (
                  <p>
                    <a href={toAbsoluteApiUrl(job.outputUrl)} target="_blank" rel="noreferrer">
                      Open media
                    </a>
                  </p>
                ) : null}
                {job.outputUrl && ["GenerateCharacterReferenceImage", "GenerateShotStartImage"].includes(job.jobTypeName) ? (
                  <img className="reference-image" src={toAbsoluteApiUrl(job.outputUrl)} alt={`${job.jobTypeName} output`} />
                ) : null}
                {job.inputImageUrl ? (
                  <div className="reference-inline">
                    <img src={toAbsoluteApiUrl(job.inputImageUrl)} alt="Input reference" />
                    <span>Shot start image</span>
                  </div>
                ) : (
                  <p className="muted">No start image used.</p>
                )}
                {job.inputImagePath ? <p className="path">Input image: {job.inputImagePath}</p> : null}
                <p className="path">Output: {job.outputPath || "-"}</p>
                {job.errorMessage ? <p className="msg error">{job.errorMessage}</p> : null}
                <p className="muted">Created: {job.createdAt || "-"}</p>
              </div>
            ))}
          </div>
        ) : null
      )}
    </section>
  );
}
