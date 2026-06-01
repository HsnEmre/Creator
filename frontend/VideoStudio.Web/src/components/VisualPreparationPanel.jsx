import { useEffect, useMemo, useState } from "react";
import { toAbsoluteApiUrl } from "../api/client";

export default function VisualPreparationPanel({
  plan,
  selectedScene,
  selectedShotIds,
  onSaveCharacterPrompt,
  onSaveShotPrompt,
  onUploadReference,
  onUploadStartImage,
  onGenerateCharacterReference,
  onGenerateShotStartImage,
  useShotStartImage,
  hasAnyShotStartImage
}) {
  const [characterDrafts, setCharacterDrafts] = useState({});
  const [shotDrafts, setShotDrafts] = useState({});

  useEffect(() => {
    const next = {};
    for (const character of plan?.characters || []) {
      if (character.id) {
        next[character.id] = {
          referenceImagePrompt: character.referenceImagePrompt || "",
          referenceImageNegativePrompt: character.referenceImageNegativePrompt || ""
        };
      }
    }
    setCharacterDrafts(next);
  }, [plan]);

  useEffect(() => {
    const next = {};
    for (const scene of plan?.scenes || []) {
      for (const shot of scene.shots || []) {
        if (shot.id) {
          next[shot.id] = {
            sceneId: shot.sceneId || scene.id,
            startImagePrompt: shot.startImagePrompt || "",
            startImageNegativePrompt: shot.startImageNegativePrompt || ""
          };
        }
      }
    }
    setShotDrafts(next);
  }, [plan]);

  const visibleShots = useMemo(() => {
    const all = (plan?.scenes || []).flatMap((scene) =>
      (scene.shots || []).map((shot) => ({ ...shot, sceneTitle: scene.title, sceneIndex: scene.index, sceneId: shot.sceneId || scene.id }))
    );
    if (selectedShotIds?.length) {
      return all.filter((shot) => selectedShotIds.includes(shot.id));
    }
    if (selectedScene?.id) {
      return all.filter((shot) => shot.sceneId === selectedScene.id);
    }
    return all.slice(0, 3);
  }, [plan, selectedScene, selectedShotIds]);

  if (!plan) {
    return (
      <section className="card">
        <h2>Visual Preparation</h2>
        <p>No production plan yet. Save a story and click Analyze.</p>
      </section>
    );
  }

  function updateCharacter(characterId, field, value) {
    setCharacterDrafts((current) => ({ ...current, [characterId]: { ...(current[characterId] || {}), [field]: value } }));
  }

  function updateShot(shotId, field, value) {
    setShotDrafts((current) => ({ ...current, [shotId]: { ...(current[shotId] || {}), [field]: value } }));
  }

  return (
    <section className="card">
      <div className="row">
        <h2>Visual Preparation</h2>
        <span className="badge">Local SDXL</span>
      </div>
      <p className="muted">
        Prepare reference and keyframe prompts before rendering. Image generation runs locally in the Python worker; manual uploads stay available.
      </p>
      {hasAnyShotStartImage ? (
        useShotStartImage ? (
          <p className="msg ok">
            Shot start images are available. Image-to-Video is enabled, so generated keyframes will be sent to Wan2.2 as start frames.
          </p>
        ) : (
          <p className="msg error">
            Shot start images are available. Enable Image-to-Video to use them as Wan2.2 start frames.
          </p>
        )
      ) : (
        <p className="muted">
          No shot start images are available yet. Rendering will stay Text-to-Video until a keyframe is generated or uploaded.
        </p>
      )}

      <section className="subcard">
        <h3>Character References</h3>
        <div className="list">
          {(plan.characters || []).map((character) => {
            const draft = characterDrafts[character.id] || {};
            return (
              <div className="list-item visual-card" key={character.id || character.name}>
                <div className="row">
                  <h4>{character.name}</h4>
                  <span className="badge">{character.jobStatus || character.referenceStatus || "Missing"}</span>
                </div>
                <p className="muted">{character.role}</p>
                {character.referenceImageUrl ? (
                  <img className="reference-image" src={toAbsoluteApiUrl(character.referenceImageUrl)} alt={`${character.name} reference`} />
                ) : null}
                <label className="full">
                  Reference image prompt
                  <textarea rows={5} value={draft.referenceImagePrompt || ""} onChange={(event) => updateCharacter(character.id, "referenceImagePrompt", event.target.value)} />
                </label>
                <label className="full">
                  Negative prompt
                  <textarea rows={3} value={draft.referenceImageNegativePrompt || ""} onChange={(event) => updateCharacter(character.id, "referenceImageNegativePrompt", event.target.value)} />
                </label>
                <div className="actions">
                  <button onClick={() => onSaveCharacterPrompt(character.id, draft)}>Save Prompt</button>
                  <button onClick={() => onGenerateCharacterReference?.(character.id)}>Generate Image</button>
                  <label className="file-control inline-file">
                    Upload Manually
                    <input
                      type="file"
                      accept=".png,.jpg,.jpeg,.webp,image/png,image/jpeg,image/webp"
                      onChange={(event) => {
                        const file = event.target.files?.[0];
                        if (file) {
                          onUploadReference?.(character.id, file);
                          event.target.value = "";
                        }
                      }}
                    />
                  </label>
                </div>
              </div>
            );
          })}
        </div>
      </section>

      <section className="subcard">
        <h3>Shot Start Images</h3>
        {!visibleShots.length ? <p>No shots selected.</p> : null}
        <div className="list">
          {visibleShots.map((shot) => {
            const draft = shotDrafts[shot.id] || {};
            return (
              <div className="list-item visual-card" key={shot.id}>
                <div className="row">
                  <h4>Scene {shot.sceneIndex} / Shot {shot.index}</h4>
                  <span className="badge">{shot.jobStatus || shot.startImageStatus || "Missing"}</span>
                </div>
                <p className="muted">{shot.sceneTitle}</p>
                {shot.startImageUrl ? (
                  <img className="reference-image" src={toAbsoluteApiUrl(shot.startImageUrl)} alt={`Shot ${shot.index} start`} />
                ) : null}
                <label>
                  Start image prompt
                  <textarea rows={5} value={draft.startImagePrompt || ""} onChange={(event) => updateShot(shot.id, "startImagePrompt", event.target.value)} />
                </label>
                <label>
                  Negative prompt
                  <textarea rows={3} value={draft.startImageNegativePrompt || ""} onChange={(event) => updateShot(shot.id, "startImageNegativePrompt", event.target.value)} />
                </label>
                <div className="actions">
                  <button onClick={() => onSaveShotPrompt(draft.sceneId || shot.sceneId, shot.id, draft)}>Save Prompt</button>
                  <button onClick={() => onGenerateShotStartImage?.(shot.id)}>Generate Image</button>
                  <label className="file-control inline-file">
                    Upload Manually
                    <input
                      type="file"
                      accept=".png,.jpg,.jpeg,.webp,image/png,image/jpeg,image/webp"
                      onChange={(event) => {
                        const file = event.target.files?.[0];
                        if (file) {
                          onUploadStartImage?.(draft.sceneId || shot.sceneId, shot.id, file);
                          event.target.value = "";
                        }
                      }}
                    />
                  </label>
                </div>
              </div>
            );
          })}
        </div>
      </section>
    </section>
  );
}
