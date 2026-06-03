import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import {
  analyzeProject,
  assembleProject,
  cancelProjectActiveJobs,
  cleanupProjectJobs,
  finalizeProject,
  generateCharacterReferences,
  generateShotStartImages,
  getFinalVideo,
  generateAudio,
  getDialogueLines,
  getProductionPlan,
  getPreproduction,
  getProject,
  getRenderJobs,
  getScenes,
  getShots,
  refinalizeProject,
  renderProject,
  preparePreproduction,
  resetStaleJobs,
  saveStory,
  updateCharacterReferencePrompt,
  updateScene,
  updateShot,
  updateShotStartImagePrompt,
  uploadCharacterReferenceImage,
  uploadShotStartImage
} from "../api/client";
import ContentStep from "../components/ContentStep";
import CastStep from "../components/CastStep";
import StoryboardStep from "../components/StoryboardStep";
import EditStep from "../components/EditStep";
import RenderJobsPanel from "../components/RenderJobsPanel";
import VideoPreviewPanel from "../components/VideoPreviewPanel";
import CreatorShell from "../components/CreatorShell";

const VISUAL_JOB_TYPES = new Set(["GenerateCharacterReferenceImage", "GenerateShotStartImage"]);

function isCompletedStatus(status) {
  return status === 2 || String(status).toLowerCase() === "completed";
}

function flattenStoryboardShots(plan) {
  return (plan?.scenes || []).flatMap((scene) =>
    (scene.shots || []).map((shot) => ({
      ...shot,
      sceneId: shot.sceneId || scene.id,
      sceneIndex: scene.index ?? scene.sceneIndex,
      shotIndex: shot.index ?? shot.shotIndex,
      index: shot.index ?? shot.shotIndex
    }))
  );
}

function hasShotStartImage(shot) {
  return Boolean(shot?.startImagePath || shot?.startImageUrl);
}

