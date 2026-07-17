using MHServerEmu.Core.Config;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.WebFrontend.Handlers;
using MHServerEmu.WebFrontend.Handlers.MTXStore;
using MHServerEmu.WebFrontend.Handlers.WebApi;
using MHServerEmu.WebFrontend.Network;

namespace MHServerEmu.WebFrontend
{
    /// <summary>
    /// Handles HTTP requests from clients.
    /// </summary>
    public class WebFrontendService : IGameService
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly WebFrontendServiceMailbox _serviceMailbox = new();

        private readonly WebService _webService;
        private List<string> _dashboardEndpoints;
        private List<string> _newsEndpoints;

        public GameServiceState State { get; private set; } = GameServiceState.Created;

        /// <summary>
        /// Constructs a new <see cref="WebFrontendService"/> instance.
        /// </summary>
        public WebFrontendService()
        {
            var config = ConfigManager.Instance.GetConfig<WebFrontendConfig>();

            WebServiceSettings webServiceSettings = new()
            {
                Name = "WebFrontend",
                ListenUrl = $"http://{config.Address}:{config.Port}/",
                FallbackHandler = new NotFoundWebHandler(),
            };

            _webService = new(webServiceSettings);

            // Register the protobuf handler to the /Login/IndexPB path for compatibility with legacy reverse proxy setups.
            // We should probably prefer to use /AuthServer/Login/IndexPB because it's more accurate to what Gazillion had.
            ProtobufWebHandler protobufHandler = new(config.EnableLoginRateLimit, TimeSpan.FromMilliseconds(config.LoginRateLimitCostMS), config.LoginRateLimitBurst);
            _webService.RegisterHandler("/Login/IndexPB",            protobufHandler);
            _webService.RegisterHandler("/AuthServer/Login/IndexPB", protobufHandler);

            // MTXStore handlers are used for the Add G panel in the client UI.
            _webService.RegisterHandler("/MTXStore/AddG", new AddGWebHandler());
            _webService.RegisterHandler("/MTXStore/AddG/Submit", new AddGSubmitWebHandler());

            if (config.EnableWebApi)
            {
                InitializeWebBackend();
                WebApiKeyManager.Instance.LoadKeys();

                if (config.EnableDashboard)
                    _dashboardEndpoints = InitializeStaticSite("web dashboard", config.DashboardFileDirectory, config.DashboardUrlPath);

                if (config.EnableNewsPage)
                    _newsEndpoints = InitializeStaticSite("news page", config.NewsFileDirectory, config.NewsUrlPath);
            }
        }

        #region IGameService Implementation

        /// <summary>
        /// Runs this <see cref="WebFrontendService"/> instance.
        /// </summary>
        public void Run()
        {
            _webService.Start();
            State = GameServiceState.Running;

            while (_webService.IsRunning)
            {
                _serviceMailbox.ProcessMessages();
                Thread.Sleep(1);
            }

            State = GameServiceState.Shutdown;
        }

        /// <summary>
        /// Stops listening and shuts down this <see cref="WebFrontendService"/> instance.
        /// </summary>
        public void Shutdown()
        {
            _webService.Stop();
        }

        public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage
        {
            _serviceMailbox.PostMessage(message);
        }

        public void GetStatus(Dictionary<string, long> statusDict)
        {
            statusDict["WebFrontendHandlers"] = _webService.HandlerCount;
            statusDict["WebFrontendHandledRequests"] = _webService.HandledRequests;
        }

        #endregion

        public void ReloadDashboard()
        {
            ReloadStaticSite(_dashboardEndpoints);
        }

        public void ReloadAddGPage()
        {
            AddGWebHandler addGHandler = _webService.GetHandler("/MTXStore/AddG") as AddGWebHandler;
            addGHandler?.Load();
        }

        public void ReloadNews()
        {
            ReloadStaticSite(_newsEndpoints);
        }

