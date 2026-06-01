import { useEffect, useState } from "react";

export default function SceneEditorPanel({ scene, onSave }) {
  const [draft, setDraft] = useState({});

  useEffect(() => {
    setDraft(scene || {});
  }, [scene]);

  if (!scene) {
    return (
      <section className="card">
        <h2>Scene Editor</h2>
        <p>Select a scene to edit.</p>
      </section>
    );
  }

  function update(field, value) {
    setDraft((current) => ({ ...current, [field]: value }));
  }

  return (
    <section className="card">
      <h2>Scene Editor</h2>
      <div className="form-grid">
        <label>
          Index
          <input type="number" value={draft.sceneIndex || 1} onChange={(e) => update("sceneIndex", Number(e.target.value))} />
        </label>
        <label>
          Duration
          <input
            type="number"
            value={draft.targetDurationSeconds || 0}
            onChange={(e) => update("targetDurationSeconds", Number(e.target.value))}
          />
        </label>
        <label className="full">
          Title
          <input value={draft.title || ""} onChange={(e) => update("title", e.target.value)} />
        </label>
        <label className="full">
          Summary
          <textarea rows={4} value={draft.summary || ""} onChange={(e) => update("summary", e.target.value)} />
        </label>
        <label>
          Location
          <input value={draft.location || ""} onChange={(e) => update("location", e.target.value)} />
        </label>
        <label>
          Mood
          <input value={draft.mood || ""} onChange={(e) => update("mood", e.target.value)} />
        </label>
      </div>
      <div className="actions">
        <button onClick={() => onSave(scene.id, draft)}>Save Scene</button>
      </div>
    </section>
  );
}
