using TheKrystalShip.KGSM.ComponentConfig;

// What the Control Panel shows about the API itself, declared beside the configuration it describes.
// TheKrystalShip.KGSM.ComponentConfig reads this out of the built assembly and writes
// deploy/kgsm-api.leaf.json; deploy.sh installs that into /var/lib/kgsm/leaves/api.json — which this
// same API then scans. It reads its descriptor as one leaf among many and never as its own config.

[assembly: Leaf(
    id: "api",
    displayName: "Control Panel API",
    unit: "kgsm-api.service",
    role: "The aggregator serving this Control Panel — the API every surface on this host talks to.",
    ReadOnly = true,
    ReadOnlyReason = "The Control Panel API publishes its own configuration for reading only. Applying a change means restarting it, which would kill the request asking for the change and take the panel down with it — so its settings are edited in /etc/kgsm-api/kgsm-api.env and applied by restarting the service.")]

[assembly: ConfigGroup("identity", "Identity", 1)]
[assembly: ConfigGroup("leaves", "Leaf connections", 2)]
[assembly: ConfigGroup("engine", "KGSM engine", 3)]
[assembly: ConfigGroup("polling", "Polling", 4)]
[assembly: ConfigGroup("auth", "Authentication", 5)]
[assembly: ConfigGroup("sessions", "Sessions", 6)]
[assembly: ConfigGroup("library", "Game library & cover art", 7)]
[assembly: ConfigGroup("files", "File & blueprint editing", 8)]
[assembly: ConfigGroup("logs", "Log reading", 9)]
[assembly: ConfigGroup("leafconfig", "Leaf configuration", 10)]
[assembly: ConfigGroup("cluster", "Cluster", 11)]
[assembly: ConfigGroup("storage", "Storage", 12)]
[assembly: ConfigGroup("general", "General", 13)]

// Lowest precedence first — the same order Program.cs registers them in.
[assembly: ConfigFloorSource("appsettings", "/opt/kgsm-api/kgsm-api.settings.json")]
[assembly: ConfigFloorSource("systemd-unit", "kgsm-api.service")]
[assembly: ConfigFloorSource("env-file", "/etc/kgsm-api/kgsm-api.env")]

[assembly: ConfigFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

[assembly: ConfigFrameworkNamespace("Kestrel__",
    "the HTTPS certificate keys are Kestrel's own, and its configuration surface is not this API's to enumerate")]

// ASP.NET's own host-filtering key, read by the framework before any of this API's types exist.
[assembly: ConfigFrameworkNamespace("AllowedHosts",
    "host filtering belongs to the framework, not to this API's configuration surface")]

[assembly: ConfigFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this API logs.",
    Group = "general",
    Type = ConfigType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]
