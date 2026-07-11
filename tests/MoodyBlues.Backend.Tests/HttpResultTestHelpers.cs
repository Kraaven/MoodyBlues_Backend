using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MoodyBlues.Backend.Tests;

/// <summary>Executes a minimal-API <see cref="IResult"/> against a fake <see cref="HttpContext"/> and captures status/body -- avoids needing a full running host to test endpoint handlers.</summary>
public static class HttpResultTestHelpers
{
    // Results.Ok/BadRequest resolve JsonOptions from HttpContext.RequestServices -- a real host wires this up,
    // so a fake HttpContext needs an equivalent minimal service provider.
    private static readonly IServiceProvider MinimalRequestServices = new ServiceCollection()
        .AddSingleton(Options.Create(new JsonOptions()))
        .AddLogging()
        .BuildServiceProvider();

    public static async Task<(int StatusCode, string Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext { RequestServices = MinimalRequestServices };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        string body = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }

    public static async Task<T?> ExecuteAndDeserializeAsync<T>(IResult result)
    {
        (int _, string body) = await ExecuteAsync(result);
        return string.IsNullOrEmpty(body)
            ? default
            : JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
