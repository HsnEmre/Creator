import { toAbsoluteApiUrl } from "../api/client";

export default function VideoPreviewPanel({ finalMediaUrl, assembledMediaUrl, renderMediaUrl, outputPath, assembledPath }) {
  const previewUrl = finalMediaUrl || assembledMediaUrl || renderMediaUrl || null;

  return (
    <section className="card">
      <h2>Output Preview</h2>
      {previewUrl ? (
        <>
          <video className="preview-player" controls src={toAbsoluteApiUrl(previewUrl)} />
          <p>
            <a href={toAbsoluteApiUrl(previewUrl)} target="_blank" rel="noreferrer">
              Open media URL
            </a>
          </p>
        </>
      ) : null}
      <p className="path">{outputPath || "No completed output yet."}</p>
      {assembledPath ? <p className="path">Assembled: {assembledPath}</p> : null}
      {!previewUrl ? <p className="muted">Preview playback will be enabled after the backend exposes generated files over HTTP.</p> : null}
    </section>
  );
}
