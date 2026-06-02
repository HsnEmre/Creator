import { toAbsoluteApiUrl } from "../api/client";
import VisualPreparationPanel from "./VisualPreparationPanel";

function getReferenceStatus(character) {
  if (character.referenceImageUrl || character.referenceImagePath) return "Reference ready";
  if (character.referenceImagePrompt || character.visualPrompt) return "Prompt ready";
  return "Needs reference";
}

function getStatusClass(status) {
  if (status === "Reference ready") return "badge-generated";
  if (status === "Prompt ready") return "badge-ready";
  return "badge-pending";
}

function shortText(value, maxLength = 180) {
  if (!value) return "";
  const text = String(value).replace(/\s+/g, " ").trim();
  if (text.length <= maxLength) return text;
  return `${text.slice(0, maxLength - 1).trim()}...`;
}

async function copyText(value) {
  if (!value || !navigator.clipboard) return;
  try {
    await navigator.clipboard.writeText(value);
  } catch {
    // Clipboard access can be unavailable in some local browser contexts.
  }
}

function CharacterReferenceCard({ character, isBusy, onUploadReference, onGenerateCharacterReference }) {
  const status = getReferenceStatus(character);
  const prompt = character.referenceImagePrompt || character.visualPrompt || "";
  const referenceUrl = character.referenceImageUrl ? toAbsoluteApiUrl(character.referenceImageUrl) : null;

  return (
    <article className="cast-character-card">
      <div className="cast-reference-frame">
        {referenceUrl ? (
          <img src={referenceUrl} alt={`${character.name} reference`} />
        ) : (
          <div className="cast-reference-empty">
            <span>No reference image</span>
          </div>
        )}
      </div>

      <div className="cast-character-body">
        <div className="row">
          <div>
            <h3>{character.name || "Unnamed Character"}</h3>
            <p className="muted">{character.role || "Character"}</p>
          </div>
          <span className={`badge ${getStatusClass(status)}`}>{status}</span>
        </div>

        <p>{shortText(character.personality || character.visualPrompt || "Character details will appear after analysis.")}</p>

        {character.visualPrompt ? (
          <div className="cast-visual-lock">
            <b>Visual lock</b>
            <span>{shortText(character.visualPrompt, 220)}</span>
          </div>
        ) : null}

        {character.voiceStyle ? (
          <p className="muted">
            <b>Voice:</b> {shortText(character.voiceStyle, 120)}
          </p>
        ) : null}
      </div>

      <div className="cast-card-actions">
        <button type="button" disabled={isBusy || !character.id} onClick={() => onGenerateCharacterReference?.(character.id)}>
          Generate Reference
        </button>
        <button type="button" disabled={!prompt} onClick={() => copyText(prompt)}>
          Copy Prompt
        </button>
        <label className="file-control inline-file cast-upload-button">
          Upload Image
          <input
            type="file"
            accept=".png,.jpg,.jpeg,.webp,image/png,image/jpeg,image/webp"
            disabled={isBusy || !character.id}
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
    </article>
  );
}

export default function CastStep({
  plan,
  isBusy,
  busyAction,
  onPrepareVisuals,
  onGenerateCharacterReferences,
  onGenerateCharacterReference,
  onUploadReference,
  onSaveCharacterPrompt,
  onNext
}) {
  const characters = plan?.characters || [];
  const referenceReadyCount = characters.filter((character) => character.referenceImageUrl || character.referenceImagePath).length;
  const missingReferenceCount = Math.max(0, characters.length - referenceReadyCount);
  const hasCharacters = characters.length > 0;

  return (
    <div className="creator-step-panel cast-step">
      <section className="cast-hero">
        <div>
          <span className="badge badge-ready">Cast</span>
          <h2>Characters detected from your story</h2>
          <p>
            Review the cast and create reference images that help keep identity, clothing, and personality consistent in generated prompts.
          </p>
        </div>
        <div className="cast-hero-stats">
          <span>
            <b>{characters.length}</b>
            Characters
          </span>
          <span>
            <b>{referenceReadyCount}</b>
            References ready
          </span>
          <span>
            <b>{missingReferenceCount}</b>
            Missing
          </span>
        </div>
      </section>

      <section className="cast-actions-card">
        <div>
          <h3>Character References</h3>
          <p className="muted">Generate references after visual prompts are prepared, or upload your own image for each character.</p>
        </div>
        <div className="actions">
          <button type="button" disabled={isBusy || !plan} onClick={onPrepareVisuals}>
            {busyAction === "prepare-visuals" ? "Preparing..." : "Prepare Visual Prompts"}
          </button>
          <button type="button" disabled={isBusy || !plan} onClick={onGenerateCharacterReferences}>
            {busyAction === "generate-character-references" ? "Queueing..." : "Generate Character References"}
          </button>
          <button type="button" disabled={!hasCharacters} onClick={onNext}>
            Next: Storyboard
          </button>
        </div>
        {hasCharacters && missingReferenceCount ? (
          <p className="msg compact-msg cast-reference-warning">
            {missingReferenceCount} character(s) still need references. You can continue to Storyboard, but prompt-only identity may drift.
          </p>
        ) : null}
      </section>

      {!hasCharacters ? (
        <section className="cast-empty-state">
          <h3>No characters yet</h3>
          <p>Analyze your story in the Content step to detect characters and prepare reference prompts.</p>
        </section>
      ) : (
        <section className="cast-character-grid" aria-label="Detected characters">
          {characters.map((character, index) => (
            <CharacterReferenceCard
              character={character}
              isBusy={isBusy}
              key={character.id || `${character.name}-${index}`}
              onGenerateCharacterReference={onGenerateCharacterReference}
              onUploadReference={onUploadReference}
            />
          ))}
        </section>
      )}

      <section className="cast-explainer">
        <div>
          <h3>How references are used</h3>
          <p>Character references guide identity in prompts, including face, hair, clothing, and continuity details.</p>
        </div>
        <div>
          <h3>What they are not</h3>
          <p>Shot start images and keyframes are what Wan2.2 animates through Image-to-Video. Character portraits are not passed as Wan2.2 start frames.</p>
        </div>
      </section>

      {hasCharacters ? (
        <details className="cast-advanced-panel">
          <summary>Advanced reference prompts</summary>
          <VisualPreparationPanel
            mode="characters"
            plan={plan}
            onSaveCharacterPrompt={onSaveCharacterPrompt}
            onUploadReference={onUploadReference}
            onGenerateCharacterReference={onGenerateCharacterReference}
          />
        </details>
      ) : null}
    </div>
  );
}
