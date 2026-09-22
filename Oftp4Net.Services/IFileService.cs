namespace Oftp4Net.Services;

public interface IFileService
{
    FileStream OpenRead(string filePath);
    Task<byte[]> ReadAllBytesAsync(string filePath);
    Task<string> ReadAllTextAsync(string filePath);
    void DeleteFile(string filePath);
    void CreateDirectory(string filePath);
    
    string CombinePath(params string[] pathParts);
    Task WriteAllBytesAsync(string filePath, byte[] content);
    bool FileExists(string filePath);
}