using QualityAviationCodeRepositoryManagmentApi.API.Services;
using QualityAviationCodeRepositoryManagmentApi.API.Middleware;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 200 * 1024 * 1024;
});
builder.Services.AddControllers();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowConsole", policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
builder.Services.AddScoped<IRepositoryService, RepositoryService>();
builder.Services.AddScoped<IFileService, FileService>();
builder.Services.AddScoped<IVersionService, VersionService>();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseCors("AllowConsole");
app.UseMiddleware<AuthenticationMiddleware>();
app.MapControllers();

app.Run();