export default function ProjectDetailPage() {
  const { projectId } = useParams();
  const navigate = useNavigate();

  const [project, setProject] = useState(null);
  const [plan, setPlan] = useState(null);
  const [jobs, setJobs] = useState([]);
  const [dialogueLines, setDialogueLines] = useState([]);
  const [finalVideo, setFinalVideo] = useState(null);
  const [preproduction, setPreproduction] = useState(null);
  const [scenes, setScenes] = useState([]);
  const [shots, setShots] = useState([]);
  const [selectedSceneId, setSelectedSceneId] = useState(null);
  const [selectedShotIds, setSelectedShotIds] = useState([]);
  const [storyText, setStoryText] = useState("");
  const [busyAction, setBusyAction] = useState("");
  const [useCharacterReferenceInPrompt, setUseCharacterReferenceInPrompt] = useState(true);
  const [useShotStartImage, setUseShotStartImage] = useState(false);
  const [hasUserSetShotStartImageMode, setHasUserSetShotStartImageMode] = useState(false);
  const [selectedStep, setSelectedStep] = useState("content");
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const visualJobStatusRef = useRef(new Map());

  const latestCompletedRender = useMemo(() => {
    const completed = jobs.find(
      (j) =>
        j.jobTypeName === "RenderVideo" &&
        (j.status === 2 || String(j.status).toLowerCase() === "completed")
    );
    return completed || null;
  }, [jobs]);
  const hasRunningRenderVideo = useMemo(
    () =>
      jobs.some(
        (j) =>
          j.jobTypeName === "RenderVideo" &&
          (j.status === 0 || j.status === 1 || ["pending", "rendering"].includes(String(j.status).toLowerCase()))
      ),
    [jobs]
  );
  const hasRunningAudio = useMemo(
    () =>
      jobs.some(
        (j) =>
          j.jobTypeName === "GenerateAudio" &&
          (j.status === 0 || j.status === 1 || ["pending", "rendering"].includes(String(j.status).toLowerCase()))
      ),
    [jobs]
  );
  const hasRunningFinalize = useMemo(
    () =>
      jobs.some(
        (j) =>
          j.jobTypeName === "MuxAudio" &&
          (j.status === 0 || j.status === 1 || ["pending", "rendering"].includes(String(j.status).toLowerCase()))
      ),
    [jobs]
  );
  const visualPlan = useMemo(() => {
    if (!preproduction || !plan) return plan;
    const characters = (plan.characters || []).map((character) => {
      const prepared = preproduction.characters?.find((item) => item.id === character.id);
      return prepared ? { ...character, ...prepared } : character;
    });
    const scenes = (plan.scenes || []).map((scene) => ({
      ...scene,
      shots: (scene.shots || []).map((shot) => {
        const prepared = preproduction.shots?.find((item) => item.id === shot.id);
        return prepared ? { ...shot, ...prepared, index: shot.index } : shot;
      })
    }));
    return { ...plan, characters, scenes };
  }, [plan, preproduction]);
  const hasAnyShotStartImage = useMemo(
    () => Boolean(visualPlan?.scenes?.some((scene) => scene.shots?.some((shot) => Boolean(shot.startImageUrl || shot.startImagePath)))),
    [visualPlan]
  );
  const storyboardShots = useMemo(() => flattenStoryboardShots(visualPlan), [visualPlan]);
  const selectedScene = useMemo(() => scenes.find((scene) => scene.id === selectedSceneId) || scenes[0] || null, [scenes, selectedSceneId]);
  const selectedSceneShots = useMemo(
    () => shots.filter((shot) => selectedScene && shot.sceneId === selectedScene.id).sort((a, b) => (a.shotIndex || 0) - (b.shotIndex || 0)),
    [shots, selectedScene]
  );

  const loadProject = useCallback(async () => {
    const data = await getProject(projectId);
    setProject(data);
    setStoryText(data.storyText || "");
  }, [projectId]);

  const refreshProjectSummary = useCallback(async () => {
    const data = await getProject(projectId);
    setProject(data);
  }, [projectId]);

  const loadPlan = useCallback(async () => {
    const data = await getProductionPlan(projectId);
    setPlan(data);
  }, [projectId]);

  const loadPreproduction = useCallback(async () => {
    const data = await getPreproduction(projectId);
    setPreproduction(data);
  }, [projectId]);

  const loadJobs = useCallback(async () => {
    const data = await getRenderJobs(projectId);
    const nextJobs = Array.isArray(data) ? data : [];
    let visualJobCompleted = false;
    const nextVisualStatuses = new Map();
    for (const job of nextJobs) {
      if (!VISUAL_JOB_TYPES.has(job.jobTypeName)) {
        continue;
      }

      const id = job.jobId || job.id;
      if (!id) {
        continue;
      }

      const currentStatus = job.status;
      const previousStatus = visualJobStatusRef.current.get(id);
      if (isCompletedStatus(currentStatus) && !isCompletedStatus(previousStatus)) {
        visualJobCompleted = true;
      }
      nextVisualStatuses.set(id, currentStatus);
    }
    visualJobStatusRef.current = nextVisualStatuses;
    setJobs(nextJobs);
    return { jobs: nextJobs, visualJobCompleted };
  }, [projectId]);

  const loadDialogueLines = useCallback(async () => {
    const data = await getDialogueLines(projectId);
    setDialogueLines(Array.isArray(data) ? data : []);
  }, [projectId]);

  const loadScenesAndShots = useCallback(async () => {
    const [sceneData, shotData] = await Promise.all([getScenes(projectId), getShots(projectId)]);
    const nextScenes = Array.isArray(sceneData) ? sceneData : [];
    setScenes(nextScenes);
    setShots(Array.isArray(shotData) ? shotData : []);
    setSelectedSceneId((current) => current || nextScenes[0]?.id || null);
  }, [projectId]);

  const loadFinalVideo = useCallback(async () => {
    const data = await getFinalVideo(projectId);
    setFinalVideo(data);
  }, [projectId]);

  const refreshAll = useCallback(async () => {
    try {
      await Promise.all([loadProject(), loadPlan(), loadPreproduction(), loadJobs(), loadDialogueLines(), loadFinalVideo(), loadScenesAndShots()]);
    } catch (err) {
      setError(err.message || "Failed to refresh project.");
    }
  }, [loadProject, loadPlan, loadPreproduction, loadJobs, loadDialogueLines, loadFinalVideo, loadScenesAndShots]);

  useEffect(() => {
    refreshAll();
  }, [refreshAll]);

  useEffect(() => {
    setHasUserSetShotStartImageMode(false);
    setUseShotStartImage(false);
    visualJobStatusRef.current = new Map();
  }, [projectId]);

  useEffect(() => {
    if (hasAnyShotStartImage && !hasUserSetShotStartImageMode) {
      setUseShotStartImage(true);
    }
  }, [hasAnyShotStartImage, hasUserSetShotStartImageMode]);

  useEffect(() => {
    const timer = setInterval(() => {
      loadJobs()
        .then((result) => {
          if (result?.visualJobCompleted) {
            Promise.all([refreshProjectSummary(), loadPlan(), loadPreproduction(), loadScenesAndShots()]).catch(() => {});
          }
        })
        .catch(() => {});
      loadFinalVideo().catch(() => {});
    }, 5000);
    return () => clearInterval(timer);
  }, [loadJobs, loadFinalVideo, refreshProjectSummary, loadPlan, loadPreproduction, loadScenesAndShots]);

  async function withAction(name, fn) {
    setBusyAction(name);
    setMessage("");
    setError("");
    try {
      await fn();
    } catch (err) {
      setError(err.message || "Request failed.");
    } finally {
      setBusyAction("");
    }
  }

  function onSaveStory() {
    return withAction("save-story", async () => {
      const updated = await saveStory(projectId, storyText);
      setProject((p) => ({ ...p, ...updated }));
      setMessage("Story saved.");
    });
  }

  function onAnalyze() {
    return withAction("analyze", async () => {
      await analyzeProject(projectId);
      await Promise.all([loadProject(), loadPlan(), loadPreproduction(), loadDialogueLines()]);
      setMessage("Analyze completed.");
    });
  }

  function onRenderFastPreview() {
    return withAction("render", async () => {
      // 0 = FastPreview, 1 = Preview, 2 = Final
      const result = await renderProject(projectId, {
        preset: 0,
        maxShots: 1,
        force: true,
        useCharacterReferenceInPrompt,
        useShotStartImage
      });
      await loadJobs();
      const targetShots = selectedShotIds.length ? selectedShotIds : [];
      const selectedMissing = targetShots.length
        ? shots.filter((shot) => targetShots.includes(shot.id) && !shot.startImageUrl && !shot.startImagePath).length
        : 0;
      const warning = useShotStartImage && (!hasAnyShotStartImage || selectedMissing > 0)
        ? ` ${selectedMissing || "Some"} selected shot(s) need start images; those renders will use Text-to-Video.`
        : "";
      setMessage(`Queued ${result?.queuedJobs ?? 0} render job(s).${warning}`);
    });
  }

  function onUseShotStartImageChange(value) {
    setHasUserSetShotStartImageMode(true);
    setUseShotStartImage(value);
  }

  function renderWithPayload(payload, label) {
    return withAction("render", async () => {
      const requestPayload = {
        // 0 = FastPreview, 1 = Preview, 2 = Final
        preset: 0,
        maxShots: payload?.shotIds?.length || payload?.maxShots || 1,
        force: true,
        useCharacterReferenceInPrompt,
        useShotStartImage,
        ...payload
      };
      if (import.meta.env.DEV) {
        console.info("[VideoStudio] render request", {
          label,
          preset: requestPayload.preset,
          maxShots: requestPayload.maxShots,
          shotIds: requestPayload.shotIds || [],
          useShotStartImage: requestPayload.useShotStartImage,
          useCharacterReferenceInPrompt: requestPayload.useCharacterReferenceInPrompt
        });
      }
      const result = await renderProject(projectId, requestPayload);
      await loadJobs();
      const warning = requestPayload.useShotStartImage && !hasAnyShotStartImage ? " No shot start image found. Render will use Text-to-Video." : "";
      setMessage(`${label}: queued ${result?.queuedJobs ?? 0} render job(s).${warning}`);
    });
  }

  function onRenderSelected() {
    return renderWithPayload({ shotIds: selectedShotIds }, "Selected shots");
  }

  function onRenderScene() {
    return renderWithPayload({ sceneIndex: selectedScene?.sceneIndex }, "Selected scene");
  }

  function onRenderAll() {
    return renderWithPayload({ maxShots: 9999 }, "All shots");
  }

  function onSelectStoryboardShot(shotId) {
    setSelectedShotIds(shotId ? [shotId] : []);
  }

  function onRenderStoryboardSelected(shot) {
    if (!shot?.id) {
      setError("Select a shot before animating.");
      return undefined;
    }
    const selectedHasKeyframe = hasShotStartImage(shot);
    const effectiveUseShotStartImage = selectedHasKeyframe || useShotStartImage;
    if (selectedHasKeyframe && !useShotStartImage) {
      setUseShotStartImage(true);
    }
    setSelectedShotIds([shot.id]);
    return renderWithPayload(
      {
        shotIds: [shot.id],
        maxShots: 1,
        useShotStartImage: effectiveUseShotStartImage
      },
      "Selected shot"
    );
  }

  function onRenderStoryboardAll() {
    const shotIds = storyboardShots.map((shot) => shot.id).filter(Boolean);
    if (!shotIds.length) {
      setError("No storyboard shots are available to animate.");
      return undefined;
    }
    const shotsWithKeyframes = storyboardShots.filter(hasShotStartImage).length;
    const effectiveUseShotStartImage = shotsWithKeyframes > 0 || useShotStartImage;
    if (hasAnyShotStartImage && !useShotStartImage) {
      setUseShotStartImage(true);
    }
    return renderWithPayload(
      {
        shotIds,
        maxShots: shotIds.length,
        force: false,
        useShotStartImage: effectiveUseShotStartImage
      },
      "All shots"
    );
  }

  function onUploadReference(characterId, file) {
    return withAction("upload-reference", async () => {
      await uploadCharacterReferenceImage(projectId, characterId, file);
      await Promise.all([loadPlan(), loadPreproduction(), loadJobs()]);
      setMessage("Character reference image uploaded.");
    });
  }

  function onUploadStartImage(sceneId, shotId, file) {
    return withAction("upload-start-image", async () => {
      await uploadShotStartImage(projectId, sceneId, shotId, file);
      await Promise.all([loadPlan(), loadPreproduction(), loadScenesAndShots()]);
      setMessage("Shot start image uploaded.");
    });
  }

  function onSaveScene(sceneId, draft) {
    return withAction("save-scene", async () => {
      await updateScene(projectId, sceneId, draft);
      await Promise.all([loadPlan(), loadPreproduction(), loadScenesAndShots()]);
      setSelectedSceneId(sceneId);
      setMessage("Scene saved.");
    });
  }

  function onSaveShot(sceneId, shotId, draft) {
    return withAction("save-shot", async () => {
      await updateShot(projectId, sceneId, shotId, draft);
      await Promise.all([loadPlan(), loadScenesAndShots()]);
      setSelectedSceneId(sceneId);
      setMessage("Shot saved.");
    });
  }

  function onPrepareVisuals() {
    return withAction("prepare-visuals", async () => {
      const result = await preparePreproduction(projectId);
      await Promise.all([loadPlan(), loadScenesAndShots()]);
      const warnings = result?.warnings?.length ? ` ${result.warnings.length} visual item(s) still need images.` : "";
      setMessage(`Visual prompts prepared.${warnings}`);
    });
  }

  function onSaveCharacterPrompt(characterId, draft) {
    return withAction("save-character-prompt", async () => {
      await updateCharacterReferencePrompt(projectId, characterId, {
        referenceImagePrompt: draft.referenceImagePrompt,
        referenceImageNegativePrompt: draft.referenceImageNegativePrompt
      });
      await Promise.all([loadPlan(), loadPreproduction()]);
      setMessage("Character reference prompt saved.");
    });
  }

  function onSaveShotPrompt(sceneId, shotId, draft) {
    return withAction("save-shot-start-prompt", async () => {
      await updateShotStartImagePrompt(projectId, sceneId, shotId, {
        startImagePrompt: draft.startImagePrompt,
        startImageNegativePrompt: draft.startImageNegativePrompt
      });
      await Promise.all([loadPlan(), loadPreproduction(), loadScenesAndShots()]);
      setMessage("Shot start image prompt saved.");
    });
  }

  function onGenerateCharacterReferences() {
    return withAction("generate-character-references", async () => {
      const result = await generateCharacterReferences(projectId, { force: false });
      await Promise.all([loadJobs(), loadPlan(), loadPreproduction()]);
      setMessage(`Queued ${result?.createdJobs ?? 0} character reference image job(s).`);
    });
  }

  function onGenerateShotStartImages() {
    return withAction("generate-shot-start-images", async () => {
      const result = await generateShotStartImages(projectId, { force: false });
      await Promise.all([loadJobs(), loadPlan(), loadPreproduction(), loadScenesAndShots()]);
      setMessage(`Queued ${result?.createdJobs ?? 0} shot start image job(s).`);
    });
  }

  function onGenerateCharacterReference(characterId) {
    return withAction("generate-character-references", async () => {
      const result = await generateCharacterReferences(projectId, { force: true, characterIds: [characterId] });
      await Promise.all([loadJobs(), loadPreproduction()]);
      setMessage(`Queued ${result?.createdJobs ?? 0} character reference image job(s).`);
    });
  }

  function onGenerateShotStartImage(shotId) {
    return withAction("generate-shot-start-images", async () => {
      const result = await generateShotStartImages(projectId, { force: true, shotIds: [shotId] });
      await Promise.all([loadJobs(), loadPreproduction(), loadScenesAndShots()]);
      setMessage(`Queued ${result?.createdJobs ?? 0} shot start image job(s).`);
    });
  }

  function onRegenerateMissingVisuals() {
    return withAction("regenerate-missing-visuals", async () => {
      const [characters, shotImages] = await Promise.all([
        generateCharacterReferences(projectId, { force: false }),
        generateShotStartImages(projectId, { force: false })
      ]);
      await Promise.all([loadJobs(), loadPlan(), loadPreproduction(), loadScenesAndShots()]);
      setMessage(`Queued ${(characters?.createdJobs ?? 0) + (shotImages?.createdJobs ?? 0)} missing visual job(s).`);
    });
  }

  function onToggleShot(shotId) {
    setSelectedShotIds((current) => (current.includes(shotId) ? current.filter((id) => id !== shotId) : [...current, shotId]));
  }

  function onGenerateAudio() {
    return withAction("audio", async () => {
      const result = await generateAudio(projectId, {
        voiceProvider: "EdgeTTS",
        language: "tr-TR",
        voice: "tr-TR-AhmetNeural",
        force: false
      });
      await loadJobs();
      setMessage(`Queued ${result?.createdJobs ?? 0} audio job(s).`);
    });
  }

  function onRegenerateAudio() {
    return withAction("audio-force", async () => {
      const result = await generateAudio(projectId, {
        voiceProvider: "EdgeTTS",
        language: "tr-TR",
        voice: "tr-TR-AhmetNeural",
        force: true
      });
      await loadJobs();
      setMessage(`Queued ${result?.createdJobs ?? 0} audio regeneration job(s).`);
    });
  }

  function onFinalize() {
    return withAction("finalize", async () => {
      const result = await finalizeProject(projectId);
      await loadJobs();
      await loadFinalVideo();
      setMessage(result?.createdJobId ? `Finalize job: ${result.createdJobId}` : "Finalize request completed.");
    });
  }

  function onAssemble() {
    return withAction("assemble", async () => {
      const result = await assembleProject(projectId, { force: false });
      await Promise.all([loadJobs(), loadFinalVideo()]);
      setMessage(result?.createdJobId ? `Assembly job: ${result.createdJobId}` : "Assembly already available.");
    });
  }

  function onRefinalize() {
    return withAction("refinalize", async () => {
      const result = await refinalizeProject(projectId);
      await loadJobs();
      await loadFinalVideo();
      setMessage(result?.createdJobId ? `Re-finalize job: ${result.createdJobId}` : "Re-finalize request completed.");
    });
  }

  function onResetStale() {
    return withAction("reset-stale", async () => {
      const result = await resetStaleJobs({ olderThanMinutes: 30 });
      await loadJobs();
      setMessage(`Reset stale jobs: ${result?.resetCount ?? 0}`);
    });
  }

  function onCleanupJobs() {
    return withAction("cleanup", async () => {
      const result = await cleanupProjectJobs(projectId);
      await loadJobs();
      setMessage(`Cleanup removed ${result?.removedJobs ?? 0}, kept ${result?.keptJobs ?? 0}.`);
    });
  }

  function onCancelActiveJobs() {
    return withAction("cancel-active-jobs", async () => {
      const result = await cancelProjectActiveJobs(projectId);
      await loadJobs();
      setMessage(`Cancelled ${result?.canceledJobs ?? 0} queued/running job(s) for this project.`);
    });
  }

  const completedJobs = jobs.filter((job) => isCompletedStatus(job.status));
  const characterReferenceCount = visualPlan?.characters?.filter((character) => character.referenceImageUrl || character.referenceImagePath).length ?? 0;
  const shotStartImageCount = visualPlan?.scenes?.reduce(
    (count, scene) => count + (scene.shots?.filter((shot) => shot.startImageUrl || shot.startImagePath).length ?? 0),
    0
  ) ?? 0;
  const characterCount = visualPlan?.characters?.length ?? 0;
  const sceneCount = visualPlan?.scenes?.length ?? 0;
  const shotCount = visualPlan?.scenes?.reduce((count, scene) => count + (scene.shots?.length ?? 0), 0) ?? 0;
  const completedRenderCount = completedJobs.filter((job) => job.jobTypeName === "RenderVideo").length;
  const completedAudioCount = dialogueLines.filter((line) => line.audioUrl || line.audioPath).length;
  const hasAssembly = completedJobs.some((job) => job.jobTypeName === "AssembleVideo") || Boolean(finalVideo?.assembledMediaUrl);
  const hasFinal = completedJobs.some((job) => job.jobTypeName === "MuxAudio") || Boolean(finalVideo?.mediaUrl);
  const activeJobCount = jobs.filter((job) => {
    const status = String(job.status).toLowerCase();
    return job.status === 0 || job.status === 1 || status === "pending" || status === "rendering";
  }).length;
  const creatorSteps = [
    {
      id: "content",
      label: "Content",
      status: plan ? "done" : project?.storyText ? "ready" : "ready",
      summary: plan ? `${sceneCount} scenes planned` : project?.storyText ? "Ready to analyze" : "Write a story"
    },
    {
      id: "cast",
      label: "Cast",
      status: characterReferenceCount > 0 ? "done" : characterCount > 0 ? "ready" : "waiting",
      summary: characterCount ? `${characterReferenceCount}/${characterCount} references` : "Analyze first"
    },
    {
      id: "storyboard",
      label: "Storyboard",
      status: completedRenderCount > 0 ? "done" : shotCount > 0 ? "ready" : "waiting",
      summary: shotCount ? `${shotStartImageCount}/${shotCount} keyframes` : "No shots yet"
    },
    {
      id: "edit",
      label: "Edit",
      status: hasFinal ? "done" : completedRenderCount > 0 || hasAssembly ? "ready" : "waiting",
      summary: hasFinal ? "Final ready" : hasAssembly ? "Ready to finalize" : "Assemble and audio"
    }
  ];

  if (!project) {
    return (
      <div className="studio-page">
        <div className="card">Loading project...</div>
      </div>
    );
  }

  return (
    <div className="studio-page">
      <CreatorShell
        title={project.title || project.name}
        projectStatus={project.status}
        activeJobCount={activeJobCount}
        steps={creatorSteps}
        activeStep={selectedStep}
        onStepChange={setSelectedStep}
        onBack={() => navigate("/")}
        onRefresh={refreshAll}
        message={message}
        error={error}
        railCollapsed={selectedStep === "content" || selectedStep === "edit"}
        rightRail={
          <>
            <VideoPreviewPanel
              finalMediaUrl={finalVideo?.mediaUrl || null}
              assembledMediaUrl={finalVideo?.assembledMediaUrl || null}
              renderMediaUrl={latestCompletedRender?.outputUrl || null}
              outputPath={finalVideo?.localPath || latestCompletedRender?.outputPath || null}
              assembledPath={finalVideo?.assembledLocalPath || null}
            />
            <details className="monitor-details">
              <summary>Job Monitor</summary>
              <RenderJobsPanel jobs={jobs} onRefresh={loadJobs} />
            </details>
          </>
        }
      >
        {selectedStep === "content" ? (
          <ContentStep
            project={project}
            plan={visualPlan}
            storyText={storyText}
            dialogueLines={dialogueLines}
            isBusy={Boolean(busyAction)}
            isSaving={busyAction === "save-story"}
            isAnalyzing={busyAction === "analyze"}
            canAnalyze={Boolean(project.storyText)}
            canGoNext={characterCount > 0}
            onStoryChange={setStoryText}
            onSaveStory={onSaveStory}
            onAnalyze={onAnalyze}
            onRefresh={refreshAll}
            onNext={() => setSelectedStep("cast")}
          />
        ) : null}

        {selectedStep === "cast" ? (
          <CastStep
            plan={visualPlan}
            isBusy={Boolean(busyAction)}
            busyAction={busyAction}
            onPrepareVisuals={onPrepareVisuals}
            onGenerateCharacterReferences={onGenerateCharacterReferences}
            onGenerateCharacterReference={onGenerateCharacterReference}
            onUploadReference={onUploadReference}
            onSaveCharacterPrompt={onSaveCharacterPrompt}
            onNext={() => setSelectedStep("storyboard")}
          />
        ) : null}

        {selectedStep === "storyboard" ? (
          <StoryboardStep
            plan={visualPlan}
            jobs={jobs}
            selectedShotIds={selectedShotIds}
            useShotStartImage={useShotStartImage}
            useCharacterReferenceInPrompt={useCharacterReferenceInPrompt}
            hasAnyShotStartImage={hasAnyShotStartImage}
            isBusy={Boolean(busyAction)}
            hasRunningRenderVideo={hasRunningRenderVideo}
            onSelectShot={onSelectStoryboardShot}
            onUseShotStartImageChange={onUseShotStartImageChange}
            onUseCharacterReferenceChange={setUseCharacterReferenceInPrompt}
            onSaveShotPrompt={onSaveShotPrompt}
            onUploadStartImage={onUploadStartImage}
            onGenerateShotStartImages={onGenerateShotStartImages}
            onGenerateShotStartImage={onGenerateShotStartImage}
            onAnimateSelected={onRenderStoryboardSelected}
            onAnimateAll={onRenderStoryboardAll}
          />
        ) : null}

        {selectedStep === "edit" ? (
          <EditStep
            finalVideo={finalVideo}
            latestCompletedRender={latestCompletedRender}
            jobs={jobs}
            dialogueLines={dialogueLines}
            shotCount={shotCount}
            completedRenderCount={completedRenderCount}
            completedAudioCount={completedAudioCount}
            hasAssembly={hasAssembly}
            hasFinal={hasFinal}
            hasRunningRenderVideo={hasRunningRenderVideo}
            hasRunningAudio={hasRunningAudio}
            hasRunningFinalize={hasRunningFinalize}
            busyAction={busyAction}
            onAssemble={onAssemble}
            onGenerateAudio={onGenerateAudio}
            onRegenerateAudio={onRegenerateAudio}
            onFinalize={onFinalize}
            onRefinalize={onRefinalize}
            onRefresh={refreshAll}
            onRefreshJobs={loadJobs}
            onRefreshDialogueLines={loadDialogueLines}
            onResetStale={onResetStale}
            onCleanupJobs={onCleanupJobs}
            onCancelActiveJobs={onCancelActiveJobs}
            onGoStoryboard={() => setSelectedStep("storyboard")}
          />
        ) : null}
      </CreatorShell>
    </div>
  );
}
