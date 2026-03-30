using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

// =============================================================================
// Entra Security Group – Bulk User Manager
// =============================================================================
// Modes:
//   Scheduled / non-interactive:
//     EntraGroupManager add    --csv <file> --group <name-or-id> [--log <file>]
//     EntraGroupManager remove --csv <file> --group <name-or-id> [--log <file>]
//   Interactive (no arguments):
//     EntraGroupManager
//
// Required app permissions (admin consent in Entra portal):
//   • User.Read.All              – resolve UPN → object ID
//   • GroupMember.ReadWrite.All  – add / remove group members
//   • Group.Read.All             – resolve group name → object ID
// =============================================================================

// ─────────────────────────────────────────────────────────────────────────────
// Configuration – appsettings.json + ENTRA_* environment variables
// ─────────────────────────────────────────────────────────────────────────────
IConfiguration config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "ENTRA_")
    .Build();

string tenantId     = config["AzureAd:TenantId"]     ?? throw new InvalidOperationException("AzureAd:TenantId is not configured.");
string clientId     = config["AzureAd:ClientId"]     ?? throw new InvalidOperationException("AzureAd:ClientId is not configured.");
string clientSecret = config["AzureAd:ClientSecret"] ?? throw new InvalidOperationException("AzureAd:ClientSecret is not configured.");

// ─────────────────────────────────────────────────────────────────────────────
// Input – command-line (scheduled) or interactive
// ─────────────────────────────────────────────────────────────────────────────
string operation;   // "add" | "remove"
string csvPath;
string groupInput;
string? logPath = null;

if (args.Length > 0)
{
    // ── Scheduled / non-interactive mode ──────────────────────────────────
    operation = args[0].ToLowerInvariant();
    if (operation is not ("add" or "remove"))
    {
        Console.Error.WriteLine("ERROR: First argument must be 'add' or 'remove'.");
        PrintUsage();
        Environment.Exit(1);
    }

    csvPath    = GetArg(args, "--csv")   ?? string.Empty;
    groupInput = GetArg(args, "--group") ?? string.Empty;
    logPath    = GetArg(args, "--log");

    if (string.IsNullOrEmpty(csvPath) || string.IsNullOrEmpty(groupInput))
    {
        Console.Error.WriteLine("ERROR: --csv and --group are required arguments.");
        PrintUsage();
        Environment.Exit(1);
    }
}
else
{
    // ── Interactive mode ───────────────────────────────────────────────────
    Console.WriteLine("=== Entra Security Group – Bulk User Manager ===");
    Console.WriteLine();

    Console.Write("Operation [add/remove]: ");
    operation = Console.ReadLine()?.Trim().ToLowerInvariant() ?? string.Empty;
    if (operation is not ("add" or "remove"))
    {
        Console.Error.WriteLine("ERROR: Operation must be 'add' or 'remove'.");
        Environment.Exit(1);
    }

    Console.Write("CSV file path           : ");
    csvPath = Console.ReadLine()?.Trim() ?? string.Empty;
    if (string.IsNullOrEmpty(csvPath))
    {
        Console.Error.WriteLine("ERROR: CSV file path cannot be empty.");
        Environment.Exit(1);
    }

    Console.Write("Security group name/ID  : ");
    groupInput = Console.ReadLine()?.Trim() ?? string.Empty;
    if (string.IsNullOrEmpty(groupInput))
    {
        Console.Error.WriteLine("ERROR: Group name/ID cannot be empty.");
        Environment.Exit(1);
    }

    Console.Write("Log file path (optional): ");
    var logInput = Console.ReadLine()?.Trim();
    if (!string.IsNullOrEmpty(logInput)) logPath = logInput;

    Console.WriteLine();
}

// ─────────────────────────────────────────────────────────────────────────────
// Logging – console + optional append-mode file (suitable for scheduled runs)
// ─────────────────────────────────────────────────────────────────────────────
StreamWriter? logWriter = null;
if (logPath is not null)
{
    try
    {
        logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"WARNING: Cannot open log file '{logPath}': {ex.Message}");
    }
}

void Log(string message)
{
    Console.WriteLine(message);
    logWriter?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
}

