using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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
    public DbSet<PartnerSetupDocument> PartnerSetupDocuments { get; init; }
    public DbSet<CertificateSigningRequest> CertificateSigningRequests { get; init; }

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
            // Our certificates per purpose; plain JSON, like the lists of a partner.
            JsonColumn(entity.Property(e => e.Certificates));
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
            entity.HasOne(e => e.SecurityCertificate).WithMany().OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.PreviousSecurityCertificate).WithMany().OnDelete(DeleteBehavior.SetNull);
            // Details from the OFTP2 Communication Setup are only read and written together with the partner.
            // Plain JSON values, not owned entities: the Havit unit of work does not support owned types.
            JsonColumn(entity.Property(e => e.Contacts));
            JsonColumn(entity.Property(e => e.InboundDsnPatterns));
            JsonColumn(entity.Property(e => e.OutboundDsnPatterns));
            JsonColumn(entity.Property(e => e.SubStations));
            JsonColumn(entity.Property(e => e.Certificates));
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

        modelBuilder.Entity<PartnerSetupDocument>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Partner).WithMany().OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.ReceivedFile).WithMany().OnDelete(DeleteBehavior.SetNull);
            // Stored as text so the table stays readable without the application.
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Signature).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(30);
            entity.HasIndex(e => new { e.Status, e.ValidFrom });
            entity.HasIndex(e => e.ReceivedFileId);
        });

        modelBuilder.Entity<CertificateSigningRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Certificate).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        ConfigureSecrets(modelBuilder);

        modelBuilder.Entity<SettingsItem>(entity =>
        {
            entity.HasKey(e => e.Name);
        });
    }

    /// <summary>A list stored as a JSON document; lists are compared by their content.</summary>
    private static void JsonColumn<T>(PropertyBuilder<List<T>> property)
    {
        property
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonList.Serialize(value),
                json => JsonList.Deserialize<T>(json),
                new ValueComparer<List<T>>(
                    (a, b) => JsonList.Serialize(a) == JsonList.Serialize(b),
                    value => JsonList.Serialize(value).GetHashCode(),
                    value => JsonList.Deserialize<T>(JsonList.Serialize(value))));
    }

    private static class JsonList
    {
        public static string Serialize<T>(List<T>? value) => JsonSerializer.Serialize(value ?? []);

        public static List<T> Deserialize<T>(string? json) =>
            string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<T>>(json) ?? [];
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
        // A 4096 bit key in PKCS#8 has about 3.2 kB in base64, encrypted more.
        modelBuilder.Entity<CertificateSigningRequest>().Property(e => e.PrivateKey).HasMaxLength(10000).HasConversion((ValueConverter?)converter);
        modelBuilder.Entity<ApiToken>().Property(e => e.WebhookSecret).HasMaxLength(1000).HasConversion((ValueConverter?)converter);
        modelBuilder.Entity<SendQueueItem>().Property(e => e.WebhookSecret).HasMaxLength(1000).HasConversion((ValueConverter?)converter);
    }
}
