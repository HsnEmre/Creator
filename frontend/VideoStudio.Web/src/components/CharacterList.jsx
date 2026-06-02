import { toAbsoluteApiUrl } from "../api/client";

export default function CharacterList({ characters, onUploadReference, showReferenceTools = true }) {
  return (
    <section className="subcard">
      <h3>Characters</h3>
      <p className="muted">
        Character reference guides identity in prompts. It is not used directly as the Wan2.2 start frame.
      </p>
      {characters.length ? (
        <div className="character-grid">
          {characters.map((c, i) => (
            <div className="list-item character-card" key={`${c.name}-${i}`}>
              <h4>
                {c.name} <span className="muted">({c.role})</span>
              </h4>
              <span className="badge">{c.referenceStatus || "Missing"}</span>
              {c.referenceImageUrl ? (
                <img className="reference-image" src={toAbsoluteApiUrl(c.referenceImageUrl)} alt={`${c.name} reference`} />
              ) : null}
              <p>{c.personality}</p>
              <p>
                <b>Visual Prompt:</b> {c.visualPrompt}
              </p>
              {c.referenceImagePrompt ? (
                <p>
                  <b>Reference Prompt:</b> {c.referenceImagePrompt}
                </p>
              ) : null}
              <p>
                <b>Voice:</b> {c.voiceStyle || "-"}
              </p>
              {showReferenceTools && c.id ? (
                <label className="file-control">
                  Reference Image
                  <input
                    type="file"
                    accept=".png,.jpg,.jpeg,.webp,image/png,image/jpeg,image/webp"
                    onChange={(event) => {
                      const file = event.target.files?.[0];
                      if (file) {
                        onUploadReference?.(c.id, file);
                        event.target.value = "";
                      }
                    }}
                  />
                </label>
              ) : null}
            </div>
          ))}
        </div>
      ) : (
        <p>No characters available.</p>
      )}
    </section>
  );
}
