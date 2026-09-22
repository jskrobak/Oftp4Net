using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Oftp4Net.Services;

public class UploadToMemoryCacheService(IMemoryCache memoryCache): IUploadService
{
    
    public async Task<string> SaveFileAsync(IFormFile file)
    {
        var id = Guid.NewGuid().ToString();
        
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        var fileBytes = stream.ToArray();
        
        // Uploads are consumed right after the upload request by the page that started it.
        memoryCache.Set(id, fileBytes, TimeSpan.FromMinutes(10));
        
        return id;
    }

    public string GetFilePath(string fileId)
    {
        return fileId;
    }

    public Task<byte[]> ReadAllBytesAsync(string fileId)
    {
        if (!memoryCache.TryGetValue<byte[]>(fileId, out var data) || data is null)
            throw new FileNotFoundException("Uploaded file not found or expired.", fileId);

        memoryCache.Remove(fileId);
        return Task.FromResult(data);
    }
}