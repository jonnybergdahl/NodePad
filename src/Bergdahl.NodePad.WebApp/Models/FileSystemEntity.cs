namespace Bergdahl.NodePad.WebApp.Models;

public class FileSystemEntity
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public required string Type { get; set; }
    public IEnumerable<FileSystemEntity>? Children { get; set; }
}