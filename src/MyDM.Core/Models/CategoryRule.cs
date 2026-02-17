namespace MyDM.Core.Models;

public class CategoryRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string[] Extensions { get; set; } = Array.Empty<string>();
    public string[] MimeTypes { get; set; } = Array.Empty<string>();
    public string SaveFolder { get; set; } = string.Empty;
}
