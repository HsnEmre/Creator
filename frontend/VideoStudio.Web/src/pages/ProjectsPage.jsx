import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { createProject, getProjects } from "../api/client";

export default function ProjectsPage() {
  const navigate = useNavigate();
  const [title, setTitle] = useState("");
  const [storyText, setStoryText] = useState("");
  const [targetDurationSeconds, setTargetDurationSeconds] = useState(60);
  const [qualityGoal, setQualityGoal] = useState("Balanced");
  const [isSaving, setIsSaving] = useState(false);
  const [projects, setProjects] = useState([]);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  useEffect(() => {
    getProjects()
      .then((data) => setProjects(Array.isArray(data) ? data : []))
      .catch(() => setProjects([]));
  }, []);

  async function onCreateProject(e) {
    e.preventDefault();
    setIsSaving(true);
    setError("");
    setMessage("");
    try {
      const project = await createProject({ title, storyText, targetDurationSeconds, qualityGoal });
      setMessage("Project created.");
      navigate(`/projects/${project.id}`);
    } catch (err) {
      setError(err.message || "Project creation failed.");
    } finally {
      setIsSaving(false);
    }
  }

  return (
    <div className="studio-page">
      <div className="header">
        <h1>VideoStudio</h1>
        <p>AI video generation orchestration panel</p>
      </div>
      <div className="layout landing-layout">
      <div className="card">
        <h2>Create Project</h2>
        <form className="form-grid" onSubmit={onCreateProject}>
          <label>
            Title
            <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Test Film" required />
          </label>
          <label>
            Target Duration (seconds)
            <input
              type="number"
              min={10}
              value={targetDurationSeconds}
              onChange={(e) => setTargetDurationSeconds(Number(e.target.value || 60))}
            />
          </label>
          <label>
            Quality Goal
            <select value={qualityGoal} onChange={(e) => setQualityGoal(e.target.value)}>
              <option value="Fast test">Fast test</option>
              <option value="Balanced">Balanced</option>
              <option value="Final quality">Final quality</option>
              <option value="Manual">Manual</option>
            </select>
          </label>
          <label className="full">
            Story Text
            <textarea
              rows={10}
              value={storyText}
              onChange={(e) => setStoryText(e.target.value)}
              placeholder="Write your story here..."
            />
          </label>
          <div className="actions full">
            <button disabled={isSaving} type="submit">
              {isSaving ? "Creating..." : "Create Project"}
            </button>
          </div>
        </form>
        {message ? <p className="msg ok">{message}</p> : null}
        {error ? <p className="msg error">{error}</p> : null}
      </div>
      <div className="card">
        <h2>Projects</h2>
        {!projects.length ? <p>No saved projects yet.</p> : null}
        <div className="list">
          {projects.map((project) => (
            <button className="scene-button" key={project.id} onClick={() => navigate(`/projects/${project.id}`)}>
              <b>{project.title}</b>
              <span>{project.status}</span>
              <small>{project.updatedAt}</small>
            </button>
          ))}
        </div>
      </div>
      </div>
    </div>
  );
}