void LogSection(string title)
{
    Log($"\n── {title} ─────────────────────────────────────────────────────");
}

// ─────────────────────────────────────────────────────────────────────────────
// Read & validate CSV
// ─────────────────────────────────────────────────────────────────────────────
if (!File.Exists(csvPath))
{
    Console.Error.WriteLine($"ERROR: CSV file not found: '{csvPath}'");
    Environment.Exit(1);
}

List<string> upns = ReadUpnsFromCsv(csvPath);
if (upns.Count == 0)
{
    Console.Error.WriteLine("ERROR: No user identifiers found in the CSV file.");
    Environment.Exit(1);
}

LogSection("Starting run");
Log($"  Timestamp  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Log($"  Operation  : {operation.ToUpper()}");
Log($"  CSV file   : {csvPath}  ({upns.Count} user(s) loaded)");
Log($"  Group      : {groupInput}");

// ─────────────────────────────────────────────────────────────────────────────
// Build Graph client (Client Credentials – no user sign-in required)
// ─────────────────────────────────────────────────────────────────────────────
var credential  = new ClientSecretCredential(tenantId, clientId, clientSecret);
var graphClient = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);

// ─────────────────────────────────────────────────────────────────────────────
// Resolve security group (name → object ID, or pass-through if already a GUID)
// ─────────────────────────────────────────────────────────────────────────────
string groupId;
try
{
    groupId = await ResolveGroupIdAsync(graphClient, groupInput);
    Log($"  Group ID   : {groupId}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\nERROR: Could not resolve group '{groupInput}': {ex.Message}");
    logWriter?.Close();
    Environment.Exit(1);
    return;
}

// ─────────────────────────────────────────────────────────────────────────────
// Process users
// ─────────────────────────────────────────────────────────────────────────────
LogSection($"Processing {upns.Count} user(s)");

int succeeded = 0, skipped = 0, failed = 0;

