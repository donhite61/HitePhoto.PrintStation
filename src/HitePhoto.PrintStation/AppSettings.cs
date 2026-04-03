using System.Text.Json.Serialization;
using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation;

public class RoutingEntry
{
    public int     ChannelNumber { get; set; }
    public string? LayoutName    { get; set; }
    public string  Source        { get; set; } = "";
}

public class AppSettings
{
    // ── Database (MariaDB on Dell server) ──
    public string DbHost     { get; set; } = "192.168.1.149";
    public int    DbPort     { get; set; } = 3306;
    public string DbName     { get; set; } = "hitephoto";
    public string DbUser     { get; set; } = "labapi";
    public string DbPassword { get; set; } = "SlantedPeanuts2026";

    /// <summary>This store's ID in the stores table (BH=1, WB=2).</summary>
    public int StoreId { get; set; } = 2;

    // ── Noritsu output ──
    public string NoritsuOutputRoot { get; set; } = "";
    public string ChannelsCsvPath   { get; set; } = "";

    // ── Refresh ──
    public int RefreshIntervalSeconds { get; set; } = 30;

    // ── Display ──
    public string Theme          { get; set; } = "Light";
    public string TreeFontFamily { get; set; } = "Segoe UI";
    public int    OrderFontSize  { get; set; } = 14;
    public int    SizeFontSize   { get; set; } = 13;

    // ── Logging ──
    public bool EnableLogging { get; set; } = true;
    public string LogDirectory { get; set; } = "";

    // ── Paths (profile override) ──
    public string SqlitePath { get; set; } = "";

    // ── Routing ──
    [Obsolete("Routing now lives in SQLite channel_mappings table. Kept for JSON deserialization compatibility.")]
    public Dictionary<string, RoutingEntry> RoutingMap { get; set; } = new();
    public List<LayoutDefinition> Layouts { get; set; } = new();

    // ── Color correction ──
    public List<string>? CorrectionSlotLayout { get; set; }
    public CorrectionStrengths CorrectionStrengths { get; set; } = new();
    public ExposureRatios ExposureRatios { get; set; } = new();
    public ControlParameters ControlParameters { get; set; } = new();
    public Dictionary<string, PresetDefinition> Presets { get; set; } = new();
    public List<CorrectionSettingsProfile> CorrectionProfiles { get; set; } = new();

    // ── Email notifications ──
    public bool   NotificationsEnabled     { get; set; } = false;
    public string SmtpHost                 { get; set; } = "mail.hitephoto.com";
    public int    SmtpPort                 { get; set; } = 465;
    public string SmtpUsername             { get; set; } = "noreply@hitephoto.com";
    public string SmtpPassword             { get; set; } = "Hite1985?";
    public string NotificationFromEmail    { get; set; } = "noreply@hitephoto.com";
    public string NotificationSubject      { get; set; } = "Your photos are ready for pickup!";
    public string NotificationBodyTemplate { get; set; } =
        "Hi {CustomerName},\n\nYour photo order is ready for pickup at {StoreName}.\n\nThank you!\nHite Photo";
    public List<Core.Processing.EmailTemplate> EmailTemplates { get; set; } = new();
    public string DefaultPickupTemplate  { get; set; } = "Pickup";
    public string DefaultShippedTemplate { get; set; } = "Shipped";

    /// <summary>Find a template by name, or null if not found.</summary>
    public Core.Processing.EmailTemplate? GetTemplate(string name)
        => EmailTemplates.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Get the appropriate default template for pickup or shipped orders.</summary>
    public Core.Processing.EmailTemplate? GetDefaultTemplate(bool isShipped)
        => GetTemplate(isShipped ? DefaultShippedTemplate : DefaultPickupTemplate)
           ?? EmailTemplates.FirstOrDefault();

    // ── Pixfizz ──
    public bool   PixfizzEnabled         { get; set; } = true;
    public string PixfizzApiUrl          { get; set; } = "https://nazkcvruighrhpgcarxg.supabase.co/functions/v1";
    public string PixfizzApiKey          { get; set; } = "";
    public string PixfizzOrganizationId  { get; set; } = "";
    public string PixfizzLocationId      { get; set; } = "";
    public string PixfizzNotifyMode      { get; set; } = "Pixfizz"; // "Pixfizz" or "Email"
    public string PixfizzFtpServer       { get; set; } = "ftp.pixfizz.com";
    public int    PixfizzFtpPort         { get; set; } = 21;
    public string PixfizzFtpUsername     { get; set; } = "";
    public string PixfizzFtpPassword     { get; set; } = "";
    public string PixfizzFtpArtworkFolder  { get; set; } = "/Artwork";
    public string PixfizzFtpDarkroomFolder { get; set; } = "/Darkroom";

    // ── Dakis ──
    public bool   DakisEnabled           { get; set; } = true;
    public string DakisWatchFolder       { get; set; } = "";

    // ── Ingest ──
    public string OrderOutputPath        { get; set; } = "";
    public int    PollIntervalSeconds    { get; set; } = 30;
    public int    DaysToVerify           { get; set; } = 14;

    // ── NAS / Auto-update ──
    public string NasRootFolder       { get; set; } = "";
    public string NasLogFolder        { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string UpdateLocalFolder => NasRootFolder;
    public string UpdateSftpHost      { get; set; } = "";
    public int    UpdateSftpPort      { get; set; } = 22;
    public string UpdateSftpUsername  { get; set; } = "";
    public string UpdateSftpPassword  { get; set; } = "";
    public string UpdateSftpFolder    { get; set; } = "";

    // ── Inter-store transfer ──
    public string TransferSftpHost     { get; set; } = "";
    public int    TransferSftpPort     { get; set; } = 22;
    public string TransferSftpUsername { get; set; } = "";
    public string TransferSftpPassword { get; set; } = "";
    public string TransferNasPrefix    { get; set; } = "S:";
    public string TransferRemoteRoot   { get; set; } = "/AAPhoto";

    // ── MariaDB sync ──
    public bool SyncEnabled          { get; set; } = true;
    public int  SyncIntervalSeconds  { get; set; } = 30;

    // ── Developer mode ──
    public bool DeveloperMode { get; set; } = false;

    /// <summary>Build a MySqlConnector connection string from the settings.</summary>
    [JsonIgnore]
    public string ConnectionString =>
        $"Server={DbHost};Port={DbPort};Database={DbName};User={DbUser};Password={DbPassword};" +
        "SslMode=None;AllowPublicKeyRetrieval=true;ConnectionTimeout=5;GuidFormat=None";
}
