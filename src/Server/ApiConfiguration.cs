using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Dispatcher;
using System.Web.Http.ExceptionHandling;
using System.Web.Http.Filters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Owin;
using Serilog;

namespace Server
{
    public enum KeyKind { Collector, Dashboard }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public abstract class ApiKeyAttribute : Attribute
    {
        protected ApiKeyAttribute(KeyKind kind) { Kind = kind; }
        public KeyKind Kind { get; private set; }
    }

    public sealed class CollectorKeyAttribute : ApiKeyAttribute
    {
        public CollectorKeyAttribute() : base(KeyKind.Collector) { }
    }

    public sealed class DashboardKeyAttribute : ApiKeyAttribute
    {
        public DashboardKeyAttribute() : base(KeyKind.Dashboard) { }
    }

    public sealed class ApiRuntime
    {
        public ApiRuntime(ServerSettings settings, CollectorState collector, DatabaseMonitor database,
            Func<DateTime> utcNow, ILogger logger)
        {
            Settings = settings; Collector = collector; Database = database; UtcNow = utcNow; Logger = logger;
        }
        public ServerSettings Settings { get; private set; }
        public CollectorState Collector { get; private set; }
        public DatabaseMonitor Database { get; private set; }
        public Func<DateTime> UtcNow { get; private set; }
        public ILogger Logger { get; private set; }
    }

    public static class ApiConfiguration
    {
        public static HttpConfiguration Create(ApiRuntime runtime)
        {
            var config = new HttpConfiguration();
            config.IncludeErrorDetailPolicy = IncludeErrorDetailPolicy.Never;
            config.Formatters.Remove(config.Formatters.XmlFormatter);
            var json = config.Formatters.JsonFormatter.SerializerSettings;
            json.ContractResolver = new CamelCasePropertyNamesContractResolver();
            json.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
            json.DateFormatString = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            json.NullValueHandling = NullValueHandling.Include;
            config.Filters.Add(new KeyAuthorization(runtime));
            config.Services.Replace(typeof(IHttpControllerActivator), new ControllerActivator(runtime));
            config.Services.Replace(typeof(IExceptionHandler), new SafeExceptionHandler(runtime.Logger));
            config.MapHttpAttributeRoutes();
            return config;
        }

        public static void Configure(IAppBuilder app, ApiRuntime runtime)
        {
            app.UseWebApi(Create(runtime));
        }

        internal static HttpResponseMessage Error(HttpRequestMessage request, HttpStatusCode status, string error)
        {
            return request.CreateResponse(status, new { error });
        }
    }

    internal sealed class ControllerActivator : IHttpControllerActivator
    {
        private readonly ApiRuntime runtime;
        public ControllerActivator(ApiRuntime runtime) { this.runtime = runtime; }
        public IHttpController Create(HttpRequestMessage request, HttpControllerDescriptor descriptor, Type type)
        {
            return (IHttpController)Activator.CreateInstance(type, runtime);
        }
    }

    internal sealed class KeyAuthorization : AuthorizationFilterAttribute
    {
        private readonly ApiRuntime runtime;
        public KeyAuthorization(ApiRuntime runtime) { this.runtime = runtime; }
        public override void OnAuthorization(HttpActionContext context)
        {
            System.Collections.Generic.IEnumerable<string> headers;
            var values = context.Request.Headers.TryGetValues("X-Api-Key", out headers) ? headers.ToArray() : new string[0];
            var key = values.Length == 1 ? values[0] : null;
            var collector = string.Equals(key, runtime.Settings.CollectorKey, StringComparison.Ordinal);
            var dashboard = string.Equals(key, runtime.Settings.DashboardKey, StringComparison.Ordinal);
            string reason = null;
            if (string.IsNullOrWhiteSpace(key)) reason = values.Length > 1 ? "invalid" : "missing";
            else if (!collector && !dashboard) reason = "invalid";
            else
            {
                var attributes = context.ActionDescriptor.GetCustomAttributes<ApiKeyAttribute>()
                    .Concat(context.ControllerContext.ControllerDescriptor.GetCustomAttributes<ApiKeyAttribute>()).ToArray();
                if (attributes.Length != 1 || !(attributes[0].Kind == KeyKind.Collector ? collector : dashboard))
                    reason = "forbidden";
            }
            if (reason == null) return;
            runtime.Logger.Warning("Authentication {Reason} {Method} {Path}", reason,
                context.Request.Method.Method, context.Request.RequestUri.AbsolutePath);
            context.Response = ApiConfiguration.Error(context.Request,
                reason == "forbidden" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized,
                reason == "forbidden" ? "forbidden" : "unauthorized");
        }
    }

    internal sealed class SafeExceptionHandler : ExceptionHandler
    {
        private readonly ILogger logger;
        public SafeExceptionHandler(ILogger logger) { this.logger = logger; }
        public override void Handle(ExceptionHandlerContext context)
        {
            var request = context.ExceptionContext.Request;
            logger.Error("Unhandled {ExceptionType} {Path}", context.Exception.GetType().FullName,
                request.RequestUri.AbsolutePath);
            context.Result = new SafeErrorResult(request);
        }
        private sealed class SafeErrorResult : IHttpActionResult
        {
            private readonly HttpRequestMessage request;
            public SafeErrorResult(HttpRequestMessage request) { this.request = request; }
            public Task<HttpResponseMessage> ExecuteAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(ApiConfiguration.Error(request, HttpStatusCode.InternalServerError, "internal"));
            }
        }
    }

    public sealed class CollectorController : ApiController
    {
        private readonly ApiRuntime runtime;
        public CollectorController(ApiRuntime runtime) { this.runtime = runtime; }

        [HttpPost, Route("api/v1/collector/heartbeat"), CollectorKey]
        public async Task<HttpResponseMessage> Heartbeat()
        {
            JToken body;
            try
            {
                if (Request.Content == null) return ApiConfiguration.Error(Request, HttpStatusCode.BadRequest, "invalid_json");
                body = JToken.Parse(await Request.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            catch (JsonException) { return ApiConfiguration.Error(Request, HttpStatusCode.BadRequest, "invalid_json"); }
            var token = (body as JObject)?["runId"];
            Guid runId;
            if (token == null || token.Type != JTokenType.String ||
                !Guid.TryParse((string)token, out runId) || runId == Guid.Empty)
                return ApiConfiguration.Error(Request, HttpStatusCode.BadRequest, "invalid_field");
            var utc = runtime.UtcNow();
            runtime.Collector.Receive(utc);
            return Request.CreateResponse(HttpStatusCode.OK, new { serverTimeUtc = utc });
        }
    }

    public sealed class StatusController : ApiController
    {
        private readonly ApiRuntime runtime;
        public StatusController(ApiRuntime runtime) { this.runtime = runtime; }

        [HttpGet, Route("api/v1/status"), DashboardKey]
        public HttpResponseMessage Status()
        {
            var snapshot = runtime.Collector.Read();
            return Request.CreateResponse(HttpStatusCode.OK, new
            {
                serverTimeUtc = runtime.UtcNow(), database = runtime.Database.State,
                collector = snapshot.State, lastHeartbeatUtc = snapshot.LastHeartbeatUtc
            });
        }
    }
}
