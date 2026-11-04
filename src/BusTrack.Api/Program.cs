using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using BusTrack.Api.Auth;
using BusTrack.Api.Data;
using BusTrack.Api.Hubs;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer")));
builder.Services.AddScoped<TrackingService>();
builder.Services.AddHttpClient<RoadRouter>(client =>
{
    client.BaseAddress = new Uri("https://router.project-osrm.org/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("WhereIsMyBus/1.0 (local depot)");
});
builder.Services.AddScoped<RouteGeometryService>();
builder.Services.AddSingleton<TelemetryStateStore>();
builder.Services.AddScoped<TelemetryService>();
builder.Services.AddSingleton<PasswordHasher<User>>();
builder.Services.AddSingleton<JwtIssuer>();
builder.Services.AddHostedService<TripSimulator>();
builder.Services.AddSignalR();
builder.Services.AddControllers();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("auth-limit", opt =>
    {
        opt.PermitLimit = 10;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
});

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "Enter your JWT token. Example: Bearer eyJhbGciOi...",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });

    options.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
    options.CustomSchemaIds(type => type.FullName?.Replace("+", ".") ?? type.Name);
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
        else if (builder.Environment.IsDevelopment())
        {
            policy.AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowAnyOrigin();
        }
        else
        {
            policy.WithOrigins("http://localhost:4317", "https://whereismybus.local")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
    });
});

var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is missing.");
if (!builder.Environment.IsDevelopment() && jwtKey.Contains("Change-In-Production", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Default development JWT signing key detected in production. Configure a secure Jwt:Key in production settings or environment variables.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var idStr = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (Guid.TryParse(idStr, out var userId))
                {
                    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
                    if (user is null || !user.IsActive)
                    {
                        context.Fail("User account is inactive or disabled.");
                    }
                }
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services);

using (var scope = app.Services.CreateScope())
{
    var tracking = scope.ServiceProvider.GetRequiredService<TrackingService>();
    if (await tracking.OperationsAreLive())
        await tracking.MaterializeToday();
}

app.UseExceptionHandler(handler =>
{
    handler.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Errors");
        logger.LogError(feature?.Error, "Unhandled exception");
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { message = "Something went wrong on the server." });
    });
});

// Security headers
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    if (!app.Environment.IsDevelopment())
    {
        context.Response.Headers.Append("Content-Security-Policy", "default-src 'self'; frame-ancestors 'none';");
        context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
    }
    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<TrackingHub>("/hubs/tracking");
app.MapGet("/api/health", async (AppDbContext db) =>
{
    var can = await db.Database.CanConnectAsync();
    var ops = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "OperationsLive");
    return Results.Ok(new
    {
        status = can ? "ok" : "degraded",
        database = can,
        operationsLive = string.Equals(ops?.Value, "true", StringComparison.OrdinalIgnoreCase)
    });
});

app.Run();
