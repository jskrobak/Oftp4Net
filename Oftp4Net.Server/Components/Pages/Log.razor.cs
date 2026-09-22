using Microsoft.AspNetCore.Components;
using Oftp4Net.Server.Logging;

namespace Oftp4Net.Server.Components.Pages;

public partial class Log : ComponentBase, IDisposable
{
    private const int MaxMessages = 500;

    [Inject] private LogBroadcaster Broadcaster { get; set; } = null!;

    private static readonly LogLevel[] levels = [LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error];
    private List<LogEntry> messages = [];
    private LogLevel minLevel = LogLevel.Information;
    private bool paused;

    protected override void OnInitialized()
    {
        messages = Broadcaster.GetRecent().Reverse().ToList();
        Broadcaster.EntryAdded += HandleEntryAdded;
    }

    private void HandleEntryAdded(LogEntry entry)
    {
        if (paused)
            return;

        InvokeAsync(() =>
        {
            messages.Insert(0, entry);
            if (messages.Count > MaxMessages)
                messages.RemoveAt(messages.Count - 1);
            StateHasChanged();
        });
    }

    private static string GetCssClass(LogLevel level) => level switch
    {
        >= LogLevel.Error => "text-danger",
        LogLevel.Warning => "text-warning",
        LogLevel.Debug or LogLevel.Trace => "text-secondary",
        _ => ""
    };

    public void Dispose()
    {
        Broadcaster.EntryAdded -= HandleEntryAdded;
    }
}
