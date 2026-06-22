using Report_Generator.Services;
using System.Text;
Console.OutputEncoding = Encoding.UTF8;
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddScoped<ChartGeneratorService>();
builder.Services.AddScoped<CsvExportService>();
builder.Services.AddScoped<CsvParserService>();
builder.Services.AddScoped<DataProcessorService>();
builder.Services.AddScoped<FolderScannerService>();
builder.Services.AddScoped<WordExportService>();
builder.Services.AddScoped<TripLineLoaderService>();
builder.Services.AddScoped<SpeedSegmentService>();
builder.Services.AddScoped<SpeedMapRenderer>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy => policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    //app.UseSwagger();
    //app.UseSwaggerUI();
}


app.UseRouting();

app.UseCors("AllowAll");
app.UseHttpsRedirection();
app.UseAuthorization();

// Conventional MVC routing — used by HomeController (e.g. /Home/ReportGenerator).
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=ReportGenerator}/{id?}");

// Attribute routing — used by ReportController ([Route("api/[controller]")]).
// Both can coexist: this matches anything the convention route above doesn't.
app.MapControllers();

app.Run();