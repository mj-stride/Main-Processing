using Ttds.Shared;
using TtdsWeb.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<AppStateStore>();
builder.Services.AddScoped<IAppStateAccessor, AppStateAccessor>();
builder.Services.AddScoped<ICsvExportService, CsvExportService>();
builder.Services.AddScoped<IGisExportService, GisExportService>();
builder.Services.AddScoped<ITripAnalysisService, TripAnalysisService>();
builder.Services.AddScoped<IPeakPeriodService, PeakPeriodService>();
builder.Services.AddScoped<IGeoDirectionService, GeoDirectionService>();
builder.Services.AddScoped<IKmPostRepositoryService, KmPostRepositoryService>();
builder.Services.AddScoped<IAnchorDetectionService, AnchorDetectionService>();
builder.Services.AddHostedService<AppStateCleanupService>();
builder.Services.AddScoped<IZipPackagingService, ZipPackagingService>();

builder.Services.Configure<ServiceOptions>(
    builder.Configuration.GetSection(ServiceOptions.SectionName)
);

builder.Services.AddDistributedMemoryCache(); // required by AddSession
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(4);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

var app = builder.Build();

app.UseStaticFiles();
app.UseSession();
app.UseRouting();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();



