namespace VideoStudio.Api.Domain;

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? StoryText { get; set; }
    public string? Genre { get; set; }
    public int TargetDurationSeconds { get; set; } = 60;
    public string? Logline { get; set; }
    public string? VisualStylePrompt { get; set; }
    public string? NegativePrompt { get; set; }
    public string? CameraStyle { get; set; }
    public string? LightingStyle { get; set; }
    public string? ColorPalette { get; set; }
    public string QualityGoal { get; set; } = "Balanced";
    public string? DirectorTreatment { get; set; }
    public string BeatSheetJson { get; set; } = "[]";
    public string ActBreakdownJson { get; set; } = "[]";
    public string CharacterBibleJson { get; set; } = "[]";
    public string LocationBibleJson { get; set; } = "[]";
    public string TimelineContinuityJson { get; set; } = "{}";
    public string VisualContinuityRulesJson { get; set; } = "[]";
    public string RenderStrategyRecommendationJson { get; set; } = "{}";
    public string AudioCuesJson { get; set; } = "[]";
    public ProjectStatus Status { get; set; } = ProjectStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Character> Characters { get; set; } = [];
    public List<Scene> Scenes { get; set; } = [];
    public List<Asset> Assets { get; set; } = [];
    public List<AudioTrack> AudioTracks { get; set; } = [];
    public List<DialogueLine> DialogueLines { get; set; } = [];
    public List<RenderJob> RenderJobs { get; set; } = [];
    public FinalVideo? FinalVideo { get; set; }
}

public sealed class Character
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ReferenceImagePrompt { get; set; }
    public string? ReferenceImageNegativePrompt { get; set; }
    public string ReferenceStatus { get; set; } = "Missing";
    public string? ReferenceImagePath { get; set; }
    public string? ReferenceImageUrl { get; set; }
    public Guid? ReferenceAssetId { get; set; }
    public Asset? ReferenceAsset { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string VisualPrompt { get; set; } = string.Empty;
    public string VoiceStyle { get; set; } = string.Empty;
    public string ContinuityRulesJson { get; set; } = "[]";
    public string CharacterBibleJson { get; set; } = "{}";
}

public sealed class Scene
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public int Order { get; set; }
    public int Index { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string TimeOfDay { get; set; } = string.Empty;
    public string Mood { get; set; } = string.Empty;
    public int EstimatedDurationSeconds { get; set; }
    public string? Purpose { get; set; }
    public string? StoryStateBefore { get; set; }
    public string? StoryStateAfter { get; set; }
    public string? LocationId { get; set; }
    public string? SceneAnchorPrompt { get; set; }
    public string? LocationContinuityPrompt { get; set; }
    public string? ForbiddenLocationDrift { get; set; }
    public string RequiredCharactersJson { get; set; } = "[]";
    public string DialogueLinesJson { get; set; } = "[]";
    public List<Shot> Shots { get; set; } = [];
    public List<DialogueLine> DialogueLines { get; set; } = [];
}

public sealed class Shot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid SceneId { get; set; }
    public Scene? Scene { get; set; }
    public int Order { get; set; }
    public int Index { get; set; }
    public int DurationSeconds { get; set; } = 5;
    public string ShotType { get; set; } = string.Empty;
    public string CameraMotion { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string? NegativePrompt { get; set; }
    public string AudioCue { get; set; } = string.Empty;
    public string ContinuityNotes { get; set; } = string.Empty;
    public string InvolvedCharacterIdsJson { get; set; } = "[]";
    public string? CharacterLockPrompt { get; set; }
    public string? LocationId { get; set; }
    public string? LocationLockPrompt { get; set; }
    public string? ForbiddenDriftTerms { get; set; }
    public string? PreviousShotVisualState { get; set; }
    public string? CurrentShotVisualState { get; set; }
    public string? NextShotSetup { get; set; }
    public string? KeyframeContinuityPrompt { get; set; }
    public string? SceneAnchorPrompt { get; set; }
    public string? RecommendedRenderDurationMode { get; set; }
    public bool AssemblyExtensionAllowed { get; set; } = true;
    public VideoGenerationMode GenerationMode { get; set; } = VideoGenerationMode.TextToVideo;
    public ShotStatus Status { get; set; } = ShotStatus.Pending;
    public string? InputImagePath { get; set; }
    public string? StartImagePrompt { get; set; }
    public string? StartImageNegativePrompt { get; set; }
    public string StartImageStatus { get; set; } = "Missing";
    public string? StartImagePath { get; set; }
    public string? StartImageUrl { get; set; }
    public Guid? StartImageAssetId { get; set; }
    public Asset? StartImageAsset { get; set; }
    public string? InputVideoPath { get; set; }
    public string? InputAudioPath { get; set; }
    public string? OutputPath { get; set; }
    public List<RenderJob> RenderJobs { get; set; } = [];
}

public sealed class Asset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? CharacterId { get; set; }
    public Character? Character { get; set; }
    public Guid? ShotId { get; set; }
    public Shot? Shot { get; set; }
    public AssetType Type { get; set; } = AssetType.InputImage;
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public string? MediaUrl { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AudioTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public TimeSpan? Duration { get; set; }
}

public sealed class RenderJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? SceneId { get; set; }
    public Scene? Scene { get; set; }
    public Guid? ShotId { get; set; }
    public Shot? Shot { get; set; }
    public Guid? CharacterId { get; set; }
    public Character? Character { get; set; }
    public Guid? DialogueLineId { get; set; }
    public DialogueLine? DialogueLine { get; set; }
    public RenderJobType JobType { get; set; } = RenderJobType.ShotRender;
    public RenderPreset Preset { get; set; } = RenderPreset.FastPreview;
    public RenderDurationMode RenderDurationMode { get; set; } = RenderDurationMode.FastPreview;
    public VideoGenerationMode GenerationMode { get; set; } = VideoGenerationMode.TextToVideo;
    public string? Size { get; set; }
    public int? FrameNum { get; set; }
    public int? SampleSteps { get; set; }
    public int? Seed { get; set; }
    public int? RequestedShotDurationSeconds { get; set; }
    public int? RequestedFrameNum { get; set; }
    public int? ActualFrameNum { get; set; }
    public double? ExpectedRawClipDurationSeconds { get; set; }
    public double? ProbedRawClipDurationSeconds { get; set; }
    public int? RawDurationCoveragePercent { get; set; }
    public string? CompiledPrompt { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string? NegativePrompt { get; set; }
    public string? InputImagePath { get; set; }
    public string? InputVideoPath { get; set; }
    public string? InputAudioPath { get; set; }
    public string? TextContent { get; set; }
    public string? Speaker { get; set; }
    public string? Emotion { get; set; }
    public string? Language { get; set; }
    public string? Voice { get; set; }
    public string? OutputPath { get; set; }
    public RenderJobStatus Status { get; set; } = RenderJobStatus.Pending;
    public int Progress { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class DialogueLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid SceneId { get; set; }
    public Scene? Scene { get; set; }
    public string Speaker { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Emotion { get; set; } = string.Empty;
    public int EstimatedStartSecond { get; set; }
    public int EstimatedEndSecond { get; set; }
    public string? AudioPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FinalVideo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Path { get; set; } = string.Empty;
    public TimeSpan? Duration { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
