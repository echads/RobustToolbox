using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Robust.Server.Interfaces;
using Robust.Server.Interfaces.Player;
using Robust.Server.Interfaces.ServerStatus;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Interfaces.Configuration;
using Robust.Shared.Interfaces.Log;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.IoC;
using Robust.Shared.Log;

// This entire file is NIHing a REST server because pulling in libraries is effort.
// Also it was fun to write.
// Just slap this thing behind an Nginx reverse proxy. It's not supposed to be directly exposed to the web.

namespace Robust.Server.ServerStatus
{
    internal sealed partial class StatusHost : IStatusHost, IDisposable
    {
        private const string Sawmill = "statushost";

        [Dependency] private readonly IConfigurationManager _configurationManager = default!;
        [Dependency] private readonly IServerNetManager _netManager = default!;
        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly IBaseServer _baseServer = default!;

        private static readonly JsonSerializer JsonSerializer = new();
        private readonly List<StatusHostHandler> _handlers = new();
        private HttpListener? _listener;
        private TaskCompletionSource? _stopSource;
        private ISawmill _httpSawmill = default!;

        private string? _serverNameCache;

        public Task ProcessRequestAsync(HttpListenerContext context)
        {
            var response = context.Response;
            var request = context.Request;
            var method = new HttpMethod(request.HttpMethod);

            _httpSawmill.Info($"{method} {context.Request.Url?.PathAndQuery} from {request.RemoteEndPoint}");

            try
            {
                foreach (var handler in _handlers)
                {
                    if (handler(method, request, response))
                    {
                        return Task.CompletedTask;
                    }
                }

                // No handler returned true, assume no handlers care about this.
                // 404.
                response.Respond(method, "Not Found", HttpStatusCode.NotFound);
            }
            catch (Exception e)
            {
                response.Respond(method, "Internal Server Error", HttpStatusCode.InternalServerError);
                _httpSawmill.Error($"Exception in StatusHost: {e}");
            }

            /*
            _httpSawmill.Debug(Sawmill, $"{method} {context.Request.Url!.PathAndQuery} {context.Response.StatusCode} " +
                                         $"{(HttpStatusCode) context.Response.StatusCode} to {context.Request.RemoteEndPoint}");
                                         */

            return Task.CompletedTask;
        }

        public event Action<JObject>? OnStatusRequest;

        public event Action<JObject>? OnInfoRequest;

        public void AddHandler(StatusHostHandler handler)
        {
            _handlers.Add(handler);
        }

        public void Start()
        {
            _httpSawmill = Logger.GetSawmill($"{Sawmill}.http");
            RegisterCVars();

            // Cache this in a field to avoid thread safety shenanigans.
            // Writes/reads of references are atomic in C# so no further synchronization necessary.
            _configurationManager.OnValueChanged(CVars.GameHostName, n => _serverNameCache = n);

            if (!_configurationManager.GetCVar(CVars.StatusEnabled))
            {
                return;
            }

            RegisterHandlers();

            _stopSource = new TaskCompletionSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://{_configurationManager.GetCVar(CVars.StatusBind)}/");
            _listener.Start();

            Task.Run(ListenerThread);
        }

        // Not a real thread but whatever.
        private async Task ListenerThread()
        {
            var maxConnections = _configurationManager.GetCVar(CVars.StatusMaxConnections);
            var connectionsSemaphore = new SemaphoreSlim(maxConnections, maxConnections);
            while (true)
            {
                var getContextTask = _listener!.GetContextAsync();
                var task = await Task.WhenAny(getContextTask, _stopSource!.Task);

                if (task == _stopSource.Task)
                {
                    return;
                }

                await connectionsSemaphore.WaitAsync();

                // Task.Run this so it gets run on another thread pool thread.
#pragma warning disable 4014
                Task.Run(async () =>
#pragma warning restore 4014
                {
                    try
                    {
                        var ctx = await getContextTask;
                        await ProcessRequestAsync(ctx);
                    }
                    catch (Exception e)
                    {
                        _httpSawmill.Error($"Error inside ProcessRequestAsync:\n{e}");
                    }
                    finally
                    {
                        connectionsSemaphore.Release();
                    }
                });
            }
        }

        private void RegisterCVars()
        {
            try
            {
                var buildInfo = File.ReadAllText(PathHelpers.ExecutableRelativeFile("build.json"));
                var info = JsonConvert.DeserializeObject<BuildInfo>(buildInfo);

                // Don't replace cvars with contents of build.json if overriden by --cvar or such.
                SetCVarIfUnmodified(CVars.BuildEngineVersion, info.EngineVersion);
                SetCVarIfUnmodified(CVars.BuildForkId, info.ForkId);
                SetCVarIfUnmodified(CVars.BuildVersion, info.Version);
                SetCVarIfUnmodified(CVars.BuildDownloadUrl, info.Download ?? "");
                SetCVarIfUnmodified(CVars.BuildHash, info.Hash ?? "");
            }
            catch (FileNotFoundException)
            {
            }

            void SetCVarIfUnmodified(CVarDef<string> cvar, string val)
            {
                if (_configurationManager.GetCVar(cvar) == "")
                    _configurationManager.SetCVar(cvar, val);
            }
        }

        public void Dispose()
        {
            if (_stopSource == null)
            {
                return;
            }

            _stopSource.SetResult();
            _listener!.Stop();
        }

        [JsonObject(ItemRequired = Required.DisallowNull)]
        private sealed class BuildInfo
        {
            [JsonProperty("engine_version")] public string EngineVersion = default!;
            [JsonProperty("hash")] public string? Hash;
            [JsonProperty("download")] public string? Download;
            [JsonProperty("fork_id")] public string ForkId = default!;
            [JsonProperty("version")] public string Version = default!;
        }
    }
}
