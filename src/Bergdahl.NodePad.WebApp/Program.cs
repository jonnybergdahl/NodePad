using Bergdahl.NodePad.WebApp;
using Bergdahl.NodePad.WebApp.Logging;

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
app.MapControllers();

app.Run();