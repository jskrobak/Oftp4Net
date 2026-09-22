using System.Reflection;
using System.Text.Json;
using Havit.Data.Patterns.UnitOfWorks;
using Microsoft.Extensions.DependencyInjection;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services;

/// <summary>
/// Loads and stores <see cref="GlobalSettings"/> as one JSON value per property in the settings table.
/// Every call returns a new instance, so callers can modify it freely before saving.
/// </summary>
public class GlobalSettingsService(IServiceScopeFactory serviceScopeFactory)
{
    private static readonly PropertyInfo[] SettingsProperties = typeof(GlobalSettings)
        .GetProperties()
        .Where(p => p.GetCustomAttribute<SettingsItemAttribute>() != null)
        .ToArray();

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, string>? _cache;

    public event Action? SettingsChanged;

    public async Task<GlobalSettings> GetGlobalSettingsAsync()
    {
        var values = await GetValuesAsync();

        var settings = new GlobalSettings();
        foreach (var prop in SettingsProperties)
        {
            if (values.TryGetValue(prop.Name, out var json))
                prop.SetValue(settings, JsonSerializer.Deserialize(json, prop.PropertyType));
        }

        return settings;
    }

    public async Task SetGlobalSettingsAsync(GlobalSettings settings)
    {
        await _lock.WaitAsync();
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISettingsItemRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var dbItems = (await repo.GetAllAsync()).ToDictionary(i => i.Name);
            var changed = false;

            foreach (var prop in SettingsProperties)
            {
                var json = JsonSerializer.Serialize(prop.GetValue(settings), prop.PropertyType);

                if (!dbItems.TryGetValue(prop.Name, out var dbItem))
                {
                    unitOfWork.AddForInsert(new SettingsItem { Name = prop.Name, Json = json });
                    changed = true;
                }
                else if (dbItem.Json != json)
                {
                    dbItem.Json = json;
                    unitOfWork.AddForUpdate(dbItem);
                    changed = true;
                }
            }

            if (!changed)
                return;

            await unitOfWork.CommitAsync();
            _cache = null;
        }
        finally
        {
            _lock.Release();
        }

        SettingsChanged?.Invoke();
    }

    private async Task<Dictionary<string, string>> GetValuesAsync()
    {
        if (_cache is { } cache)
            return cache;

        await _lock.WaitAsync();
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISettingsItemRepository>();
            var items = await repo.GetAllAsync();
            return _cache = items.ToDictionary(i => i.Name, i => i.Json);
        }
        finally
        {
            _lock.Release();
        }
    }
}
