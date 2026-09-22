using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Oftp4Net.DataLayer.Repositories;
using BitzArt.Blazor.Cookies;
using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.DependencyInjection;
using Oftp4Net.Server;
using Oftp4Net.Server.Api;
using Oftp4Net.Server.Components;
using Oftp4Net.Server.Logging;
using Oftp4Net.Services;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.Api;
using Oftp4Net.Services.Hooks;
using Oftp4Net.Services.TransferEvents;
using Serilog;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

// Logging: file (Serilog), console and the in-app Log page.
Log.Logger = new LoggerConfiguration()
    .WriteTo.File(Path.Combine(builder.Configuration["LogDirectory"] ?? "logs", "app.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog();
builder.Logging.AddConsole();

var logBroadcaster = new LogBroadcaster();
builder.Services.AddSingleton(logBroadcaster);
builder.Logging.AddProvider(new BroadcastLoggerProvider(logBroadcaster));

// Keys encrypt the authentication cookie and the passwords stored in the database. Back them up with the database.
builder.Services.AddDataProtection()
    .SetApplicationName("Oftp4Net")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeysDirectory"] ?? "keys"));

builder.Services.AddOpenApi();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenAuthenticationHandler.SchemeName, null)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.Cookie.Name = "Oftp4Net.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization(options =>
{
    // Everything except the login page and static files requires a signed in user.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
builder.Services.AddCascadingAuthenticationState();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust the reverse proxy only when explicitly configured.
    if (builder.Configuration.GetValue<bool>("ReverseProxy:TrustAll"))
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHxServices();
builder.Services.AddHxMessenger();
builder.Services.AddHxMessageBoxHost();

builder.Services.AddDataLayer(builder.Configuration, enableSensitiveDataLogging: builder.Environment.IsDevelopment());

builder.Services.AddTransient<IDataService, DataService>();
builder.Services.AddTransient<IUploadService, UploadToMemoryCacheService>();
builder.Services.AddSingleton<IFileService, LocalFileService>();
builder.Services.AddSingleton<GlobalSettingsService>();
builder.Services.AddSingleton<OutboxStorage>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<CertificateSeedService>();
builder.Services.AddScoped<SelfSignedCertificateService>();
builder.Services.AddScoped<LoopbackSeedService>();

builder.AddBlazorCookies();

builder.Services.AddSingleton<TransferClaims>();
builder.Services.AddScoped<ApiTokenService>();
builder.Services.AddHttpClient(WebhookDispatcher.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddSingleton<IWebhookDispatcher>(sp => sp.GetRequiredService<WebhookDispatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
builder.Services.AddSingleton<TransferEventLog>();
builder.Services.AddSingleton<ITransferEventLog>(sp => sp.GetRequiredService<TransferEventLog>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TransferEventLog>());
builder.Services.AddSingleton<HookRunner>();
builder.Services.AddSingleton<IHookDispatcher>(sp => sp.GetRequiredService<HookRunner>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<HookRunner>());
// Hosted services start in registration order: listeners first, so the send queue can reach a local listener
// (e.g. the development loopback partner) right away.
builder.Services.AddSingleton<ListenerService>();
builder.Services.AddHostedService(serviceCollection => serviceCollection.GetRequiredService<ListenerService>());
builder.Services.AddSingleton<SendService>();
builder.Services.AddHostedService(serviceCollection => serviceCollection.GetRequiredService<SendService>());

builder.Services.AddResponseCompression(opts =>
{
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        [ "application/octet-stream" ]);
});

var app = builder.Build();

// Create or update the database schema, the default user admin/admin on an empty database
// the development data (SeedCertificates, SeedLoopback) and, when there is none, an own TLS certificate.
using (var scope = app.Services.CreateScope())
{
    if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
    {
        var dbContext = (Microsoft.EntityFrameworkCore.DbContext)scope.ServiceProvider.GetRequiredService<IDbContext>();
        app.Logger.LogInformation("Applying database migrations");
        await dbContext.Database.MigrateAsync();
    }

    await scope.ServiceProvider.GetRequiredService<UserService>().EnsureDefaultUserAsync();
    await scope.ServiceProvider.GetRequiredService<CertificateSeedService>().SeedAsync(app.Environment.ContentRootPath);
    await scope.ServiceProvider.GetRequiredService<SelfSignedCertificateService>().EnsureCertificateAsync();
    await scope.ServiceProvider.GetRequiredService<LoopbackSeedService>().SeedAsync(app.Environment.ContentRootPath);
}

app.UseForwardedHeaders();
app.UseResponseCompression();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapApi();
app.MapOpenApi().AllowAnonymous();

app.MapPost("/account/login", async (HttpContext httpContext, UserService userService, [FromForm] string username,
    [FromForm] string password, [FromForm] string? returnUrl) =>
{
    var user = await userService.ValidateCredentialsAsync(username, password);
    if (user is null)
    {
        app.Logger.LogWarning("Failed sign in attempt for {UserName} from {RemoteIp}", username, httpContext.Connection.RemoteIpAddress);
        await Task.Delay(TimeSpan.FromSeconds(1));
        return Results.Redirect($"/Login?error=invalid&returnUrl={Uri.EscapeDataString(returnUrl ?? "")}");
    }

    await SignInAsync(httpContext, user.UserName, user.MustChangePassword);

    if (user.MustChangePassword)
        return Results.Redirect("/ChangePassword");

    // Only redirect to local paths to prevent open redirects.
    var target = "/" + (returnUrl ?? "").TrimStart('/');
    return Results.LocalRedirect(Uri.IsWellFormedUriString(target, UriKind.Relative) && !target.StartsWith("//") ? target : "/");
}).AllowAnonymous();

app.MapPost("/account/change-password", async (HttpContext httpContext, UserService userService,
    [FromForm] string currentPassword, [FromForm] string newPassword, [FromForm] string confirmPassword) =>
{
    var userName = httpContext.User.Identity!.Name!;
    try
    {
        if (newPassword != confirmPassword)
            throw new InvalidOperationException("The new passwords do not match.");

        await userService.ChangePasswordAsync(userName, currentPassword, newPassword);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Redirect($"/ChangePassword?error={Uri.EscapeDataString(ex.Message)}");
    }

    // Issue a new cookie without the "must change password" flag.
    await SignInAsync(httpContext, userName, mustChangePassword: false);
    return Results.Redirect("/?passwordChanged=1");
});

app.MapPost("/account/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/Login");
});

// File upload endpoint used by HxInputFile (certificates). The browser posts it with the auth cookie.
app.MapPost("/upload", async ([FromForm] IFormFile file, IUploadService uploadService) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");

    var fileId = await uploadService.SaveFileAsync(file);

    return Results.Ok(fileId);
}).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(5 * 1024 * 1024));

