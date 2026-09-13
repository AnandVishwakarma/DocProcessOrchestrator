namespace DocumentProcessing.Infrastructure.Options;

public sealed class DocDbOptions
{
    public const string SectionName = "DocDb";

    public string ConnectionString { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 15;
    public int OcrTimeoutSeconds { get; set; } = 5;
}
