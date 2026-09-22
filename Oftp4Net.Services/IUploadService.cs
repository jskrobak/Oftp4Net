using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Oftp4Net.Services;

public interface IUploadService
{
    public Task<string> SaveFileAsync([FromForm] IFormFile file);
    string GetFilePath(string fileId);
    public Task<byte[]> ReadAllBytesAsync(string fileId);
}