using Report_Generator.Services;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddScoped<ChartGeneratorService>();
builder.Services.AddScoped<CsvExportService>();
builder.Services.AddScoped<CsvParserService>();
builder.Services.AddScoped<DataProcessorService>();
builder.Services.AddScoped<FolderScannerService>();
builder.Services.AddScoped<WordExportService>();
builder.Services.AddScoped<TripLineLoaderService>();
builder.Services.AddScoped<SpeedSegmentService>();
builder.Services.AddScoped<SpeedMapRenderer>();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
//builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy => policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());
});

var app = builder.Build();

app.UseDefaultFiles();

app.UseStaticFiles();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    //app.UseSwagger();
    //app.UseSwaggerUI();
}

app.UseCors("AllowAll");

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
