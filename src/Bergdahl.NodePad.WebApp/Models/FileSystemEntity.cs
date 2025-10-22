namespace Bergdahl.NodePad.WebApp.Models;

public class FileSystemEntity
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public required string Type { get; set; }
    public IEnumerable<FileSystemEntity>? Children { get; set; }
    // Optional enrichment fields for directories (when includeCounts=true)
    public int? FileCount { get; set; }
    public int? TotalFileCount { get; set; }
    // Optional title enrichment for files (when includeTitles=true)
    public string? Title { get; set; }
}