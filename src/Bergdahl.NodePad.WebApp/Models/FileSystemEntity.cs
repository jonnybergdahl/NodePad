namespace Bergdahl.NodePad.WebApp.Models;

public class FileSystemEntity
{
    public string Name { get; set; }
    public string Path { get; set; }
    public string Type { get; set; }
    public IEnumerable<FileSystemEntity> Children { get; set; }
}