export default function SceneListPanel({ scenes, selectedSceneId, onSelectScene }) {
  return (
    <section className="card">
      <h2>Scenes</h2>
      {!scenes.length ? <p>No scenes yet.</p> : null}
      <div className="list">
        {scenes.map((scene) => (
          <button
            className={`scene-button ${selectedSceneId === scene.id ? "selected" : ""}`}
            key={scene.id}
            onClick={() => onSelectScene(scene.id)}
          >
            <b>
              {scene.sceneIndex}. {scene.title || "Untitled scene"}
            </b>
            <span>{scene.mood || "-"}</span>
            <small>{scene.shotCount ?? 0} shot(s)</small>
          </button>
        ))}
      </div>
    </section>
  );
}
