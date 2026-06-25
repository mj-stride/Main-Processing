using Report_Generator.Services;
using System.Text;
Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// ---- Report generation services ----
builder.Services.AddScoped<ChartGeneratorService>();
builder.Services.AddScoped<CsvExportService>();
builder.Services.AddScoped<CsvParserService>();
builder.Services.AddScoped<DataProcessorService>();
builder.Services.AddScoped<FolderScannerService>();
builder.Services.AddScoped<WordExportService>();
builder.Services.AddScoped<TripLineLoaderService>();
builder.Services.AddScoped<SpeedSegmentService>();
builder.Services.AddScoped<SpeedMapRenderer>();
builder.Services.AddScoped<ReportProcessingService>();
builder.Services.AddScoped<ZipExtractService>();
builder.Services.AddScoped<ShapefileExportService>();

// ---- Job infrastructure ----
builder.Services.AddSingleton<ReportJobService>();
builder.Services.AddHostedService<ReportBackgroundService>();
builder.Services.Configure<ServiceOptions>(
    builder.Configuration.GetSection(ServiceOptions.SectionName)
);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy => policy
        .AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader());
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseCors("AllowAll");
app.UseHttpsRedirection();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=ReportGenerator}/{id?}");

app.MapControllers();

app.Run();

