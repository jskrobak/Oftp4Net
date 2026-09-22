using System.Reflection;
using Havit.Data.EntityFrameworkCore;
using Havit.Data.EntityFrameworkCore.Patterns.DependencyInjection;
using Havit.Data.EntityFrameworkCore.Patterns.Infrastructure;
using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.UnitOfWorks.EntityValidation;
using Havit.Data.Patterns.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Entity;
using Havit.Data.Patterns.Repositories;
using Havit.Services.Caching;
using Havit.Services.TimeServices;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Oftp4Net.Services;

namespace Oftp4Net.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <param name="enableSensitiveDataLogging">Log SQL parameter values; enable in development only.</param>
    public static IServiceCollection AddDataLayer(this IServiceCollection services,
        IConfiguration configuration, bool enableSensitiveDataLogging = false)
    {
        var connString = configuration.GetConnectionString("Oftp4Net");
        if (string.IsNullOrWhiteSpace(connString))
            throw new InvalidOperationException("Connection string 'Oftp4Net' is not configured.");

        services.AddDbContext<IDbContext, O4NDbContext>(options => options
            .UseNpgsql(connString)
            .EnableSensitiveDataLogging(enableSensitiveDataLogging));
        
        services.AddDataLayerCoreServices();
        
        //Partner
        services.TryAddScoped<IPartnerRepository, PartnerRepository>();
        services.TryAddScoped<IRepository<Partner, int>>(sp => sp.GetRequiredService<IPartnerRepository>());
        services.TryAddTransient<IEntityKeyAccessor<Partner, int>, DbEntityKeyAccessor<Partner, int>>();
        services.TryAddSingleton<IRepositoryQueryProvider<Partner, int>, PartnerDbRepositoryQueryProvider>();
        
        //Identity
        services.TryAddScoped<IIdentityRepository, IdentityRepository>();
        services.TryAddScoped<IRepository<Identity, int>>(sp => sp.GetRequiredService<IIdentityRepository>());
        services.TryAddTransient<IEntityKeyAccessor<Identity, int>, DbEntityKeyAccessor<Identity, int>>();
        services.TryAddSingleton<IRepositoryQueryProvider<Identity, int>, IdentityDbRepositoryQueryProvider>();
        
        //Listener
        services.TryAddScoped<IListenerRepository, ListenerRepository>();
        services.TryAddScoped<IRepository<Listener, int>>(sp => sp.GetRequiredService<IListenerRepository>());
        services.TryAddTransient<IEntityKeyAccessor<Listener, int>, DbEntityKeyAccessor<Listener, int>>();
        services.TryAddSingleton<IRepositoryQueryProvider<Listener, int>, ListenerDbRepositoryQueryProvider>();
        
        //Certificate
        services.TryAddScoped<ICertificateRepository, CertificateRepository>();
        services.TryAddScoped<IRepository<Certificate, int>>(sp => sp.GetRequiredService<ICertificateRepository>());
        services.TryAddTransient<IEntityKeyAccessor<Certificate, int>, DbEntityKeyAccessor<Certificate, int>>();
        services.TryAddSingleton<IRepositoryQueryProvider<Certificate, int>, CertificateDbRepositoryQueryProvider>();
        
        //SendQueueItem
        services.TryAddScoped<ISendQueueItemRepository, SendQueueItemRepository>();
        services.TryAddScoped<IRepository<SendQueueItem, int>>(sp => sp.GetRequiredService<ISendQueueItemRepository>());
        services.TryAddTransient<IEntityKeyAccessor<SendQueueItem, int>, DbEntityKeyAccessor<SendQueueItem, int>>();
        services.TryAddSingleton<IRepositoryQueryProvider<SendQueueItem, int>, SendQueueItemDbRepositoryQueryProvider>();
        
        //ReceivedFile
        services.TryAddScoped<IReceivedFileRepository, ReceivedFileRepository>();
        services.TryAddScoped<IRepository<ReceivedFile, int>>(sp => sp.GetRequiredService<IReceivedFileRepository>());
        services.TryAddTransient<IEntityKeyAccessor<ReceivedFile, int>, DbEntityKeyAccessor<ReceivedFile, int>>();
        services.TryAddSingleton<IRepositoryQueryProvider<ReceivedFile, int>, ReceivedFileDbRepositoryQueryProvider>();
        
        //User
        services.TryAddScoped<IUserRepository, UserRepository>();
        services.TryAddScoped<IRepository<User, int>>(sp => sp.GetRequiredService<IUserRepository>());
        services.TryAddTransient<IEntityKeyAccessor<User, int>, DbEntityKeyAccessor<User, int>>();
        services.TryAddSingleton<IRepositoryQueryProvider<User, int>, UserDbRepositoryQueryProvider>();
        
        //SettingsItem
        services.TryAddScoped<ISettingsItemRepository, SettingsItemRepository>();
        services.TryAddScoped<IRepository<SettingsItem, string>>(sp => sp.GetRequiredService<ISettingsItemRepository>());
        services.TryAddTransient<IEntityKeyAccessor<SettingsItem, string>, DbEntityKeyAccessor<SettingsItem, string>>();
        services.TryAddSingleton<IRepositoryQueryProvider<SettingsItem, string>, SettingsItemDbRepositoryQueryProvider>();
        
        services.AddSingleton<IEntityValidator<object>, ValidatableObjectEntityValidator>();

        services.AddSingleton<ITimeService, ApplicationTimeService>();
        services.AddSingleton<ICacheService, MemoryCacheService>();
        services.AddSingleton(new MemoryCacheServiceOptions { UseCacheDependenciesSupport = false });

        services.AddMemoryCache();
        
        return services;
    }
}