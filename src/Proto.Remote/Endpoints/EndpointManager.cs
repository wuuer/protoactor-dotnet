// -----------------------------------------------------------------------
//   <copyright file="EndpointManager.cs" company="Asynkron AB">
//       Copyright (C) 2015-2024 Asynkron AB All rights reserved
//   </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Proto.Diagnostics;
using Proto.Mailbox;

namespace Proto.Remote;

public sealed class EndpointManager : IDiagnosticsProvider
{
    public const string ActivatorActorName = "$activator";

    private static readonly ILogger Logger = Log.CreateLogger<EndpointManager>();
    private readonly ConcurrentDictionary<string, DateTime> _blockedAddresses = new();
    private readonly ConcurrentDictionary<string, DateTime> _blockedClientSystemIds = new();
    private readonly IEndpoint _blockedEndpoint;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly IChannelProvider _channelProvider;
    private readonly ConcurrentDictionary<string, IEndpoint> _clientEndpoints = new();
    private readonly EventStreamSubscription<object>? _endpointTerminatedEvnSub;
    private readonly RemoteConfigBase _remoteConfig;
    private readonly ConcurrentDictionary<string, IEndpoint> _serverEndpoints = new();
    private readonly object _synLock = new();
    private readonly ActorSystem _system;

    public EndpointManager(ActorSystem system, RemoteConfigBase remoteConfig, IChannelProvider channelProvider)
    {
        _system = system;
        _system.ProcessRegistry.RegisterHostResolver(pid => new RemoteProcess(_system, this, pid));
        _remoteConfig = remoteConfig;
        _channelProvider = channelProvider;

        _endpointTerminatedEvnSub =
            _system.EventStream.Subscribe<EndpointTerminatedEvent>(OnEndpointTerminated, Dispatchers.DefaultDispatcher);

        _blockedEndpoint = new BlockedEndpoint(system);
        RemoteMessageHandler = new RemoteMessageHandler(this, _system, _remoteConfig.Serialization, _remoteConfig);
    }

    internal RemoteMessageHandler RemoteMessageHandler { get; }

    public CancellationToken CancellationToken => _cancellationTokenSource.Token;
    private PID? ActivatorPid { get; set; }

    public void Start() => SpawnActivator();

