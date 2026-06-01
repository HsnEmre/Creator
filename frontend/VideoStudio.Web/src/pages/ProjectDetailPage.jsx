import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import {
  analyzeProject,
  assembleProject,
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
import StoryEditor from "../components/StoryEditor";
import ProductionPlanViewer from "../components/ProductionPlanViewer";
import RenderJobsPanel from "../components/RenderJobsPanel";
import DialogueLinesPanel from "../components/DialogueLinesPanel";
import VideoPreviewPanel from "../components/VideoPreviewPanel";
import ActionToolbar from "../components/ActionToolbar";
import SceneListPanel from "../components/SceneListPanel";
import SceneEditorPanel from "../components/SceneEditorPanel";
import ShotEditorPanel from "../components/ShotEditorPanel";
import ShotSelectionToolbar from "../components/ShotSelectionToolbar";
import AssemblyPanel from "../components/AssemblyPanel";
import VisualPreparationPanel from "../components/VisualPreparationPanel";
import WorkflowStageBar from "../components/WorkflowStageBar";

const VISUAL_JOB_TYPES = new Set(["GenerateCharacterReferenceImage", "GenerateShotStartImage"]);

function isCompletedStatus(status) {
  return status === 2 || String(status).toLowerCase() === "completed";
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
      const result = await renderProject(projectId, {
        preset: 0,
        force: true,
        useCharacterReferenceInPrompt,
        useShotStartImage,
        ...payload
      });
      await loadJobs();
      const warning = useShotStartImage && !hasAnyShotStartImage ? " No shot start image found. Render will use Text-to-Video." : "";
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

  if (!project) {
    return (
      <div className="studio-page">
        <div className="card">Loading project...</div>
      </div>
    );
  }

  return (
    <div className="studio-page">
      <div className="header">
        <div>
          <p className="eyebrow">Local AI Video Studio</p>
          <h1>{project.title || project.name}</h1>
        </div>
        <div className="header-actions">
          <button onClick={() => navigate("/")}>Back</button>
          <button onClick={refreshAll}>Refresh</button>
        </div>
      </div>

      <WorkflowStageBar project={project} plan={plan} jobs={jobs} finalVideo={finalVideo} />

      {message ? <p className="msg ok">{message}</p> : null}
      {error ? <p className="msg error">{error}</p> : null}

      <div className="layout">
        <aside className="panel left">
          <StoryEditor
            storyText={storyText}
            onChange={setStoryText}
            onSave={onSaveStory}
            isBusy={busyAction === "save-story"}
          />
          <ActionToolbar
            isBusy={Boolean(busyAction)}
            busyAction={busyAction}
            renderBusy={hasRunningRenderVideo}
            audioBusy={hasRunningAudio}
            finalizeBusy={hasRunningFinalize}
            useCharacterReferenceInPrompt={useCharacterReferenceInPrompt}
            useShotStartImage={useShotStartImage}
            hasAnyShotStartImage={hasAnyShotStartImage}
            onUseCharacterReferenceInPromptChange={setUseCharacterReferenceInPrompt}
            onUseShotStartImageChange={onUseShotStartImageChange}
            onAnalyze={onAnalyze}
            onPrepareVisuals={onPrepareVisuals}
            onGenerateCharacterReferences={onGenerateCharacterReferences}
            onGenerateShotStartImages={onGenerateShotStartImages}
            onRegenerateMissingVisuals={onRegenerateMissingVisuals}
            onRenderFastPreview={onRenderFastPreview}
            onGenerateAudio={onGenerateAudio}
            onRegenerateAudio={onRegenerateAudio}
            onFinalize={onFinalize}
            onRefinalize={onRefinalize}
            onResetStale={onResetStale}
            onCleanupJobs={onCleanupJobs}
          />
          <AssemblyPanel
            isBusy={Boolean(busyAction)}
            onAssemble={onAssemble}
            onFinalize={onFinalize}
            finalVideo={finalVideo}
          />
        </aside>

        <main className="panel center">
          <SceneListPanel scenes={scenes} selectedSceneId={selectedScene?.id} onSelectScene={setSelectedSceneId} />
          <SceneEditorPanel scene={selectedScene} onSave={onSaveScene} />
          <ShotSelectionToolbar
            selectedCount={selectedShotIds.length}
            isBusy={Boolean(busyAction)}
            onRenderSelected={onRenderSelected}
            onRenderScene={onRenderScene}
            onRenderAll={onRenderAll}
          />
          <ShotEditorPanel
            scene={selectedScene}
            shots={selectedSceneShots}
            selectedShotIds={selectedShotIds}
            onToggleShot={onToggleShot}
            onSaveShot={onSaveShot}
            onUploadStartImage={onUploadStartImage}
          />
          <VisualPreparationPanel
            plan={visualPlan}
            selectedScene={selectedScene}
            selectedShotIds={selectedShotIds}
            onSaveCharacterPrompt={onSaveCharacterPrompt}
            onSaveShotPrompt={onSaveShotPrompt}
            onUploadReference={onUploadReference}
            onUploadStartImage={onUploadStartImage}
            onGenerateCharacterReference={onGenerateCharacterReference}
            onGenerateShotStartImage={onGenerateShotStartImage}
            useShotStartImage={useShotStartImage}
            hasAnyShotStartImage={hasAnyShotStartImage}
          />
          <ProductionPlanViewer plan={visualPlan} onUploadReference={onUploadReference} onUploadStartImage={onUploadStartImage} />
        </main>

        <aside className="panel right">
          <RenderJobsPanel jobs={jobs} onRefresh={loadJobs} />
          <DialogueLinesPanel lines={dialogueLines} onRefresh={loadDialogueLines} />
          <VideoPreviewPanel
            finalMediaUrl={finalVideo?.mediaUrl || null}
            assembledMediaUrl={finalVideo?.assembledMediaUrl || null}
            renderMediaUrl={latestCompletedRender?.outputUrl || null}
            outputPath={finalVideo?.localPath || latestCompletedRender?.outputPath || null}
            assembledPath={finalVideo?.assembledLocalPath || null}
          />
        </aside>
      </div>
    </div>
  );
}
