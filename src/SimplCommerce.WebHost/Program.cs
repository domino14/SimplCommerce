using System;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.WebEncoders;
using Microsoft.OpenApi.Models;
using SimplCommerce.Infrastructure;
using SimplCommerce.Infrastructure.Data;
using SimplCommerce.Infrastructure.Modules;
using SimplCommerce.Infrastructure.Web;
using SimplCommerce.Module.Core.Data;
using SimplCommerce.Module.Core.Extensions;
using SimplCommerce.Module.Localization.Extensions;
using SimplCommerce.Module.Localization.TagHelpers;
using SimplCommerce.WebHost.Extensions;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;

var serviceName = "SimplCommerce.CesarsFork";
var serviceVersion = "1.0.0";

var builder = WebApplication.CreateBuilder(args);
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

#if DEBUG
Console.WriteLine("Build Configuration: Debug");
#else
Console.WriteLine("Build Configuration: Release");
#endif


ConfigureService();
var app = builder.Build();
Configure();
app.Run();


void ConfigureService()
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    builder.Configuration.AddEntityFrameworkConfig(options =>
    {
        options.UseNpgsql(connectionString);
    });

    GlobalConfiguration.WebRootPath = builder.Environment.WebRootPath;
    GlobalConfiguration.ContentRootPath = builder.Environment.ContentRootPath;


    builder.Services.AddOpenTelemetry()
      .WithTracing(b =>
      {
          b
          .AddSource(serviceName)
          .ConfigureResource(resource =>
              resource.AddService(
                  serviceName: serviceName,
                  serviceVersion: serviceVersion))
          .AddAspNetCoreInstrumentation()
          .AddConsoleExporter();
      });

    builder.Services.AddModules();
    builder.Services.AddCustomizedDataStore(builder.Configuration);
    builder.Services.AddCustomizedIdentity(builder.Configuration);
    builder.Services.AddHttpClient();
    builder.Services.AddTransient(typeof(IRepository<>), typeof(Repository<>));
    builder.Services.AddTransient(typeof(IRepositoryWithTypedId<,>), typeof(RepositoryWithTypedId<,>));
    builder.Services.AddScoped<SlugRouteValueTransformer>();

    builder.Services.AddCustomizedLocalization();

    builder.Services.AddCustomizedMvc(GlobalConfiguration.Modules);
    builder.Services.Configure<RazorViewEngineOptions>(
        options => { options.ViewLocationExpanders.Add(new ThemeableViewLocationExpander()); });
    builder.Services.Configure<WebEncoderOptions>(options =>
    {
        options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All);
    });
    builder.Services.AddScoped<ITagHelperComponent, LanguageDirectionTagHelperComponent>();
    builder.Services.AddTransient<IRazorViewRenderer, RazorViewRenderer>();
    builder.Services.AddAntiforgery(options => options.HeaderName = "X-XSRF-Token");
    builder.Services.AddCloudscribePagination();

    foreach (var module in GlobalConfiguration.Modules)
    {
        var moduleInitializerType = module.Assembly.GetTypes()
           .FirstOrDefault(t => typeof(IModuleInitializer).IsAssignableFrom(t));
        if ((moduleInitializerType != null) && (moduleInitializerType != typeof(IModuleInitializer)))
        {
            var moduleInitializer = (IModuleInitializer)Activator.CreateInstance(moduleInitializerType);
            builder.Services.AddSingleton(typeof(IModuleInitializer), moduleInitializer);
            moduleInitializer.ConfigureServices(builder.Services);
        }
    }

    builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "SimplCommerce API", Version = "v1" });
    });
}

void Configure()
{
    if (app.Environment.IsDevelopment())
    {
        // app.UseDeveloperExceptionPage();
        app.UseMigrationsEndPoint();
    }
    else
    {
        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase),
            a => a.UseExceptionHandler("/Home/Error")
        );
        app.UseHsts();
    }
    app.UseMiddleware<CodeComet.CaptureMiddleware>(
        "cc-e23d00ac-d60bd829d922cd9f4c068b218a431339",  // api key
        "a63069c8-ffc0-4d87-a30f-f27b4b212d71",   // project ID
        false,  // send all traffic, defaults to false.
        "http://localhost/api/trafficconsumer.TrafficService/IngestTrafficLog" // defaults to the prod URL
    );

    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase),
        a => a.UseStatusCodePagesWithReExecute("/Home/ErrorWithCode/{0}")
    );

    app.UseHttpsRedirection();
    app.UseCustomizedStaticFiles(builder.Environment);
    app.UseRouting();
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "SimplCommerce API V1");
    });
    app.UseCookiePolicy();
    app.UseCustomizedIdentity();
    app.UseCustomizedRequestLocalization();
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapDynamicControllerRoute<SlugRouteValueTransformer>("/{**slug}");
        endpoints.MapControllerRoute(
            name: "areas",
            pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");
        endpoints.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}");
    });

    var moduleInitializers = app.Services.GetServices<IModuleInitializer>();
    foreach (var moduleInitializer in moduleInitializers)
    {
        moduleInitializer.Configure(app, builder.Environment);
    }
}


public static class Telemetry
{
    //...

    // Name it after the service name for your app.
    // It can come from a config file, constants file, etc.
    public static readonly ActivitySource MyActivitySource = new("SimplCommerce.CesarsFork");

    //...
}


// public class ErrorLoggingMiddleware
// {
//     private readonly RequestDelegate _next;

//     public ErrorLoggingMiddleware(RequestDelegate next)
//     {
//         _next = next;
//     }

//     public async Task Invoke(HttpContext context)
//     {
//         try
//         {
//             await _next(context);
//         }
//         catch (Exception ex)
//         {
//             var currentSpan = OpenTelemetry.Trace.Tracer.CurrentSpan;
//             if (currentSpan.IsRecording)
//             {
//                 currentSpan.RecordException(ex);
//                 currentSpan.SetStatus(Status.Error.WithDescription(ex.Message));
//             }

//             // Rethrow the exception to maintain the normal flow of exception handling.
//             throw;
//         }
//     }
// }

