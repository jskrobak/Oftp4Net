namespace Oftp4Net.Services;

public class LocalFileService: IFileService
{
    public FileStream OpenRead(string filePath)
    {
        return File.OpenRead(filePath);
    }

    public async Task<byte[]> ReadAllBytesAsync(string filePath)
    {
        return await File.ReadAllBytesAsync(filePath);
    }

    public Task<string> ReadAllTextAsync(string filePath)
    {
        return File.ReadAllTextAsync(filePath);
    }

    public void DeleteFile(string filePath)
    {
        File.Delete(filePath);
    }

    public void CreateDirectory(string dirPath)
    {
        Directory.CreateDirectory(dirPath);
    }

    public string CombinePath(params string[] pathParts)
    {
        return Path.Combine(pathParts);
    }

    public async Task WriteAllBytesAsync(string filePath, byte[] content)
    {
        await File.WriteAllBytesAsync(filePath, content);
    }

    public bool FileExists(string filePath)
    {
        return File.Exists(filePath);
    }
}