import { toAbsoluteApiUrl } from "../api/client";

export default function ShotList({ shots, onUploadStartImage }) {
  if (!shots.length) {
    return <p>No shots available.</p>;
  }
  return (
    <div className="list">
      {shots.map((shot) => (
        <div className="list-item" key={`shot-${shot.index}-${shot.action}`}>
          <h5>
            Shot {shot.index} <span className="muted">({shot.durationSeconds}s)</span>
          </h5>
          <p>
            <b>Action:</b> {shot.action}
          </p>
          {shot.startImageUrl ? (
            <img className="reference-image" src={toAbsoluteApiUrl(shot.startImageUrl)} alt={`Shot ${shot.index} start`} />
          ) : null}
          <p className="muted">Shot start image is used for Image-to-Video.</p>
          {shot.id && shot.sceneId ? (
            <label className="file-control">
              Upload start image / keyframe
              <input
                type="file"
                accept=".png,.jpg,.jpeg,.webp,image/png,image/jpeg,image/webp"
                onChange={(event) => {
                  const file = event.target.files?.[0];
                  if (file) {
                    onUploadStartImage?.(shot.sceneId, shot.id, file);
                    event.target.value = "";
                  }
                }}
              />
            </label>
          ) : null}
          <p>
            <b>Prompt:</b> {shot.wanPrompt || "-"}
          </p>
          <pre className="prompt-block">{shot.wanPrompt || "-"}</pre>
          <p>
            <b>Negative:</b> {shot.negativePrompt || "-"}
          </p>
          <pre className="prompt-block">{shot.negativePrompt || "-"}</pre>
        </div>
      ))}
    </div>
  );
}
