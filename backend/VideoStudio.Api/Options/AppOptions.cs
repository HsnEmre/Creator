namespace VideoStudio.Api.Options;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "llama3.1";
}

public sealed class StorageOptions
{
    public string RootPath { get; set; } = "../../storage";
}

public sealed class RenderSettings
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Frames { get; set; } = 81;
    public int Fps { get; set; } = 24;
    public int Seed { get; set; } = 42;
}
