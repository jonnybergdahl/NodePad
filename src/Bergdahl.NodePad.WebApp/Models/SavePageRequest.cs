namespace Bergdahl.NodePad.WebApp.Models;

public class SavePageRequest
{
    public string? Content { get; set; }
    public IEnumerable<string>? Tags { get; set; }
}