// Files for the send queue; streamed to the outbox directory, so they may be large.
var maxOutboxFileSize = app.Configuration.GetValue("Upload:MaxOutboxFileSizeMB", 512L) * 1024 * 1024;
app.MapPost("/upload/outbox", async ([FromForm] IFormFile file, OutboxStorage outbox, CancellationToken cancellationToken) =>
{
    if (file.Length == 0)
        return Results.BadRequest("The file is empty.");

    var path = await outbox.SaveAsync(file, cancellationToken);
    app.Logger.LogInformation("File {FileName} ({Size} bytes) uploaded to {Path}", file.FileName, file.Length, path);
    return Results.Ok(path);
}).DisableAntiforgery()
  .WithMetadata(new RequestSizeLimitAttribute(maxOutboxFileSize),
      new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxOutboxFileSize });

// Public part of a stored certificate (PEM), e.g. to send our certificate to a partner.
app.MapGet("/certificates/{id:int}/download", async (int id, ICertificateRepository certificates) =>
{
    var certificate = (await certificates.GetAllAsync()).FirstOrDefault(c => c.Id == id);
    if (certificate is null)
        return Results.NotFound();

    using var x509 = CertificateLoader.Load(certificate);
    var fileName = (x509.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } cn ? cn : $"certificate-{id}") + ".crt";
    return Results.File(Encoding.ASCII.GetBytes(x509.ExportCertificatePem() + "\n"), "application/x-pem-file", fileName);
});

app.MapGet("/received/{id:int}/download", async (int id, IDataService dataService) =>
{
    var file = await dataService.GetReceivedFileAsync(id);
    if (file is null || !File.Exists(file.FilePath))
        return Results.NotFound();

    return Results.File(Path.GetFullPath(file.FilePath), "application/octet-stream", file.VirtualFileName);
});

app.Run();

static async Task SignInAsync(HttpContext httpContext, string userName, bool mustChangePassword)
{
    List<Claim> claims = [new(ClaimTypes.Name, userName)];
    if (mustChangePassword)
        claims.Add(new Claim(AuthClaims.MustChangePassword, "true"));

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
}
