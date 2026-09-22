using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;


namespace Oftp4Net.Entity;

public class O4NDesignTimeDbContextFactory:IDesignTimeDbContextFactory<O4NDbContext>
{
    public O4NDbContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appSettings.Entity.json")
            .AddJsonFile($"appSettings.Entity.{environment}.json", true)
            .AddJsonFile($"appSettings.Entity.{environment}.local.json", true) // .gitignored
            .Build();

        var connectionString = configuration.GetConnectionString("Oftp4Net");
        
        var builder = new DbContextOptionsBuilder<O4NDbContext>();
        
        builder.UseNpgsql(connectionString);
        
        return new O4NDbContext(builder.Options);
    }
}