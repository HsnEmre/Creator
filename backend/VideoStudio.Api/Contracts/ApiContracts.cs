using VideoStudio.Api.Domain;

namespace VideoStudio.Api.Contracts;

public sealed class CreateProjectRequest
{
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? StoryText { get; set; }
    public string? Description { get; set; }
    public int? TargetDurationSeconds { get; set; }
}

public sealed class StoryRequest
{
    public string? StoryText { get; set; }
}

public sealed class AnalyzeProjectRequest
{
    public string? Notes { get; set; }
}

public sealed record ProjectSummaryDto(Guid Id, string Title, string? StoryText, int TargetDurationSeconds, ProjectStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ProjectDetailsDto(Guid Id, string Title, string? StoryText, int TargetDurationSeconds, ProjectStatus Status, string? Logline, string? Genre, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ProductionPlanDto(
    string Title,
    string? Logline,
    string? Genre,
    int TargetDurationSeconds,
    VisualStyleDto VisualStyle,
    List<CharacterPlanDto> Characters,
    List<ScenePlanDto> Scenes,
    List<AudioCueDto> AudioCues)
{
    public int SceneCount { get; init; }
    public int ShotCount { get; init; }
    public int TotalPlannedDurationSeconds { get; init; }
    public int PlannedDurationCoveragePercent { get; init; }
    public bool IsDurationPlanValid { get; init; }
    public string? DurationPlanWarning { get; init; }
    public bool HasContinuityBible { get; init; }
    public bool CharacterVisualLocksApplied { get; init; }
    public int DistinctNegativePromptCount { get; init; }
    public int DuplicateNegativePromptGroups { get; init; }
    public string? ContinuityWarning { get; init; }
};

public sealed record VisualStyleDto(string StylePrompt, string NegativePrompt, string CameraStyle, string LightingStyle, string ColorPalette);

public sealed record CharacterPlanDto(
    string Name,
    string Role,
    string Personality,
    string VisualPrompt,
    string VoiceStyle,
    List<string> ContinuityRules)
{
    public Guid? Id { get; init; }
    public string? ReferenceImagePrompt { get; init; }
    public string? ReferenceImageNegativePrompt { get; init; }
    public string? ReferenceStatus { get; init; }
    public string? ReferenceImagePath { get; init; }
    public string? ReferenceImageUrl { get; init; }
}

public sealed record ScenePlanDto(
    int Index,
    string Title,
    string Summary,
    string Location,
    string TimeOfDay,
    string Mood,
    int EstimatedDurationSeconds,
    List<string> RequiredCharacters,
    List<ShotPlanDto> Shots,
    List<DialogueLineDto> DialogueLines)
{
    public Guid? Id { get; init; }
}

public sealed record ShotPlanDto(
    int Index,
    int DurationSeconds,
    string ShotType,
    string CameraMotion,
    string Action,
    string WanPrompt,
    string NegativePrompt,
    string AudioCue,
    string ContinuityNotes)
{
    public Guid? Id { get; init; }
    public Guid? SceneId { get; init; }
    public string? StartImagePrompt { get; init; }
    public string? StartImageNegativePrompt { get; init; }
    public string? StartImageStatus { get; init; }
    public string? StartImagePath { get; init; }
    public string? StartImageUrl { get; init; }
}

public sealed record DialogueLineDto(string Speaker, string Text, string Emotion, int EstimatedStartSecond, int EstimatedEndSecond);
public sealed record AudioCueDto(string Type, string Description, int StartSecond, int EndSecond);
public sealed record StoryResultDto(bool Success, ProductionPlanDto? Plan, string? Error);
public sealed class RenderRequestDto
{
    public RenderPreset? Preset { get; set; }
    public int? MaxShots { get; set; }
    public int? SceneIndex { get; set; }
    public int? ShotIndex { get; set; }
    public List<Guid>? ShotIds { get; set; }
    public bool Force { get; set; }
    public bool UseCharacterReference { get; set; }
    public bool UseCharacterReferenceInPrompt { get; set; }
    public bool UseShotStartImage { get; set; }
}

public sealed class ScenePatchRequest
{
    public int? SceneIndex { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string? Location { get; set; }
    public string? Mood { get; set; }
    public int? TargetDurationSeconds { get; set; }
}

public sealed class ShotPatchRequest
{
    public int? ShotIndex { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? VisualPrompt { get; set; }
    public string? CompiledPrompt { get; set; }
    public string? NegativePrompt { get; set; }
    public string? CameraDirection { get; set; }
    public string? MotionDirection { get; set; }
    public int? TargetDurationSeconds { get; set; }
    public string? DialogueText { get; set; }
    public string? ContinuityNotes { get; set; }
    public string? StartImagePath { get; set; }
    public Guid? SceneId { get; set; }
}

public sealed class AssembleRequest
{
    public bool Force { get; set; }
}

public sealed class ResetStaleJobsRequest
{
    public int? OlderThanMinutes { get; set; }
    public RenderJobStatus? SetStatus { get; set; }
}

public sealed class GenerateAudioRequest
{
    public string VoiceProvider { get; set; } = "EdgeTTS";
    public string Language { get; set; } = "tr-TR";
    public string Voice { get; set; } = "tr-TR-AhmetNeural";
    public bool Force { get; set; }
}
public sealed class FinalizeRequest
{
    public bool Force { get; set; }
}

public sealed class CharacterReferencePromptRequest
{
    public string? ReferenceImagePrompt { get; set; }
    public string? ReferenceImageNegativePrompt { get; set; }
}

public sealed class ShotStartImagePromptRequest
{
    public string? StartImagePrompt { get; set; }
    public string? StartImageNegativePrompt { get; set; }
}

public sealed class VisualGenerationRequest
{
    public bool Force { get; set; }
    public List<Guid>? CharacterIds { get; set; }
    public List<Guid>? ShotIds { get; set; }
}

public sealed record PreproductionCharacterDto(Guid Id, string Name, string Role, string VisualPrompt, string? ReferenceImagePrompt, string? ReferenceImageNegativePrompt, string ReferenceStatus, string? ReferenceImagePath, string? ReferenceImageUrl, Guid? JobId, RenderJobStatus? JobStatus);
public sealed record PreproductionShotDto(Guid Id, Guid SceneId, int SceneIndex, int ShotIndex, string ShotType, string Action, string? StartImagePrompt, string? StartImageNegativePrompt, string StartImageStatus, string? StartImagePath, string? StartImageUrl, Guid? JobId, RenderJobStatus? JobStatus);
public sealed record PreproductionDto(Guid ProjectId, string Title, List<PreproductionCharacterDto> Characters, List<PreproductionShotDto> Shots, List<string> MissingFields, List<string> Warnings);

public sealed record RenderQueuedShotDto(Guid ShotId, int SceneIndex, int ShotIndex);
public sealed record RenderQueuedDto(Guid ProjectId, int QueuedJobs, RenderPreset Preset, int MaxShots, List<RenderQueuedShotDto> Shots, int SkippedShots = 0, List<RenderQueuedShotDto>? Skipped = null);
public sealed record RenderStatusDto(Guid ProjectId, ProjectStatus ProjectStatus, int TotalJobs, int Queued, int Rendering, int Completed, int Failed);
public sealed record RenderJobDto(Guid Id, Guid ProjectId, Guid? SceneId, Guid? ShotId, Guid? CharacterId, Guid? DialogueLineId, RenderJobType JobType, RenderPreset Preset, VideoGenerationMode GenerationMode, string GenerationModeName, string Prompt, string? CompiledPrompt, string? NegativePrompt, string? Size, int? FrameNum, int? SampleSteps, int? Seed, string? InputImagePath, string? InputImageUrl, string? InputVideoPath, string? InputAudioPath, string? TextContent, string? Speaker, string? Emotion, string? Language, string? Voice, string? OutputPath, RenderJobStatus Status, int Progress, string? ErrorMessage, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, double? DurationSeconds);
public sealed record ProjectRenderJobDetailsDto(Guid JobId, Guid ProjectId, Guid? SceneId, Guid? ShotId, int? SceneIndex, int? ShotIndex, RenderJobType JobType, string JobTypeName, VideoGenerationMode GenerationMode, string GenerationModeName, RenderJobStatus Status, string StatusName, int Progress, RenderPreset Preset, string? InputImagePath, string? InputImageUrl, string? OutputPath, string? OutputUrl, string? ErrorMessage, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, double? DurationSeconds, bool IsLatestSuccessfulRenderForShot);
public sealed record DialogueLineDtoResponse(Guid Id, Guid SceneId, int SceneIndex, string Speaker, string Text, string Emotion, int EstimatedStartSecond, int EstimatedEndSecond, string? AudioPath, string? AudioUrl);
public sealed record CompleteRenderJobRequest(string OutputPath);
public sealed record FailRenderJobRequest(string ErrorMessage);
public sealed record ProgressRenderJobRequest(int Progress);
public sealed record AssetDto(Guid Id, Guid? ProjectId, string FileName, string ContentType, string Path, long SizeBytes);
public sealed record FinalVideoDto(Guid? Id, Guid ProjectId, string? LocalPath, string? MediaUrl, string? AssembledLocalPath, string? AssembledMediaUrl);
public sealed record ProjectMediaItemDto(string Type, Guid? JobId, string FileName, string LocalPath, string Url);
public sealed record ProjectMediaSummaryDto(List<ProjectMediaItemDto> ShotVideos, List<ProjectMediaItemDto> ShotStartImages, List<ProjectMediaItemDto> CharacterReferences, List<ProjectMediaItemDto> AudioFiles, ProjectMediaItemDto? AssembledVideo, ProjectMediaItemDto? FinalVideo);
public sealed record CharacterReferenceImageDto(Guid CharacterId, string ReferenceImagePath, string ReferenceImageUrl);
public sealed record ShotStartImageDto(Guid ShotId, string StartImagePath, string StartImageUrl);
