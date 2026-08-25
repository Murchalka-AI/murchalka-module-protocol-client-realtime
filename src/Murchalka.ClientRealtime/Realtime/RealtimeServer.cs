using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Murchalka.ClientRealtime.Runtime;

namespace Murchalka.ClientRealtime.Realtime;

internal sealed class RealtimeServer : IAsyncDisposable
{
    private readonly ModuleConnection _connection;
    private WebApplication? _application;

    public RealtimeServer(ModuleConnection connection) => _connection = connection;

    public async Task StartAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (_application is not null)
        {
            return;
        }

        if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("Realtime endpoint must be an explicit HTTP loopback address.");
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(endpoint.ToString().TrimEnd('/'));
        builder.Services.AddSingleton(_connection);
        builder.Services.AddSingleton<WebSocketConnectionHandler>();
        var application = builder.Build();
        application.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        application.MapGet("/health", () => Results.Ok(new { status = "ready" }));
        application.Map("/v1/realtime", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await context.RequestServices.GetRequiredService<WebSocketConnectionHandler>()
                .RunAsync(socket, context.RequestAborted).ConfigureAwait(false);
        });
        await application.StartAsync(cancellationToken).ConfigureAwait(false);
        _application = application;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_application is null)
        {
            return;
        }

        await _application.StopAsync(cancellationToken).ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        _application = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is not null)
        {
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }
}
