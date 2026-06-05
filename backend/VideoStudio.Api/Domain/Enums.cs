namespace VideoStudio.Api.Domain;

public enum ProjectStatus
{
    Draft,
    Planning,
    Planned,
    ReadyForRender,
    Rendering,
    Completed,
    Failed
}

public enum ShotStatus
{
    Draft,
    Pending,
    Planned,
    Queued,
    Rendering,
    Completed,
    Failed
}

public enum RenderJobStatus
{
    Pending,
    Rendering,
    Completed,
    Failed,
    Canceled
}

public enum RenderJobType
{
    ShotRender,
    SceneAssembly,
    FinalAssembly,
    GenerateAudio,
    MuxAudio,
    RenderVideo,
    AssembleVideo,
    GenerateCharacterReferenceImage,
    GenerateShotStartImage
}

public enum RenderPreset
{
    FastPreview,
    Preview,
    Final
}

public enum RenderDurationMode
{
    FastPreview,
    CinematicPreview,
    LongMotion,
    ComfyUIParity
}

public enum VideoGenerationMode
{
    TextToVideo,
    ImageToVideo,
    SpeechToVideo,
    VideoToVideo,
    Animate
}

public enum AssetType
{
    CharacterReference,
    InputImage,
    InputVideo,
    GeneratedVideo,
    GeneratedAudio,
    FinalVideo
}
