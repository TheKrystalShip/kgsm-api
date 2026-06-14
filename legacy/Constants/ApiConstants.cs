namespace TheKrystalShip.KGSM.Api.Constants;

/// <summary>
/// Constants for API routes, policies, and configuration
/// </summary>
public static class ApiConstants
{
    /// <summary>
    /// API route constants
    /// </summary>
    public static class Routes
    {
        public const string ApiBase = "/api";
        public const string SystemBase = "/api/system";
        public const string InstancesBase = "/api/instances";
        public const string BlueprintsBase = "/api/blueprints";

        public const string SystemMetrics = "/api/system/metrics";
        public const string SystemCpu = "/api/system/cpu";
        public const string SystemMemory = "/api/system/memory";
        public const string SystemDisk = "/api/system/disk";
        public const string SystemNetwork = "/api/system/network";

        public const string Health = "/health";
    }

    /// <summary>
    /// CORS policy names
    /// </summary>
    public static class CorsPolicy
    {
        public const string KgsmApiPolicy = "KgsmApiPolicy";
    }

    /// <summary>
    /// Configuration section names
    /// </summary>
    public static class ConfigurationSections
    {
        public const string KgsmApi = "KgsmApi";
        public const string KgsmPath = "KgsmApi:KgsmPath";
        public const string SocketPath = "KgsmApi:SocketPath";
    }

    /// <summary>
    /// Default configuration values
    /// </summary>
    public static class Defaults
    {
        public const string KgsmPath = "kgsm";
        public const string SocketPath = "/tmp/kgsm.sock";
        public const string LocalhostOrigin = "http://localhost:3000";
        public const string LocalhostIpOrigin = "http://127.0.0.1:3000";
    }

    /// <summary>
    /// OpenAPI/Swagger configuration
    /// </summary>
    public static class OpenApi
    {
        public const string EndpointPath = "/openapi/v1.json";
        public const string Title = "KGSM API v1";
    }

    /// <summary>
    /// Content types
    /// </summary>
    public static class ContentTypes
    {
        public const string ApplicationJson = "application/json";
        public const string TextPlain = "text/plain";
        public const string TextHtml = "text/html";
    }

    /// <summary>
    /// HTTP headers
    /// </summary>
    public static class Headers
    {
        public const string ContentType = "Content-Type";
        public const string Accept = "Accept";
        public const string Authorization = "Authorization";
        public const string CacheControl = "Cache-Control";
    }
}
