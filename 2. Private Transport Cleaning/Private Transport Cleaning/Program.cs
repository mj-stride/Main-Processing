using Microsoft.AspNetCore.Http.Features;
using PrivateTransportCleaning.Services;

var builder = WebApplication.CreateBuilder(args);

// =========================
// KESTREL LIMIT (UPLOAD SIZE)
// =========================
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = null; // NO LIMIT
});

// =========================
// FORM LIMIT (MULTIPART)
// =========================
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = long.MaxValue;
});

// =========================
// MVC
// =========================
builder.Services.AddControllersWithViews();

// =========================
// SERVICE REGISTRATION
// =========================
builder.Services.AddScoped<ZipProcessingService>();
builder.Services.AddScoped<GpxProcessingService>();
builder.Services.AddScoped<GpxParserService>();
builder.Services.AddScoped<SnappingService>();
builder.Services.AddScoped<GeoUtilityService>();
builder.Services.AddScoped<KilometerPostService>();
builder.Services.AddScoped<RegionRoadDetectionService>();
builder.Services.AddScoped<FileNamingService>();
builder.Services.AddScoped<CsvExportService>();

// =========================
// BUILD APP
// =========================
var app = builder.Build();

// =========================
// PIPELINE
// =========================
app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=SurveyData}/{action=Index}/{id?}");

app.Run();