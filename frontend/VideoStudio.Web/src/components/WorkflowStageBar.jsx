const STAGES = ["Story", "Analyze", "Prepare Visuals", "Generate Visuals", "Render", "Assemble", "Finalize"];

export default function WorkflowStageBar({ project, plan, jobs, finalVideo }) {
  const completedJobs = jobs.filter((job) => String(job.status).toLowerCase() === "completed" || job.status === 2);
  const hasVisuals = completedJobs.some((job) => ["GenerateCharacterReferenceImage", "GenerateShotStartImage"].includes(job.jobTypeName));
  const hasRender = completedJobs.some((job) => job.jobTypeName === "RenderVideo");
  const hasAssembly = completedJobs.some((job) => job.jobTypeName === "AssembleVideo") || Boolean(finalVideo?.assembledMediaUrl);
  const hasFinal = completedJobs.some((job) => job.jobTypeName === "MuxAudio") || Boolean(finalVideo?.mediaUrl);

  function isDone(stage) {
    if (stage === "Story") return Boolean(project?.storyText);
    if (stage === "Analyze") return Boolean(plan);
    if (stage === "Prepare Visuals") return Boolean(plan?.characters?.some((c) => c.referenceImagePrompt) || plan?.scenes?.some((s) => s.shots?.some((shot) => shot.startImagePrompt)));
    if (stage === "Generate Visuals") return hasVisuals || Boolean(plan?.characters?.some((c) => c.referenceImageUrl) || plan?.scenes?.some((s) => s.shots?.some((shot) => shot.startImageUrl)));
    if (stage === "Render") return hasRender;
    if (stage === "Assemble") return hasAssembly;
    if (stage === "Finalize") return hasFinal;
    return false;
  }

  return (
    <nav className="stage-bar" aria-label="Workflow stages">
      {STAGES.map((stage, index) => (
        <div className={`stage-pill ${isDone(stage) ? "done" : ""}`} key={stage}>
          <span>{index + 1}</span>
          {stage}
        </div>
      ))}
    </nav>
  );
}
