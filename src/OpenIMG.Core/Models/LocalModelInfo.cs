namespace OpenIMG.Core.Models;

public record LocalModelInfo
{
    public string ModelName { get; init; } = string.Empty;
    public string ModelVersion { get; init; } = string.Empty;
    public string ModelSha256 { get; init; } = string.Empty;
    public string InputSize { get; init; } = string.Empty;
    public string ExecutionProvider { get; init; } = string.Empty;
    public string Precision { get; init; } = string.Empty;
}
