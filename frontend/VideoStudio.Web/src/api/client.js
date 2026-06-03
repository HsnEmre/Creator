const API_BASE = import.meta.env.VITE_API_BASE_URL || "http://localhost:5281";

async function request(path, options = {}) {
  const allowNotFound = Boolean(options.allowNotFound);
  const isFormData = typeof FormData !== "undefined" && options.body instanceof FormData;
  const response = await fetch(`${API_BASE}${path}`, {
    headers: isFormData ? (options.headers || {}) : { "Content-Type": "application/json", ...(options.headers || {}) },
    ...options
  });

  if (allowNotFound && response.status === 404) {
    return null;
  }

  if (response.status === 204) {
    return null;
  }

  const text = await response.text();
  const body = text ? safeParseJson(text) : null;

  if (!response.ok) {
    const message =
      (body && (body.error || body.title || body.detail || body.message)) ||
      text ||
      `HTTP ${response.status}`;
    throw new Error(message);
  }

  return body;
}

function safeParseJson(text) {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

export function createProject(payload) {
  return request("/api/projects", { method: "POST", body: JSON.stringify(payload) });
}

export function getProjects() {
  return request("/api/projects");
}

export function getProject(projectId) {
  return request(`/api/projects/${projectId}`);
}

export function saveStory(projectId, storyText) {
  return request(`/api/projects/${projectId}/story`, {
    method: "POST",
    body: JSON.stringify({ storyText })
  });
}

export function analyzeProject(projectId) {
  return request(`/api/projects/${projectId}/analyze`, {
    method: "POST",
    body: JSON.stringify({})
  });
}

export function getProductionPlan(projectId) {
  return request(`/api/projects/${projectId}/production-plan`, { allowNotFound: true });
}

export function getScenes(projectId) {
  return request(`/api/projects/${projectId}/scenes`);
}

export function getShots(projectId) {
  return request(`/api/projects/${projectId}/shots`);
}

export function updateScene(projectId, sceneId, payload) {
  return request(`/api/projects/${projectId}/scenes/${sceneId}`, {
    method: "PATCH",
    body: JSON.stringify(payload)
  });
}

export function updateShot(projectId, sceneId, shotId, payload) {
  return request(`/api/projects/${projectId}/scenes/${sceneId}/shots/${shotId}`, {
    method: "PATCH",
    body: JSON.stringify(payload)
  });
}

export function renderProject(projectId, payload) {
  return request(`/api/projects/${projectId}/render`, {
    method: "POST",
    body: JSON.stringify(payload)
  });
}

export function uploadCharacterReferenceImage(projectId, characterId, file) {
  const formData = new FormData();
  formData.append("file", file);
  return request(`/api/projects/${projectId}/characters/${characterId}/reference-image`, {
    method: "POST",
    body: formData,
    headers: {}
  });
}

export function uploadShotStartImage(projectId, sceneId, shotId, file) {
  const formData = new FormData();
  formData.append("file", file);
  return request(`/api/projects/${projectId}/scenes/${sceneId}/shots/${shotId}/start-image`, {
    method: "POST",
    body: formData,
    headers: {}
  });
}

export function preparePreproduction(projectId) {
  return request(`/api/projects/${projectId}/preproduction/prepare`, {
    method: "POST",
    body: JSON.stringify({})
  });
}

export function getPreproduction(projectId) {
  return request(`/api/projects/${projectId}/preproduction`, { allowNotFound: true });
}

export function updateCharacterReferencePrompt(projectId, characterId, payload) {
  return request(`/api/projects/${projectId}/characters/${characterId}/reference-prompt`, {
    method: "PATCH",
    body: JSON.stringify(payload)
  });
}

export function updateShotStartImagePrompt(projectId, sceneId, shotId, payload) {
  return request(`/api/projects/${projectId}/scenes/${sceneId}/shots/${shotId}/start-image-prompt`, {
    method: "PATCH",
    body: JSON.stringify(payload)
  });
}

export function generateCharacterReferences(projectId, payload = { force: false }) {
  return request(`/api/projects/${projectId}/visuals/generate-character-references`, {
    method: "POST",
    body: JSON.stringify(payload)
  });
}

export function generateShotStartImages(projectId, payload = { force: false }) {
  return request(`/api/projects/${projectId}/visuals/generate-shot-start-images`, {
    method: "POST",
    body: JSON.stringify(payload)
  });
}

export function getRenderJobs(projectId) {
  return request(`/api/projects/${projectId}/render-jobs`);
}

export function getDialogueLines(projectId) {
  return request(`/api/projects/${projectId}/dialogue-lines`);
}

export function generateAudio(projectId, payload) {
  return request(`/api/projects/${projectId}/audio/generate`, {
    method: "POST",
    body: JSON.stringify(payload)
  });
}

export function finalizeProject(projectId) {
  return request(`/api/projects/${projectId}/finalize`, {
    method: "POST",
    body: JSON.stringify({ force: false })
  });
}

export function assembleProject(projectId, payload = { force: false }) {
  return request(`/api/projects/${projectId}/assemble`, {
    method: "POST",
    body: JSON.stringify(payload)
  });
}

export function refinalizeProject(projectId) {
  return request(`/api/projects/${projectId}/finalize`, {
    method: "POST",
    body: JSON.stringify({ force: true })
  });
}

export function getFinalVideo(projectId) {
  return request(`/api/projects/${projectId}/final-video`, { allowNotFound: true });
}

export function toAbsoluteApiUrl(path) {
  if (!path) return "";
  if (/^https?:\/\//i.test(path)) return path;
  return `${API_BASE}${path}`;
}

export function resetStaleJobs(payload = { olderThanMinutes: 30 }) {
  return request("/api/worker/jobs/reset-stale", {
    method: "POST",
    body: JSON.stringify(payload)
  });
}

export function cleanupProjectJobs(projectId) {
  return request(`/api/projects/${projectId}/jobs/cleanup`, {
    method: "POST",
    body: JSON.stringify({})
  });
}

export function cancelProjectActiveJobs(projectId) {
  return request(`/api/projects/${projectId}/jobs/cancel-active`, {
    method: "POST",
    body: JSON.stringify({})
  });
}
