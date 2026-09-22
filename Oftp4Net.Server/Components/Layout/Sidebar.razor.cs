using Microsoft.AspNetCore.Components;

namespace Oftp4Net.Server.Components.Layout;

public partial class Sidebar : ComponentBase
{
    private bool isCollapsed = false;
    
    private Task HandleCollapsedChanged()
    {
        isCollapsed = !isCollapsed;
        return Task.CompletedTask;
    }
}