foreach (string upn in upns)
{
    try
    {
        User? user = await graphClient.Users[upn].GetAsync(req =>
        {
            req.QueryParameters.Select = ["id", "displayName", "userPrincipalName"];
        });

        if (user?.Id is null)
        {
            Log($"  [SKIP] {upn,-45} – user not found in directory.");
            skipped++;
            continue;
        }

        string display = $"{user.UserPrincipalName} ({user.DisplayName})";

        if (operation == "add")
        {
            await graphClient.Groups[groupId].Members.Ref.PostAsync(new ReferenceCreate
            {
                OdataId = $"https://graph.microsoft.com/v1.0/directoryObjects/{user.Id}"
            });
            Log($"  [OK]   Added   {display}");
        }
        else
        {
            // DELETE /groups/{groupId}/members/{userId}/$ref
            await graphClient.Groups[groupId].Members[user.Id].Ref.DeleteAsync();
            Log($"  [OK]   Removed {display}");
        }

        succeeded++;
    }
    catch (ODataError odataEx) when (
        operation == "add"
        && odataEx.Error?.Code == "Request_BadRequest"
        && odataEx.Error.Message?.Contains("already exist", StringComparison.OrdinalIgnoreCase) == true)
    {
        Log($"  [SKIP] {upn,-45} – already a member.");
        skipped++;
    }
    catch (ODataError odataEx) when (
        operation == "remove"
        && (odataEx.Error?.Code == "Request_BadRequest" || odataEx.Error?.Code == "404")
        && (odataEx.Error.Message?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) == true
            || odataEx.Error.Message?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true))
    {
        Log($"  [SKIP] {upn,-45} – not a member.");
        skipped++;
    }
    catch (ODataError odataEx)
    {
        Log($"  [FAIL] {upn,-45} – Graph error [{odataEx.Error?.Code}]: {odataEx.Error?.Message}");
        failed++;
    }
    catch (Exception ex)
    {
        Log($"  [FAIL] {upn,-45} – {ex.Message}");
        failed++;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Summary
// ─────────────────────────────────────────────────────────────────────────────
LogSection("Summary");
Log($"  Operation  : {operation.ToUpper()}");
Log($"  Group      : {groupInput}");
Log($"  Total      : {upns.Count}");
Log($"  Succeeded  : {succeeded}");
Log($"  Skipped    : {skipped}  (already member / not member / not found)");
Log($"  Failed     : {failed}");
Log(string.Empty);

logWriter?.Close();

// Exit 0 = all succeeded/skipped  |  Exit 2 = at least one hard failure
Environment.Exit(failed > 0 ? 2 : 0);

// =============================================================================
// Helper methods
// =============================================================================

static string? GetArg(string[] args, string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}

static void PrintUsage()
{
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  EntraGroupManager add    --csv <file> --group <name-or-id> [--log <file>]");
    Console.WriteLine("  EntraGroupManager remove --csv <file> --group <name-or-id> [--log <file>]");
    Console.WriteLine("  EntraGroupManager                    (interactive mode – prompts for inputs)");
    Console.WriteLine();
    Console.WriteLine("CSV format:");
    Console.WriteLine("  Must contain a column named 'UserPrincipalName', 'UPN', 'Email', or 'Mail'.");
    Console.WriteLine("  If no recognised header is found, the first column is used as the UPN.");
    Console.WriteLine("  Headerless single-column files (one UPN per line) are also supported.");
}

/// <summary>
/// Reads UPNs (or object IDs) from a CSV file.
/// Recognises header columns: UserPrincipalName, UPN, Email, Mail, User.
/// Falls back to column 0 when no recognised header is found.
/// </summary>
static List<string> ReadUpnsFromCsv(string path)
{
    var lines = File.ReadAllLines(path)
        .Where(l => !string.IsNullOrWhiteSpace(l))
        .ToList();

    if (lines.Count == 0) return [];

    var firstLine = lines[0];
    var headers   = firstLine.Split(',').Select(h => h.Trim().Trim('"')).ToList();

    string[] knownHeaders = ["userprincipalname", "upn", "email", "mail", "user"];
    int upnColumnIndex = -1;

    for (int i = 0; i < headers.Count; i++)
    {
        if (knownHeaders.Contains(headers[i].ToLowerInvariant()))
        {
            upnColumnIndex = i;
            break;
        }
    }

    int startLine;
    if (upnColumnIndex >= 0)
    {
        // Named header row found – skip it
        startLine = 1;
    }
    else if (firstLine.Contains('@'))
    {
        // Headerless file – treat first line as data, use column 0
        upnColumnIndex = 0;
        startLine = 0;
    }
    else
    {
        // Unknown header – skip first row, use column 0
        upnColumnIndex = 0;
        startLine = 1;
    }

    var result = new List<string>();
    for (int i = startLine; i < lines.Count; i++)
    {
        var cols  = lines[i].Split(',');
        if (upnColumnIndex < cols.Length)
        {
            var value = cols[upnColumnIndex].Trim().Trim('"');
            if (!string.IsNullOrEmpty(value) && !value.StartsWith('#'))
                result.Add(value);
        }
    }

    return result;
}

/// <summary>
/// Resolves a group name or GUID to an Entra security group object ID.
/// If <paramref name="groupInput"/> is already a valid GUID it is returned as-is.
/// Otherwise a case-sensitive display-name search is performed.
/// </summary>
static async Task<string> ResolveGroupIdAsync(GraphServiceClient client, string groupInput)
{
    if (Guid.TryParse(groupInput, out _))
        return groupInput;   // already an object ID

    // Escape single quotes in the display-name for the OData filter
    string safeName = groupInput.Replace("'", "''");

    var result = await client.Groups.GetAsync(req =>
    {
        req.QueryParameters.Filter = $"displayName eq '{safeName}' and securityEnabled eq true";
        req.QueryParameters.Select = ["id", "displayName"];
        req.QueryParameters.Top    = 2;
    });

    var groups = result?.Value;
    if (groups is null || groups.Count == 0)
        throw new InvalidOperationException($"No security group named '{groupInput}' was found.");

    if (groups.Count > 1)
        throw new InvalidOperationException(
            $"Multiple groups named '{groupInput}' exist. Please use the Object ID instead.");

    return groups[0].Id ?? throw new InvalidOperationException("Group returned a null ID.");
}