    public async Task StopAsync()
    {
        lock (_synLock)
        {            
            if (CancellationToken.IsCancellationRequested)
            {
                return;
            }

            Logger.LogDebug("[{SystemAddress}] Stopping", _system.Address);

            _system.EventStream.Unsubscribe(_endpointTerminatedEvnSub);

            _cancellationTokenSource.Cancel();
        }
        
        // release the lock while we dispose, other threads will see the cancellation token and return blocked endpoint.
        foreach (var endpoint in _serverEndpoints.Values)
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var endpoint in _clientEndpoints.Values)
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
        }

        _serverEndpoints.Clear();
        _clientEndpoints.Clear();

        StopActivator();

        Logger.LogDebug("[{SystemAddress}] Stopped", _system.Address);
    }

    private async Task OnEndpointTerminated(EndpointTerminatedEvent evt)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug("[{SystemAddress}] Endpoint {Address} terminating", _system.Address,
                evt.Address ?? evt.ActorSystemId);
        }

        Action? unblock = null;
        try
        {
            IEndpoint? endpoint = null;
            lock (_synLock)
            {
                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                if (evt.Address is not null && _serverEndpoints.TryRemove(evt.Address, out endpoint))
                {
                    _blockedAddresses.TryAdd(evt.Address, DateTime.UtcNow);
                    unblock = () => _blockedAddresses.TryRemove(evt.Address, out _);
                }
                else if (evt.ActorSystemId is not null && _clientEndpoints.TryRemove(evt.ActorSystemId, out endpoint))
                {
                    _blockedClientSystemIds.TryAdd(evt.ActorSystemId, DateTime.UtcNow);
                    unblock = () => _blockedClientSystemIds.TryRemove(evt.ActorSystemId, out _);
                }
            }

            if (endpoint != null)
            {
                // leave the lock to dispose the endpoint, so that requests can't build up behind the lock
                // the address will always be blocked while we dispose, at a minimum
                await endpoint.DisposeAsync().ConfigureAwait(false);

                Logger.LogInformation("[{SystemAddress}] Endpoint {Address} terminated", _system.Address,
                    evt.Address ?? evt.ActorSystemId);

                if (evt.ShouldBlock && _remoteConfig.WaitAfterEndpointTerminationTimeSpan.HasValue)
                {
                    await Task.Delay(_remoteConfig.WaitAfterEndpointTerminationTimeSpan.Value, CancellationToken).ConfigureAwait(false);
                }

            }
            else
            {
                Logger.LogDebug("[{SystemAddress}] Endpoint {Address} already removed.", _system.Address,
                    evt.Address ?? evt.ActorSystemId);
            }
        }
        catch (Exception ex)
        {
            // since these async EventStream subscription handlers are fire and forget, we need to
            // log if something goes wrong, or we'll never know
            Logger.LogError(ex, "[{SystemAddress}] Error during endpoint {Address} termination", _system.Address,
                evt.Address ?? evt.ActorSystemId);
        }
        finally
        {
            // make sure that the unblock action runs if it was set, or we can end up with a forever blocked address
            // which is bad if a new endpoint is started with the same address, or the same one restarts and reconnects
            unblock?.Invoke();
        }
    }

    internal IEndpoint GetOrAddServerEndpoint(string? address)
    {
        if (address is null)
        {
            Logger.LogError("[{SystemAddress}] Tried to get endpoint for null address", _system.Address);

            return _blockedEndpoint;
        }

        if (_cancellationTokenSource.IsCancellationRequested || _blockedAddresses.ContainsKey(address))
        {
            return _blockedEndpoint;
        }

        if (_serverEndpoints.TryGetValue(address, out var endpoint))
        {
            return endpoint;
        }

        lock (_synLock)
        {
            if (_cancellationTokenSource.IsCancellationRequested || _blockedAddresses.ContainsKey(address))
            {
                return _blockedEndpoint;
            }
            
            if (_serverEndpoints.TryGetValue(address, out endpoint))
            {
                return endpoint;
            }
            
            if (_system.Address.StartsWith(ActorSystem.Client, StringComparison.Ordinal))
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug("[{SystemAddress}] Requesting new client side ServerEndpoint for {Address}",
                        _system.Address, address);
                }

                endpoint = _serverEndpoints.GetOrAdd(address,
                    v => new ServerEndpoint(_system, _remoteConfig, v, _channelProvider,
                        ServerConnector.Type.ClientSide, RemoteMessageHandler));
            }
            else
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug("[{SystemAddress}] Requesting new server side ServerEndpoint for {Address}",
                        _system.Address, address);
                }

                endpoint = _serverEndpoints.GetOrAdd(address,
                    v => new ServerEndpoint(_system, _remoteConfig, v, _channelProvider,
                        ServerConnector.Type.ServerSide, RemoteMessageHandler));
            }

            return endpoint;
        }
    }

    internal IEndpoint GetOrAddClientEndpoint(string systemId)
    {
        if (systemId is null)
        {
            Logger.LogError("[{SystemAddress}] Tried to get endpoint for null systemId", _system.Address);

            return _blockedEndpoint;
        }

        if (_cancellationTokenSource.IsCancellationRequested || _blockedClientSystemIds.ContainsKey(systemId))
        {
            return _blockedEndpoint;
        }

        if (_clientEndpoints.TryGetValue(systemId, out var endpoint))
        {
            return endpoint;
        }

        lock (_synLock)
        {
            if (_cancellationTokenSource.IsCancellationRequested || _blockedClientSystemIds.ContainsKey(systemId))
            {
                return _blockedEndpoint;
            }
            
            if (_clientEndpoints.TryGetValue(systemId, out endpoint))
            {
                return endpoint;
            }

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("[{SystemAddress}] Requesting new ServerSideClientEndpoint for {SystemId}",
                    _system.Address, systemId);
            }

            return _clientEndpoints.GetOrAdd(systemId,
                address => new ServerSideClientEndpoint(_system, _remoteConfig, address));
        }
    }

    internal IEndpoint GetServerEndpoint(string address)
    {
        if (_cancellationTokenSource.IsCancellationRequested || _blockedAddresses.ContainsKey(address))
        {
            return _blockedEndpoint;
        }

        if (_serverEndpoints.TryGetValue(address, out var endpoint))
        {
            return endpoint;
        }

        return _blockedEndpoint;
    }

    internal IEndpoint GetClientEndpoint(string systemId)
    {
        if (_cancellationTokenSource.IsCancellationRequested || _blockedClientSystemIds.ContainsKey(systemId))
        {
            return _blockedEndpoint;
        }

        if (_clientEndpoints.TryGetValue(systemId, out var endpoint))
        {
            return endpoint;
        }

        return _blockedEndpoint;
    }

    private void SpawnActivator()
    {
        var props = Props.FromProducer(() => new Activator(_remoteConfig, _system));
        ActivatorPid = _system.Root.SpawnNamedSystem(props, ActivatorActorName);
    }

    private void StopActivator()
    {
        if (ActivatorPid is not null)
        {
            _system.Root.Stop(ActivatorPid);
        }
    }

    public Task<DiagnosticsEntry[]> GetDiagnostics()
    {
        var endpoints = new DiagnosticsEntry("Remote", "Endpoints", _serverEndpoints.Keys.ToArray());

        return Task.FromResult(new[] { endpoints });
    }
}
