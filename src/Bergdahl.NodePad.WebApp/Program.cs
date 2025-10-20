using Bergdahl.NodePad.WebApp;
using Bergdahl.NodePad.WebApp.Logging;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers(options =>
{
    options.InputFormatters.Add(new TextPlainInputFormatter());
});

// Lägg till logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Bind file logger options and register file logger provider via DI (avoid BuildServiceProvider)
builder.Services.Configure<FileLoggerOptions>(builder.Configuration.GetSection("Logging:File"));
builder.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>();

var app = builder.Build();

// Lägg till felhantering
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Serve PagesDirectory under /pages for embedded assets (images placed next to markdown files)
var pagesDir = builder.Configuration.GetValue<string>("PagesDirectory") ?? Path.Combine(Directory.GetCurrentDirectory(), "Pages");
var pagesFullPath = Path.GetFullPath(pagesDir);
if (!Directory.Exists(pagesFullPath))
{
    Directory.CreateDirectory(pagesFullPath);
}
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(pagesFullPath),
    RequestPath = "/pages",
});

app.MapControllers();

app.Run();