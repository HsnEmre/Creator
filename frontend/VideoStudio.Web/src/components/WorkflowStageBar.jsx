const STAGES = [
  "Story",
  "Analyze",
  "Production Plan",
  "Characters",
  "Character References",
  "Keyframes",
  "Render",
  "Assemble",
  "Audio",
  "Finalize"
];

export default function WorkflowStageBar({ project, plan, jobs, finalVideo, dialogueLines }) {
  const completedJobs = jobs.filter((job) => String(job.status).toLowerCase() === "completed" || job.status === 2);
  const hasCharacterReferences = completedJobs.some((job) => job.jobTypeName === "GenerateCharacterReferenceImage") ||
    Boolean(plan?.characters?.some((character) => character.referenceImageUrl || character.referenceImagePath));
  const hasKeyframes = completedJobs.some((job) => job.jobTypeName === "GenerateShotStartImage") ||
    Boolean(plan?.scenes?.some((scene) => scene.shots?.some((shot) => shot.startImageUrl || shot.startImagePath)));
  const hasRender = completedJobs.some((job) => job.jobTypeName === "RenderVideo");
  const hasAssembly = completedJobs.some((job) => job.jobTypeName === "AssembleVideo") || Boolean(finalVideo?.assembledMediaUrl);
  const hasAudio = completedJobs.some((job) => job.jobTypeName === "GenerateAudio") ||
    Boolean(dialogueLines?.some((line) => line.audioUrl || line.audioPath));
  const hasFinal = completedJobs.some((job) => job.jobTypeName === "MuxAudio") || Boolean(finalVideo?.mediaUrl);

  function isDone(stage) {
    if (stage === "Story") return Boolean(project?.storyText);
    if (stage === "Analyze") return Boolean(plan);
    if (stage === "Production Plan") return Boolean(plan?.scenes?.length);
    if (stage === "Characters") return Boolean(plan?.characters?.length);
    if (stage === "Character References") return hasCharacterReferences;
    if (stage === "Keyframes") return hasKeyframes;
    if (stage === "Render") return hasRender;
    if (stage === "Assemble") return hasAssembly;
    if (stage === "Audio") return hasAudio;
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
