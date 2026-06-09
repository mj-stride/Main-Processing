using Microsoft.AspNetCore.Http.Features;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

var url = "http://127.0.0.1:5050";
var openUrl = "http://127.0.0.1:5050/gpx/upload";

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

var app = builder.Build();

app.Lifetime.ApplicationStarted.Register(() =>
{
    Process.Start(new ProcessStartInfo
    {
        FileName = openUrl,
        UseShellExecute = true
    });
});

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

app.Run();