var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers(options =>
{
    options.InputFormatters.Add(new TextPlainInputFormatter());
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.Run();
