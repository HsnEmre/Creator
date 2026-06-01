namespace VideoStudio.Api.Services;

public sealed class OllamaRequestException(string message) : Exception(message);
