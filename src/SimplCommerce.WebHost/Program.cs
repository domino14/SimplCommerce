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
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

using System.Net.Http;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text;

var serviceName = "SimplCommerce.CesarsFork";
var serviceVersion = "1.0.0";

var builder = WebApplication.CreateBuilder(args);
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

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
    app.UseMiddleware<RequestResponseLoggingMiddleware>();

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



public class RequestResponseLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HttpClient _httpClient;
    private const string ApiKey = "sOME_API_KEY";
    private const string ExternalServerUrl = "https://eo5baqp3ugpiwna.m.pipedream.net";

    public RequestResponseLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
        _httpClient = new HttpClient();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var staticExtensions = new HashSet<string> { ".js", ".css", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico" };
        if (staticExtensions.Any(ext => context.Request.Path.Value.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            // It's a static file request, so just continue to the next middleware
            await _next(context);
        }
        else
        {
            var requestData = new Dictionary<string, object>();
            var originalBodyStream = context.Response.Body;

            using (var responseBody = new MemoryStream())
            {
                context.Response.Body = responseBody;

                try
                {
                    // Collect request and response details
                    await CollectRequestData(context, requestData);
                    await _next(context);

                    context.Response.Body.Seek(0, SeekOrigin.Begin);
                    var responseBodyText = await new StreamReader(context.Response.Body).ReadToEndAsync();
                    context.Response.Body.Seek(0, SeekOrigin.Begin);

                    await responseBody.CopyToAsync(originalBodyStream);
                    // collect response data
                    requestData["ResponseBody"] = responseBodyText;
                    requestData["ResponseStatusCode"] = context.Response.StatusCode;
                }
                catch (Exception ex)
                {
                    if (context.Response.StatusCode == 200 || context.Response.StatusCode == 0)
                    {
                        context.Response.StatusCode = 500;
                    }
                    requestData["ResponseStatusCode"] = context.Response.StatusCode;
                    HandleException(context, ex, requestData);
                    throw;
                }
                finally
                {
                    context.Response.Body = originalBodyStream;
                    // Send data to external server
                    await SendDataToExternalServer(requestData);
                }
            }
        }
    }

    private async Task CollectRequestData(HttpContext context, Dictionary<string, object> data)
    {
        var request = context.Request;

        // Ensure the request body can be read multiple times
        request.EnableBuffering();

        // Read the request body
        var body = string.Empty;
        var requestBodyStream = new MemoryStream();
        await request.Body.CopyToAsync(requestBodyStream);
        requestBodyStream.Seek(0, SeekOrigin.Begin);

        using (var reader = new StreamReader(requestBodyStream))
        {
            body = await reader.ReadToEndAsync();
            // Reset the stream so that it can be read again later
            request.Body.Seek(0, SeekOrigin.Begin);
        }


        data["Method"] = request.Method;
        data["Path"] = request.Path;
        data["QueryString"] = request.QueryString.ToString();
        data["Headers"] = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());
        data["RequestBody"] = body;

    }

    private void HandleException(HttpContext context, Exception exception, Dictionary<string, object> data)
    {
        data["ExceptionMessage"] = exception.Message;
        data["StackTrace"] = exception.StackTrace;
    }

    private async Task SendDataToExternalServer(Dictionary<string, object> data)
    {
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
            var response = await _httpClient.PostAsync(ExternalServerUrl, content);

            // Check the response
            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Failed to log data: {response.StatusCode} - {responseContent}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending data to external server: {ex.Message}");
        }

    }
}