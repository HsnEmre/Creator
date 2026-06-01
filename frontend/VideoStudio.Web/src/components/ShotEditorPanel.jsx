import { useEffect, useState } from "react";
import { toAbsoluteApiUrl } from "../api/client";

export default function ShotEditorPanel({
  scene,
  shots,
  selectedShotIds,
  onToggleShot,
  onSaveShot,
  onUploadStartImage
}) {
  const [drafts, setDrafts] = useState({});

  useEffect(() => {
    const next = {};
    for (const shot of shots) {
      next[shot.id] = { ...shot };
    }
    setDrafts(next);
  }, [shots]);

  if (!scene) {
    return (
      <section className="card">
        <h2>Shot Editor</h2>
        <p>Select a scene to review shots.</p>
      </section>
    );
  }

  function update(shotId, field, value) {
    setDrafts((current) => ({ ...current, [shotId]: { ...(current[shotId] || {}), [field]: value } }));
  }

  return (
    <section className="card">
      <h2>Shots</h2>
      {!shots.length ? <p>No shots in this scene.</p> : null}
      <div className="list">
        {shots.map((shot) => {
          const draft = drafts[shot.id] || shot;
          const checked = selectedShotIds.includes(shot.id);
          return (
            <div className={`list-item ${checked ? "selected-soft" : ""}`} key={shot.id}>
              <div className="row">
                <label className="check-control">
                  <input type="checkbox" checked={checked} onChange={() => onToggleShot(shot.id)} />
                  Shot {shot.shotIndex}
                </label>
                <span className="badge">{shot.startImageUrl || shot.startImagePath ? "I2V Ready" : "Needs Start Image"}</span>
              </div>
              {shot.startImageUrl ? (
                <img className="reference-image" src={toAbsoluteApiUrl(shot.startImageUrl)} alt={`Shot ${shot.shotIndex} start`} />
              ) : null}
              <label className="file-control">
                Upload start image / keyframe
                <input
                  type="file"
                  accept=".png,.jpg,.jpeg,.webp,image/png,image/jpeg,image/webp"
                  onChange={(event) => {
                    const file = event.target.files?.[0];
                    if (file) {
                      onUploadStartImage(shot.sceneId, shot.id, file);
                      event.target.value = "";
                    }
                  }}
                />
              </label>
              <div className="form-grid">
                <label>
                  Index
                  <input type="number" value={draft.shotIndex || 1} onChange={(e) => update(shot.id, "shotIndex", Number(e.target.value))} />
                </label>
                <label>
                  Duration
                  <input
                    type="number"
                    value={draft.targetDurationSeconds || 5}
                    onChange={(e) => update(shot.id, "targetDurationSeconds", Number(e.target.value))}
                  />
                </label>
                <label className="full">
                  Visual Prompt
                  <textarea rows={5} value={draft.visualPrompt || ""} onChange={(e) => update(shot.id, "visualPrompt", e.target.value)} />
                </label>
                <label className="full">
                  Negative Prompt
                  <textarea rows={3} value={draft.negativePrompt || ""} onChange={(e) => update(shot.id, "negativePrompt", e.target.value)} />
                </label>
                <label>
                  Camera
                  <input value={draft.cameraDirection || ""} onChange={(e) => update(shot.id, "cameraDirection", e.target.value)} />
                </label>
                <label>
                  Motion
                  <input value={draft.motionDirection || ""} onChange={(e) => update(shot.id, "motionDirection", e.target.value)} />
                </label>
                <label className="full">
                  Description / Action
                  <textarea rows={3} value={draft.description || ""} onChange={(e) => update(shot.id, "description", e.target.value)} />
                </label>
                <label className="full">
                  Continuity
                  <textarea rows={2} value={draft.continuityNotes || ""} onChange={(e) => update(shot.id, "continuityNotes", e.target.value)} />
                </label>
              </div>
              <div className="actions">
                <button onClick={() => onSaveShot(shot.sceneId, shot.id, draft)}>Save Shot</button>
              </div>
            </div>
          );
        })}
      </div>
    </section>
  );
}
