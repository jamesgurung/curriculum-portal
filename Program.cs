using CurriculumPortal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var isProduction = !builder.Environment.IsDevelopment();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
  options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
  options.KnownIPNetworks.Clear();
  options.KnownProxies.Clear();
});

var appOptions = builder.Configuration.Get<AppOptions>();
appOptions.Validate();
builder.Services.AddSingleton(appOptions);

builder.Services.AddDataProtection().PersistKeysToAzureBlobStorage(new Uri(appOptions.DataProtectionBlobUri));

var configService = new ConfigService(appOptions);
await configService.LoadAsync();

builder.Services.AddSingleton(configService);
builder.Services.AddSingleton<CourseService>();
builder.Services.AddSingleton<AIService>();
builder.Services.AddSingleton<AssignmentService>();
builder.Services.AddSingleton<CacheService>();
builder.Services.AddScoped<IRazorViewRenderer, RazorViewRenderer>();
builder.Services.AddScoped<EmailTemplateService>();
builder.Services.AddSingleton<ServiceAccountAuthService>();
builder.Services.AddSingleton<MailService>();
builder.Services.AddSingleton<TeamsService>();
builder.Services.AddSingleton<AssignmentSettingService>();

builder.Services.AddHostedService(provider => provider.GetRequiredService<AssignmentSettingService>());

builder.ConfigureAuth(configService);
builder.Services.AddResponseCompression(options => { options.EnableForHttps = isProduction; });
builder.Services.AddAntiforgery(options => { options.HeaderName = "X-CSRF-TOKEN"; });
builder.Services.Configure<RouteOptions>(options => { options.LowercaseUrls = true; });
builder.Services.Configure<JsonOptions>(options => { options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; });
builder.Services.AddRazorPages();

builder.Services.AddWebOptimizer(pipeline =>
{
  if (isProduction)
  {
    pipeline.MinifyCssFiles("css/*.css");
    pipeline.MinifyJsFiles("js/*.js");
  }
});

var app = builder.Build();

if (isProduction)
{
  app.UseExceptionHandler("/error");
  app.UseHsts();
}

app.UseForwardedHeaders();
app.UseResponseCompression();
app.UseHttpsRedirection();
app.UseWebOptimizer();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapAuthPaths();
app.MapApiPaths();
app.MapRazorPages();

await app.RunAsync();