        private void InitializeWebBackend()
        {
            _webService.RegisterHandler("/AccountManagement/Create",        new AccountCreateWebHandler());
            _webService.RegisterHandler("/AccountManagement/SetPlayerName", new AccountSetPlayerNameWebHandler());
            _webService.RegisterHandler("/AccountManagement/SetPassword",   new AccountSetPasswordWebHandler());
            _webService.RegisterHandler("/AccountManagement/SetUserLevel",  new AccountSetUserLevelWebHandler());
            _webService.RegisterHandler("/AccountManagement/SetFlag",       new AccountSetFlagWebHandler());
            _webService.RegisterHandler("/AccountManagement/ClearFlag",     new AccountClearFlagWebHandler());

            _webService.RegisterHandler("/ServerStatus", new ServerStatusWebHandler());

            // PhantomHeroes runtime endpoints.
            _webService.RegisterHandler("/webapi/phantom/spawn",  new PhantomHeroSpawnWebHandler());
            _webService.RegisterHandler("/webapi/phantom/clear",  new PhantomHeroClearWebHandler());
            _webService.RegisterHandler("/webapi/phantom/status", new PhantomHeroStatusWebHandler());

            _webService.RegisterHandler("/RegionReport", new RegionReportWebHandler());
            _webService.RegisterHandler("/Metrics/Performance", new MetricsPerformanceWebHandler());
        }

        private void ReloadStaticSite(List<string> endpoints)
        {
            if (endpoints == null)
                return;

            foreach (string localPath in endpoints)
            {
                StaticFileWebHandler fileHandler = _webService.GetHandler(localPath) as StaticFileWebHandler;
                fileHandler?.Load();
            }
        }

        /// <summary>
        /// Registers a directory of static files (an index.html plus any other assets) under the
        /// given URL path. Used for both the admin web dashboard and the in-game news page.
        /// </summary>
        private List<string> InitializeStaticSite(string siteLabel, string directoryName, string localPath)
        {
            string siteDirectory = Path.Combine(FileHelper.DataDirectory, "Web", directoryName);
            if (Directory.Exists(siteDirectory) == false)
            {
                Logger.Warn($"InitializeStaticSite(): Directory '{directoryName}' does not exist for {siteLabel}");
                return null;
            }

            string indexFilePath = Path.Combine(siteDirectory, "index.html");
            if (File.Exists(indexFilePath) == false)
            {
                Logger.Warn($"InitializeStaticSite(): Index file not found at '{indexFilePath}' for {siteLabel}");
                return null;
            }

            List<string> endpoints = new();

            // Make sure local path starts and ends with slashes.
            if (localPath.StartsWith('/') == false)
                localPath = $"/{localPath}";

            if (localPath.EndsWith('/') == false)
                localPath = $"{localPath}/";

            _webService.RegisterHandler(localPath, new StaticFileWebHandler(indexFilePath));
            endpoints.Add(localPath);

            // Add redirect for requests to our site "directory" that don't have trailing slashes.
            if (localPath.Length > 1)
            {
                string localPathRedirect = localPath[..^1];
                _webService.RegisterHandler(localPathRedirect, new TrailingSlashRedirectWebHandler());
                endpoints.Add(localPathRedirect);
            }

            // Register other files.
            foreach (string filePath in Directory.GetFiles(siteDirectory, "*", SearchOption.AllDirectories))
            {
                string relativeFilePath = Path.GetRelativePath(siteDirectory, filePath);

                if (string.Equals(relativeFilePath, "index.html", StringComparison.InvariantCultureIgnoreCase))
                    continue;

                string subFilePath = $"{localPath}{relativeFilePath.Replace('\\', '/')}";

                _webService.RegisterHandler(subFilePath, new StaticFileWebHandler(filePath));
                endpoints.Add(subFilePath);
            }

            Logger.Info($"Initialized {siteLabel} at {localPath}");
            return endpoints;
        }
    }
}
