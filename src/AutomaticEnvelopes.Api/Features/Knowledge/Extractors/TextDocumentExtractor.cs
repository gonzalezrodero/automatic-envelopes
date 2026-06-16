namespace AutomaticEnvelopes.Api.Features.Knowledge.Extractors;

public class TextDocumentExtractor : IDocumentExtractor
{
    public string[] SupportedExtensions => [".md", ".txt"];

    public async Task<string> ExtractTextAsync(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
