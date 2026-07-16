using Ttds.Shared;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

var url = "http://127.0.0.1:5050";

builder.WebHost.UseUrls(url);

builder.Services.AddControllersWithViews();

builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 1_500_000_000;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 1_500_000_000;
});
builder.Services.Configure<ServiceOptions>(
    builder.Configuration.GetSection(ServiceOptions.SectionName)
);
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapGet("/", () => Results.Redirect("/gpx/upload"));

app.Run();