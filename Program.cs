using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using BromcomEssentials;
using CurriculumPortal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using System.Globalization;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var appConfigEndpoint = builder.Configuration["AppConfigurationEndpoint"];
var appConfigConnectionString = builder.Configuration.GetConnectionString("AppConfiguration");
if (appConfigEndpoint is not null || appConfigConnectionString is not null)
{
  builder.Configuration.AddAzureAppConfiguration(options =>
  {
    if (appConfigEndpoint is not null)
    {
      options.Connect(new Uri(appConfigEndpoint), new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
    }
    else
    {
      options.Connect(appConfigConnectionString);
    }
    options
      .Select("Shared:*")
      .Select("Bromcom:*")
      .Select("CurriculumPortal:*")
      .TrimKeyPrefix("Shared:")
      .TrimKeyPrefix("Bromcom:")
      .TrimKeyPrefix("CurriculumPortal:");
  });
}

var isProduction = !builder.Environment.IsDevelopment();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
  options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
  options.KnownIPNetworks.Clear();
  options.KnownProxies.Clear();
});

var appOptions = builder.Configuration.Get<AppOptions>();
appOptions.Validate();
builder.Services.AddSingleton(appOptions);

AppContext.SetSwitch("OpenAI.DisableTelemetry", true);

builder.Services.AddHttpClient();
var blobServiceClient = new BlobServiceClient(appOptions.StorageAccountConnectionString);
var tableServiceClient = new TableServiceClient(appOptions.StorageAccountConnectionString);
builder.Services.AddSingleton(blobServiceClient);
builder.Services.AddSingleton(tableServiceClient);
builder.Services.AddDataProtection().PersistKeysToAzureBlobStorage(new Uri(appOptions.DataProtectionBlobUri));

var configService = new ConfigService(appOptions, blobServiceClient);
await configService.LoadAsync();

builder.Services.AddSingleton(configService);
RegisterBehaviourRecordService(builder.Services, appOptions);
builder.Services.AddSingleton<CourseService>();
builder.Services.AddSingleton<XpService>();
builder.Services.AddSingleton<BonusQuizService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<AITokenBudgetService>();
builder.Services.AddSingleton<AIService>();
builder.Services.AddSingleton<AssignmentService>();
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<CourseEvaluationService>();
builder.Services.AddScoped<IRazorViewRenderer, RazorViewRenderer>();
builder.Services.AddScoped<EmailTemplateService>();
builder.Services.AddSingleton<ServiceAccountAuthService>();
builder.Services.AddSingleton<MailService>();
builder.Services.AddSingleton<TeamsService>();
builder.Services.AddSingleton<AssignmentAutomationService>();

builder.Services.AddHostedService(provider => provider.GetRequiredService<AssignmentAutomationService>());
builder.Services.AddHostedService<CourseEvaluationAutomationService>();

builder.ConfigureAuth(configService, appOptions);
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

static void RegisterBehaviourRecordService(IServiceCollection services, AppOptions options)
{
  var bromcomSettingsCount = CountConfigured(options.BromcomApplicationId, options.BromcomApplicationSecret, options.BromcomSchoolId);
  var classChartsSettingsCount = CountConfigured(options.ClassChartsEmail, options.ClassChartsPassword);
  var bromcomConfigured = bromcomSettingsCount == 3;
  var classChartsConfigured = classChartsSettingsCount == 2;

  if (bromcomSettingsCount is > 0 and < 3)
    throw new InvalidOperationException("Bromcom behaviour recording is partially configured. Configure BromcomApplicationId, BromcomApplicationSecret, and BromcomSchoolId, or remove all Bromcom settings.");

  if (classChartsSettingsCount is > 0 and < 2)
    throw new InvalidOperationException("Class Charts behaviour recording is partially configured. Configure ClassChartsEmail and ClassChartsPassword, or remove both Class Charts settings.");

  if (bromcomConfigured && classChartsConfigured)
    throw new InvalidOperationException("Only one behaviour recording provider can be configured. Remove either Bromcom settings or Class Charts settings.");

  if (bromcomConfigured)
  {
    if (!int.TryParse(options.BromcomSchoolId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bromcomSchoolId))
      throw new InvalidOperationException("Bromcom school ID must be a valid integer.");
    services.AddHttpClient<SchoolBromcomClient, SchoolBromcomClient>(httpClient => new(options.BromcomApplicationId, options.BromcomApplicationSecret, bromcomSchoolId, httpClient));
    services.AddSingleton<IBehaviourRecordService, BromcomService>();
    return;
  }

  if (classChartsConfigured)
  {
    services.AddSingleton<IBehaviourRecordService, ClassChartsService>();
    return;
  }

  services.AddSingleton<IBehaviourRecordService, NoOpBehaviourRecordService>();

  static int CountConfigured(params string[] values)
  {
    return values.Count(value => !string.IsNullOrWhiteSpace(value));
  }
}
