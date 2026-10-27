using System.Reflection;
using CleanArchitecture.Application;
using CleanArchitecture.Infrastructure;
using CleanArchitecture.Infrastructure.Persistence;
using CleanArchitecture.Presentation.Extensions;
using CleanArchitecture.Presentation.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddEndpoints(Assembly.GetExecutingAssembly());

builder.Services.AddAuthenticationAndAuthorization();

builder.Services.AddApiDocumentation();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

var isRunningAsTheEntryPoint = Assembly.GetEntryAssembly() == typeof(Program).Assembly;

// Lets a host without a database (e.g. a deployment smoke environment) skip startup migrations.
var applyMigrationsOnStartup = app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", defaultValue: true);

if (isRunningAsTheEntryPoint && applyMigrationsOnStartup)
{
    await app.Services.ApplyPendingMigrationsAsync();
}

app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

app.MapApi();

app.Run();

public partial class Program;
