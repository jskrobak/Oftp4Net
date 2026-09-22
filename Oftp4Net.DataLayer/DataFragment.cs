namespace Oftp4Net.DataLayer;

public class DataFragment<TItem>
{
    public List<TItem> Data { get; init; } = [];

    /// <summary>
    /// Celkový počet záznamů bez ohledu na stránkování/segmentování.
    /// </summary>
    public int TotalCount { get; init; }
}