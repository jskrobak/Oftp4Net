using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Oftp4Net.Services;

public class UploadService: IUploadService
{
    private readonly ILogger<UploadService> _logger;
    private readonly string _basePath;
    private readonly IFileService _fileService;
    
    public UploadService(ILogger<UploadService> logger, IConfiguration configuration, IFileService fileService)
    {
        _logger = logger;
        _basePath = configuration["UploadBasePath"] ?? "uploads";
        _fileService = fileService;
        
        fileService.CreateDirectory(_basePath);
    }

    public async Task<string> SaveFileAsync(IFormFile file)
    {
        var id = Guid.NewGuid().ToString();

        var filePath = GetFilePath(id);
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        var fileBytes = stream.ToArray();
        
        await _fileService.WriteAllBytesAsync(filePath, fileBytes);
        
        return id;
    }

    
    public string GetFilePath(string id)
    {
        return _fileService.CombinePath(_basePath, id);
    }

    public async Task<byte[]> ReadAllBytesAsync(string fileId)
    {
        return await _fileService.ReadAllBytesAsync(GetFilePath(fileId));
    }
}