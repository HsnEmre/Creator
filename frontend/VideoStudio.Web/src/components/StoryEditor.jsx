export default function StoryEditor({ storyText, onChange, onSave, isBusy }) {
  return (
    <section className="card">
      <h2>Story Editor</h2>
      <textarea rows={12} value={storyText} onChange={(e) => onChange(e.target.value)} placeholder="Write or paste story..." />
      <div className="actions">
        <button disabled={isBusy} onClick={onSave}>
          {isBusy ? "Saving..." : "Save Story"}
        </button>
      </div>
    </section>
  );
}
