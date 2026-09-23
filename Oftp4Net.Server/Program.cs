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
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
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
using Oftp4Net.Services.Health;
using Oftp4Net.Services.Hooks;
using Oftp4Net.Services.Import;
using Oftp4Net.Services.Pdx;
using Oftp4Net.Services.Retention;
using Oftp4Net.Services.Certificates;
using Oftp4Net.Services.Security;
using Oftp4Net.Services.Tsl;
using Oftp4Net.Services.TransferEvents;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Serilog;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

// Values that must not go into the repository, e.g. the client secret of Entra ID: appsettings.{Environment}.local.json
// is read here (.gitignore knows it) and in development also the user secrets of the .NET tooling, which live in the
// profile of the user. The environment variables are added again afterwards, so that they keep the last word.
builder.Configuration
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

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

builder.Services.AddOpenApi(options =>
{
    // Describe the bearer authentication of the REST API, so it can be used from the documentation.
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info = new OpenApiInfo
        {
            Title = "Oftp4Net REST API",
            Version = "v1",
            Description = "Sending and receiving OFTP files. Authenticate with a token from Settings / API tokens.",
        };
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "Token created in Settings / API tokens.",
        };
        document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
        return Task.CompletedTask;
    });
});

// Signing in with Microsoft Entra ID is optional: without the configuration section the password login is the
// only way in, and it stays available in any case, so that a wrong tenant cannot lock the administrator out.
var entra = builder.Configuration.GetSection(EntraOptions.SectionName).Get<EntraOptions>() ?? new EntraOptions();
builder.Services.AddSingleton(entra);

var authentication = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
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

if (entra.IsConfigured)
{
    authentication.AddOpenIdConnect(EntraOptions.SchemeName, options =>
    {
        options.Authority = entra.AuthorityUrl;
        options.ClientId = entra.ClientId;
        options.ClientSecret = entra.ClientSecret;
        options.CallbackPath = entra.CallbackPath;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.SaveTokens = false;
        options.Scope.Add("email");
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.Events.OnTokenValidated = OnEntraTokenValidatedAsync;
        options.Events.OnRemoteFailure = context =>
        {
            context.Response.Redirect("/Login?error=entra&message=" +
                                      Uri.EscapeDataString(context.Failure?.Message ?? "The sign in was not completed."));
            context.HandleResponse();
            return Task.CompletedTask;
        };
    });
}
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
builder.Services.AddScoped<Os4xPartnerImporter>();
builder.Services.AddScoped<PdxImporter>();
builder.Services.AddScoped<PartnerSetupService>();
builder.Services.AddScoped<PdxExporter>();
builder.Services.AddScoped<CertificateSigningRequestService>();
builder.Services.AddScoped<CertificateExchangeService>();
builder.Services.AddSingleton<PartnerSetupScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PartnerSetupScheduler>());
builder.Services.AddHttpClient(WebhookDispatcher.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient(TslService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient(CrlStore.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<CrlStore>();
builder.Services.AddSingleton<TslService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TslService>());
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
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());
builder.Services.AddOftpHealthChecks();

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
    // Probes of the orchestrator come over plain HTTP from inside and do not follow a redirect.
    app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), branch => branch.UseHttpsRedirection());
}

// Fingerprinted static files (no stale CSS after an update); they are public, unlike the rest of the app.
app.MapStaticAssets().AllowAnonymous();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapApi();
app.MapHealth();

// API description and its documentation; both require a signed in administrator (fallback policy).
app.MapOpenApi();
app.MapScalarApiReference("/ApiReference", options => options
    .WithTitle("Oftp4Net REST API")
    .WithOpenApiRoutePattern("/openapi/{documentName}.json")
    .AddPreferredSecuritySchemes("Bearer"));

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
    return Results.LocalRedirect(LocalPath("/" + (returnUrl ?? "").TrimStart('/')));
}).AllowAnonymous().ExcludeFromDescription();

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
}).ExcludeFromDescription();

// Hands the user over to Entra ID; the answer comes back to the callback path of the scheme.
app.MapGet("/account/entra-login", (HttpContext httpContext, EntraOptions options, [FromQuery] string? returnUrl) =>
{
    if (!options.IsConfigured)
        return Results.Redirect("/Login");

    var target = "/" + (returnUrl ?? "").TrimStart('/');
    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = LocalPath(target) },
        [EntraOptions.SchemeName]);
}).AllowAnonymous().ExcludeFromDescription();

