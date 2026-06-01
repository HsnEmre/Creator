import CharacterList from "./CharacterList";
import ShotList from "./ShotList";

export default function ProductionPlanViewer({ plan, onUploadReference, onUploadStartImage }) {
  if (!plan) {
    return (
      <section className="card">
        <h2>Production Plan</h2>
        <p>No production plan yet. Save a story and click Analyze.</p>
      </section>
    );
  }

  return (
    <section className="card">
      <h2>Production Plan</h2>
      <div className="meta-grid">
        <div>
          <b>Title:</b> {plan.title}
        </div>
        <div>
          <b>Genre:</b> {plan.genre || "-"}
        </div>
        <div className="full">
          <b>Logline:</b> {plan.logline || "-"}
        </div>
      </div>

      <section className="subcard">
        <h3>Visual Style</h3>
        <p>{plan.visualStyle?.stylePrompt || "-"}</p>
        <p>
          <b>Negative:</b> {plan.visualStyle?.negativePrompt || "-"}
        </p>
      </section>

      <CharacterList characters={plan.characters || []} onUploadReference={onUploadReference} />

      <section className="subcard">
        <h3>Scenes</h3>
        {plan.scenes?.length ? (
          plan.scenes.map((scene) => (
            <details className="scene" key={`scene-${scene.index}`} open={scene.index === 1}>
              <summary>
                Scene {scene.index}: {scene.title}
              </summary>
              <p>{scene.summary}</p>
              <p>
                <b>Location:</b> {scene.location} | <b>Mood:</b> {scene.mood}
              </p>
              <ShotList shots={scene.shots || []} onUploadStartImage={onUploadStartImage} />
            </details>
          ))
        ) : (
          <p>No scenes available.</p>
        )}
      </section>
    </section>
  );
}
