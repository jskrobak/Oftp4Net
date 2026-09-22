using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Oftp4Net.Domain;

namespace Oftp4Net.Entity;

/// <param name="dataProtectionProvider">
/// Encrypts passwords stored in the database. Not available at design time (migrations), where encryption is not needed.
/// </param>
public class O4NDbContext(DbContextOptions options, IDataProtectionProvider? dataProtectionProvider = null)
    : Havit.Data.EntityFrameworkCore.DbContext(options)
{
    public DbSet<Partner> Partners { get; init; }
    public DbSet<Identity> Identities { get; init; }
    public DbSet<SendQueueItem> SendQueueItems { get; init; }
    public DbSet<Listener> Listeners { get; init; }
    public DbSet<SettingsItem> GlobalSettings { get; init; }
    public DbSet<ReceivedFile> ReceivedFiles { get; init; }
    public DbSet<User> Users { get; init; }
    public DbSet<TransferEvent> TransferEvents { get; init; }
    public DbSet<ApiToken> ApiTokens { get; init; }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // The application works with local time (see ApplicationTimeService); store it as is.
        configurationBuilder.Properties<DateTime>().HaveColumnType("timestamp without time zone");
        configurationBuilder.Properties<DateTime?>().HaveColumnType("timestamp without time zone");
    }
    
    protected override void ModelCreatingCompleting(ModelBuilder modelBuilder)
    {
        base.ModelCreatingCompleting(modelBuilder);
        
        modelBuilder.Entity<Identity>(entity =>
        {
            entity.HasKey(e => e.Id);
        });
        
        modelBuilder.Entity<Partner>(entity =>
        {
            entity.HasKey(e => e.Id);
        });
        
        modelBuilder.Entity<SendQueueItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Partner);
            entity.HasOne(e => e.Identity);
        });

        modelBuilder.Entity<ReceivedFile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Partner).WithMany().OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => new { e.VirtualFileName, e.FileDate, e.FileTime });
        });

        modelBuilder.Entity<Listener>(entity =>
        {
            entity.HasOne(e => e.Identity).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Partner>(entity =>
        {
            entity.HasOne(e => e.TrustedCertificate).WithMany().OnDelete(DeleteBehavior.SetNull);
            // Stored as text so the table stays readable without the application.
            entity.Property(e => e.OutgoingEncoding).HasConversion<string>().HasMaxLength(10);
        });

        modelBuilder.Entity<SendQueueItem>(entity =>
        {
            entity.HasIndex(e => new { e.VirtualFileName, e.FileDate, e.FileTime });
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserName).IsUnique();
        });

        modelBuilder.Entity<TransferEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Stored as text so the table stays readable without the application.
            entity.Property(e => e.Category).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Level).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => new { e.Category, e.IsArchived, e.Timestamp });
            entity.HasIndex(e => new { e.IsArchived, e.Timestamp });
        });

        modelBuilder.Entity<ApiToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
        });

        ConfigureSecrets(modelBuilder);

        modelBuilder.Entity<SettingsItem>(entity =>
        {
            entity.HasKey(e => e.Name);
        });
    }

    /// <summary>Password columns hold the encrypted value, which is much longer than the password itself.</summary>
    private void ConfigureSecrets(ModelBuilder modelBuilder)
    {
        var converter = dataProtectionProvider is null
            ? null
            : new SecretValueConverter(dataProtectionProvider.CreateProtector(SecretValueConverter.PurposeName));

        modelBuilder.Entity<Partner>().Property(e => e.Password).HasMaxLength(1000).HasConversion((ValueConverter?)converter);
        modelBuilder.Entity<Identity>().Property(e => e.Password).HasMaxLength(1000).HasConversion((ValueConverter?)converter);
        modelBuilder.Entity<Certificate>().Property(e => e.Password).HasMaxLength(1000).HasConversion((ValueConverter?)converter);
        modelBuilder.Entity<ApiToken>().Property(e => e.WebhookSecret).HasMaxLength(1000).HasConversion((ValueConverter?)converter);
        modelBuilder.Entity<SendQueueItem>().Property(e => e.WebhookSecret).HasMaxLength(1000).HasConversion((ValueConverter?)converter);
    }
}
