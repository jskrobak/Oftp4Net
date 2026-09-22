namespace Oftp4Net.Domain;

public abstract class BaseQueueItem
{
    public int Id { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;   
    public string? LastError { get; set; }
    public DateTime? LastErrorDate { get; set; }
    public int RetryCount { get; set; } = 0;
    public DateTime NextRetry { get; set; } = DateTime.MinValue;
}