app.MapPost("/account/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/Login");
}).ExcludeFromDescription();

// File upload endpoint used by HxInputFile (certificates). The browser posts it with the auth cookie.
app.MapPost("/upload", async ([FromForm] IFormFile file, IUploadService uploadService) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");

    var fileId = await uploadService.SaveFileAsync(file);

    return Results.Ok(fileId);
}).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(5 * 1024 * 1024)).ExcludeFromDescription();

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
      new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxOutboxFileSize })
  .ExcludeFromDescription();

// Public part of a stored certificate (PEM), e.g. to send our certificate to a partner.
// A certificate signing request (PEM) to be submitted to a certification authority.
app.MapGet("/certificate-requests/{id:int}/csr", async (int id, ICertificateSigningRequestRepository requests) =>
{
    var request = await requests.GetObjectAsync(id);
    var fileName = string.Concat(request.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_')) + ".csr";
    return Results.File(System.Text.Encoding.ASCII.GetBytes(request.Csr), "application/pkcs10", fileName);
}).ExcludeFromDescription();

app.MapGet("/certificates/{id:int}/download", async (int id, ICertificateRepository certificates) =>
{
    var certificate = (await certificates.GetAllAsync()).FirstOrDefault(c => c.Id == id);
    if (certificate is null)
        return Results.NotFound();

    using var x509 = CertificateLoader.Load(certificate);
    var fileName = (x509.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } cn ? cn : $"certificate-{id}") + ".crt";
    return Results.File(Encoding.ASCII.GetBytes(x509.ExportCertificatePem() + "\n"), "application/x-pem-file", fileName);
}).ExcludeFromDescription();

// Our OFTP2 Communication Setup of an identity, e.g. to send it to a new partner by e-mail.
app.MapGet("/pdx/export/{identityId:int}", async (int identityId, DateTimeOffset? validFrom, string? version, PdxExporter exporter) =>
{
    var export = await exporter.ExportAsync(identityId, validFrom, version ?? "1.2");
    return Results.File(export.Content, "application/xml", export.FileName);
}).ExcludeFromDescription();

app.MapGet("/received/{id:int}/download", async (int id, IDataService dataService) =>
{
    var file = await dataService.GetReceivedFileAsync(id);
    if (file is null || !File.Exists(file.FilePath))
        return Results.NotFound();

    return Results.File(Path.GetFullPath(file.FilePath), "application/octet-stream", file.VirtualFileName);
}).ExcludeFromDescription();

app.Run();

/// <summary>
/// An identity from Entra ID is let in when a user with that e-mail address or user principal name is configured
/// here: Entra ID says who somebody is, the user list says who may come in. The session then runs under the local
/// user name, as it does after a password sign in.
/// </summary>
static async Task OnEntraTokenValidatedAsync(TokenValidatedContext context)
{
    var principal = context.Principal;
    var address = principal?.FindFirst("preferred_username")?.Value
                  ?? principal?.FindFirst(ClaimTypes.Email)?.Value
                  ?? principal?.FindFirst("upn")?.Value
                  ?? "";

    var services = context.HttpContext.RequestServices;
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Entra");

    if (address.Length == 0)
    {
        logger.LogWarning("Entra ID returned no e-mail address or user principal name");
        context.Fail("The answer of Entra ID contains no e-mail address.");
        return;
    }

    var user = await services.GetRequiredService<UserService>().SignInWithEntraAsync(address);
    if (user is null)
    {
        logger.LogWarning("{Address} signed in with Entra ID, but no user is configured for that address", address);
        context.Fail($"No user of Oftp4Net is configured for {address}.");
        return;
    }

    logger.LogInformation("User {UserName} signed in with Entra ID as {Address}", user.UserName, address);
    context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, user.UserName), new Claim(AuthClaims.SignedInWithEntra, "true")],
        CookieAuthenticationDefaults.AuthenticationScheme));
}

/// <summary>A relative path of this application, so that no sign in redirects somewhere else.</summary>
static string LocalPath(string path) =>
    Uri.IsWellFormedUriString(path, UriKind.Relative) && !path.StartsWith("//") ? path : "/";

static async Task SignInAsync(HttpContext httpContext, string userName, bool mustChangePassword)
{
    List<Claim> claims = [new(ClaimTypes.Name, userName)];
    if (mustChangePassword)
        claims.Add(new Claim(AuthClaims.MustChangePassword, "true"));

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
}
