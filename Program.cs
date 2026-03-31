// Azure.Identity – supplies ClientSecretCredential, which authenticates the app
// with Entra ID (formerly Azure AD) using the client-credentials OAuth 2.0 flow
// (i.e. app-only, no interactive user sign-in required).
using Azure.Identity;

// Microsoft.Extensions.Configuration – layered configuration abstraction that lets
// us read settings from appsettings.json and override them with environment variables
// without changing any code (important for secret management in CI/CD pipelines).
using Microsoft.Extensions.Configuration;

// Microsoft.Graph – the official .NET SDK for the Microsoft Graph REST API.
// GraphServiceClient wraps every API call with strongly-typed request builders.
using Microsoft.Graph;

// Microsoft.Graph.Models – strongly-typed POCO classes returned by Graph responses
// (User, Group, ReferenceCreate, etc.).
using Microsoft.Graph.Models;

// Microsoft.Graph.Models.ODataErrors – ODataError is thrown by the Graph SDK whenever
// the API returns a 4xx/5xx HTTP error, so we can inspect the error code and message
// to distinguish expected "soft" failures (already a member) from real errors.
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
// We use a two-layer configuration approach:
//   Layer 1 – appsettings.json   : stores non-secret defaults (TenantId, ClientId).
//   Layer 2 – environment vars   : any variable prefixed with "ENTRA_" overrides the
//             JSON value at runtime. This lets CI/CD pipelines and scheduled task
//             runners inject the ClientSecret without it ever touching the file system.
// The later layers win on conflict, so env vars always take precedence over JSON.
IConfiguration config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "ENTRA_")
    .Build();

// Fail fast at startup if any required setting is absent.
// Using ?? throw (null-coalescing throw) gives a readable error message immediately
// rather than a cryptic NullReferenceException deep inside the Graph SDK later.
string tenantId     = config["AzureAd:TenantId"]     ?? throw new InvalidOperationException("AzureAd:TenantId is not configured.");
string clientId     = config["AzureAd:ClientId"]     ?? throw new InvalidOperationException("AzureAd:ClientId is not configured.");
string clientSecret = config["AzureAd:ClientSecret"] ?? throw new InvalidOperationException("AzureAd:ClientSecret is not configured.");

// ─────────────────────────────────────────────────────────────────────────────
// Input – command-line (scheduled) or interactive
// ─────────────────────────────────────────────────────────────────────────────
// The tool supports two modes so it can be used both by administrators running
// it manually and by automated schedulers (Windows Task Scheduler, cron, etc.).
//   • Scheduled mode  : arguments are passed on the command line — no stdin needed.
//   • Interactive mode: when no arguments are passed the user is prompted in the
//                       terminal, which is more ergonomic for one-off ad-hoc runs.
string operation;   // "add" | "remove"  – what to do with the users in the CSV
string csvPath;     // path to the CSV file containing the list of users
string groupInput;  // target security group – either a display name or an object GUID
string? logPath = null;  // optional file path for structured log output (null = console only)

if (args.Length > 0)
{
    // ── Scheduled / non-interactive mode ──────────────────────────────────
    // The first positional argument is the operation verb (add / remove).
    // Named flags (--csv, --group, --log) can appear in any order after it.
    operation = args[0].ToLowerInvariant();
    if (operation is not ("add" or "remove"))
    {
        Console.Error.WriteLine("ERROR: First argument must be 'add' or 'remove'.");
        PrintUsage();
        Environment.Exit(1);  // Exit code 1 = bad invocation / configuration error
    }

    // GetArg scans for each named flag and returns the value that follows it.
    csvPath    = GetArg(args, "--csv")   ?? string.Empty;
    groupInput = GetArg(args, "--group") ?? string.Empty;
    logPath    = GetArg(args, "--log");  // optional – null when flag is absent

    // Both --csv and --group are mandatory for unattended operation; bail early
    // with a clear message rather than letting the run fail halfway through.
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
    // No arguments were supplied, so we prompt the user for each required input.
    // Each input is validated immediately so the user gets instant feedback
    // instead of waiting until the Graph API call to discover a mistake.
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

    // Log file is optional – pressing Enter without typing anything skips it.
    Console.Write("Log file path (optional): ");
    var logInput = Console.ReadLine()?.Trim();
    if (!string.IsNullOrEmpty(logInput)) logPath = logInput;

    Console.WriteLine();
}

// ─────────────────────────────────────────────────────────────────────────────
// Logging – console + optional append-mode file (suitable for scheduled runs)
// ─────────────────────────────────────────────────────────────────────────────
// Every message is always printed to the console so an operator running the tool
// manually can see what is happening in real time.
//
// When a --log path is provided the same messages are APPENDED to that file
// with a timestamp prefix.  Append mode (rather than overwrite) is intentional:
// a scheduled daily task accumulates all runs in a single audit log file, making
// it easy to look back at historical runs without managing multiple log files.
//
// AutoFlush = true ensures that each line is written to disk immediately, so the
// log is not lost if the process is killed mid-run or the host machine crashes.
StreamWriter? logWriter = null;
if (logPath is not null)
{
    try
    {
        logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
    }
    catch (Exception ex)
    {
        // Treat a log-file open failure as a non-fatal warning: the run can still
        // proceed and report results to the console.  This avoids blocking the
        // actual group-management work over a logging misconfiguration.
        Console.Error.WriteLine($"WARNING: Cannot open log file '{logPath}': {ex.Message}");
    }
}

// Log() is a local function (closure) so it has access to logWriter without
// needing to pass it as a parameter to every helper method.
void Log(string message)
{
    Console.WriteLine(message);
    // Write to file only when a log writer was successfully opened above.
    // The timestamp is added here (not in the caller) so all file entries are
    // consistently formatted regardless of where Log() is called from.
    logWriter?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
}

// LogSection() adds a visible horizontal separator to make multi-phase runs
// easy to skim in both the console and the log file.
void LogSection(string title)
{
    Log($"\n── {title} ─────────────────────────────────────────────────────");
}

// ─────────────────────────────────────────────────────────────────────────────
// Read & validate CSV
// ─────────────────────────────────────────────────────────────────────────────
// Guard clauses before we attempt any Graph API calls:
//   1. Verify the file actually exists on disk (catches typos in the path).
//   2. Verify at least one UPN was parsed from the file (catches empty or
//      malformed CSVs that would otherwise silently succeed with zero work).
if (!File.Exists(csvPath))
{
    Console.Error.WriteLine($"ERROR: CSV file not found: '{csvPath}'");
    Environment.Exit(1);
}

// ReadUpnsFromCsv handles various CSV shapes – see the method below for details.
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
// ClientSecretCredential implements the OAuth 2.0 client-credentials flow:
//   POST /tenantId/oauth2/v2.0/token  →  receives a bearer token automatically.
// The SDK handles token caching and renewal, so we never manage tokens manually.
//
// The scope "https://graph.microsoft.com/.default" tells Entra to issue a token
// with ALL app-level permissions that were granted in the portal (i.e. the
// User.Read.All, GroupMember.ReadWrite.All, and Group.Read.All permissions
// listed at the top of the file).  Using .default is the correct pattern for
// client-credential (app-only) flows.
var credential  = new ClientSecretCredential(tenantId, clientId, clientSecret);
var graphClient = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);

// ─────────────────────────────────────────────────────────────────────────────
// Resolve security group (name → object ID, or pass-through if already a GUID)
// ─────────────────────────────────────────────────────────────────────────────
// All Graph API member-management endpoints require the group's object ID (GUID),
// not its display name.  ResolveGroupIdAsync handles both input forms:
//   • GUID already supplied  → returned as-is (faster: no extra Graph call).
//   • Display name supplied  → looked up via Graph filter (see the method below).
//
// This is done once before we start processing users so that a bad group name
// is caught immediately, rather than after partially completing the user list.
string groupId;
try
{
    groupId = await ResolveGroupIdAsync(graphClient, groupInput);
    Log($"  Group ID   : {groupId}");  // log the resolved GUID for audit traceability
}
catch (Exception ex)
{
    // Fatal error – we cannot continue without a valid target group.
    // Close the log writer so any buffered data is flushed before we exit.
    Console.Error.WriteLine($"\nERROR: Could not resolve group '{groupInput}': {ex.Message}");
    logWriter?.Close();
    Environment.Exit(1);
    return;  // satisfies the compiler's definite assignment analysis for groupId
}

// ─────────────────────────────────────────────────────────────────────────────
// Process users
// ─────────────────────────────────────────────────────────────────────────────
// We iterate over every UPN from the CSV and perform the requested operation.
// Three counters track outcomes across the entire run:
//   succeeded – Graph API call completed successfully.
//   skipped   – the desired state was already true (idempotent no-op).
//   failed    – an unexpected error occurred; the user was NOT processed.
LogSection($"Processing {upns.Count} user(s)");

int succeeded = 0, skipped = 0, failed = 0;

foreach (string upn in upns)
{
    try
    {
        // Step 1 – Look up the user by UPN (or object ID) to retrieve their
        // immutable object ID.  The Graph member-management endpoints require an
        // object ID, not a UPN, so this lookup is always necessary.
        // We request only the three fields we actually use (Select) to minimise
        // the response payload and avoid over-fetching sensitive attributes.
        User? user = await graphClient.Users[upn].GetAsync(req =>
        {
            req.QueryParameters.Select = ["id", "displayName", "userPrincipalName"];
        });

        // A null result or missing Id means the account does not exist in this
        // tenant.  We log it as a skip (not a failure) because it is normal for
        // an off-boarding CSV to contain accounts that have already been deleted.
        if (user?.Id is null)
        {
            Log($"  [SKIP] {upn,-45} – user not found in directory.");
            skipped++;
            continue;
        }

        // Build a human-readable display string for the log (UPN + display name).
        string display = $"{user.UserPrincipalName} ({user.DisplayName})";

        if (operation == "add")
        {
            // Step 2a – Add the user to the group.
            // The Graph API requires a "directory object reference" body whose
            // OdataId points to the user's directoryObjects URL.  This is the
            // standard way to establish a membership link without sending the
            // full user object.
            await graphClient.Groups[groupId].Members.Ref.PostAsync(new ReferenceCreate
            {
                OdataId = $"https://graph.microsoft.com/v1.0/directoryObjects/{user.Id}"
            });
            Log($"  [OK]   Added   {display}");
        }
        else
        {
            // Step 2b – Remove the user from the group.
            // DELETE /groups/{groupId}/members/{userId}/$ref  removes only the
            // membership link; it does NOT delete the user object itself.
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
        // The user is already a member of the group – this is an idempotent no-op.
        // Logging it as SKIP (not FAIL) allows the same CSV to be re-run safely
        // after a partial failure without producing misleading error counts.
        Log($"  [SKIP] {upn,-45} – already a member.");
        skipped++;
    }
    catch (ODataError odataEx) when (
        operation == "remove"
        && (odataEx.Error?.Code == "Request_BadRequest" || odataEx.Error?.Code == "404")
        && (odataEx.Error.Message?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) == true
            || odataEx.Error.Message?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true))
    {
        // The user is not a member of the group – nothing to remove.
        // Same idempotency reasoning as the "add" handler above: re-running an
        // off-boarding CSV should not report errors for already-removed users.
        Log($"  [SKIP] {upn,-45} – not a member.");
        skipped++;
    }
    catch (ODataError odataEx)
    {
        // An OData error that does NOT match the known idempotency patterns above –
        // e.g. permission denied, throttling, or a malformed request.  We log the
        // error code and message for diagnosis and increment the failure counter.
        Log($"  [FAIL] {upn,-45} – Graph error [{odataEx.Error?.Code}]: {odataEx.Error?.Message}");
        failed++;
    }
    catch (Exception ex)
    {
        // Catch-all for unexpected non-OData errors (network timeouts, etc.).
        // We continue to the next user rather than aborting the whole run, so a
        // single transient error does not prevent the remaining users from being
        // processed.
        Log($"  [FAIL] {upn,-45} – {ex.Message}");
        failed++;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Summary
// ─────────────────────────────────────────────────────────────────────────────
// Print a structured summary so operators can quickly verify the run outcome
// and so monitoring systems (or human reviewers) can parse the log file.
LogSection("Summary");
Log($"  Operation  : {operation.ToUpper()}");
Log($"  Group      : {groupInput}");
Log($"  Total      : {upns.Count}");
Log($"  Succeeded  : {succeeded}");
Log($"  Skipped    : {skipped}  (already member / not member / not found)");
Log($"  Failed     : {failed}");
Log(string.Empty);

// Flush and close the log file before exiting so the OS releases the file handle
// cleanly.  (AutoFlush already writes each line, but Close() finalises the stream.)
logWriter?.Close();

// Structured exit codes allow callers (schedulers, CI pipelines) to distinguish
// outcomes without parsing the log text:
//   0 – success: every user was processed or was already in the desired state.
//   2 – partial/full failure: at least one user could not be processed.
// Exit code 1 is reserved for configuration / invocation errors (see above).
Environment.Exit(failed > 0 ? 2 : 0);

// =============================================================================
// Helper methods
// =============================================================================

/// <summary>
/// Scans <paramref name="args"/> for a named flag (e.g. "--csv") and returns
/// the immediately following token as its value.
/// </summary>
/// <remarks>
/// The search is case-insensitive so "--CSV" and "--csv" are treated the same.
/// The loop stops at <c>args.Length - 1</c> to avoid an index-out-of-range
/// when the flag appears as the very last token with no value after it.
/// Returns <c>null</c> when the flag is absent, which callers use to detect
/// that an optional argument was not provided.
/// </remarks>
static string? GetArg(string[] args, string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];  // the token right after the flag is its value
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
/// Reads user principal names (UPNs) or object IDs from a CSV file and returns
/// them as a list of strings ready to be passed to the Graph API.
/// </summary>
/// <remarks>
/// The method intentionally supports a variety of real-world CSV shapes so that
/// administrators can feed exports from HR systems, Azure AD, or hand-crafted
/// spreadsheets without needing to reformat them:
///
///   Shape 1 – Named header row:
///     The first row is detected as a header if one of its columns matches a
///     known alias (UserPrincipalName, UPN, Email, Mail, User – case-insensitive).
///     Data starts from row 2, and the matching column is used across all rows.
///
///   Shape 2 – Headerless single-column file:
///     If the first line contains '@' it is assumed to be a UPN rather than a
///     header, so it is treated as the first data row and column 0 is used.
///
///   Shape 3 – Unknown / unrecognised header:
///     If the first row looks like a header (no '@') but none of its columns
///     match the known list, the first row is skipped and column 0 is used.
///
/// Lines beginning with '#' are treated as comments and ignored, allowing
/// operators to annotate the CSV without causing lookup errors.
/// </remarks>
static List<string> ReadUpnsFromCsv(string path)
{
    // Read the file in one shot and strip blank lines upfront to avoid
    // accidentally counting empty trailing rows in the result.
    var lines = File.ReadAllLines(path)
        .Where(l => !string.IsNullOrWhiteSpace(l))
        .ToList();

    if (lines.Count == 0) return [];  // empty file – caller will exit with an error

    var firstLine = lines[0];
    // Split on comma and trim surrounding whitespace / Excel-style double-quotes
    // from each header token before comparing to the known header list.
    var headers   = firstLine.Split(',').Select(h => h.Trim().Trim('"')).ToList();

    string[] knownHeaders = ["userprincipalname", "upn", "email", "mail", "user"];
    int upnColumnIndex = -1;  // -1 = not yet found

    // Scan columns left-to-right and stop at the first recognised header so
    // that multi-column CSVs with other irrelevant columns are handled correctly.
    for (int i = 0; i < headers.Count; i++)
    {
        if (knownHeaders.Contains(headers[i].ToLowerInvariant()))
        {
            upnColumnIndex = i;
            break;
        }
    }

    // Determine which row data starts at and which column index to read from.
    int startLine;
    if (upnColumnIndex >= 0)
    {
        // Shape 1: a recognised header was found – skip row 0 (it's the header)
        // and use the column we identified above.
        startLine = 1;
    }
    else if (firstLine.Contains('@'))
    {
        // Shape 2: the first line contains '@', so it looks like a real UPN
        // rather than a header.  Include it in the results and default to column 0.
        upnColumnIndex = 0;
        startLine = 0;
    }
    else
    {
        // Shape 3: the first line looks like an unrecognised header (no '@').
        // Skip it and fall back to column 0 for all subsequent rows.
        upnColumnIndex = 0;
        startLine = 1;
    }

    var result = new List<string>();
    for (int i = startLine; i < lines.Count; i++)
    {
        var cols  = lines[i].Split(',');
        if (upnColumnIndex < cols.Length)
        {
            // Trim whitespace and surrounding quotes from the value before adding it.
            var value = cols[upnColumnIndex].Trim().Trim('"');

            // Skip empty cells and comment lines (lines starting with '#') so that
            // operators can leave notes in the CSV without polluting the user list.
            if (!string.IsNullOrEmpty(value) && !value.StartsWith('#'))
                result.Add(value);
        }
    }

    return result;
}

/// <summary>
/// Resolves a group display name or object GUID to the group's Entra object ID.
/// </summary>
/// <param name="client">An authenticated Graph client with Group.Read.All permission.</param>
/// <param name="groupInput">Either a display name (e.g. "Finance – Read Only") or an
/// object ID GUID (e.g. "a1b2c3d4-...").</param>
/// <returns>The group's object ID as a string.</returns>
/// <exception cref="InvalidOperationException">
/// Thrown when the group cannot be found, or when the name is ambiguous (more than one
/// security group shares the same display name).
/// </exception>
/// <remarks>
/// Security groups are targeted by object ID in all membership endpoints, yet operators
/// often know a group by its human-readable name.  This method bridges that gap.
///
/// Ambiguity detection (Top = 2 + count check) is intentional: if two groups share a
/// display name the caller should be forced to supply the unambiguous GUID rather than
/// silently operating on the wrong group.
/// </remarks>
static async Task<string> ResolveGroupIdAsync(GraphServiceClient client, string groupInput)
{
    // Fast path: if the input is already a valid GUID it must be an object ID,
    // so skip the Graph call entirely and return it directly.
    if (Guid.TryParse(groupInput, out _))
        return groupInput;

    // OData filter strings use single-quoted string literals.  A display name
    // containing a single-quote (e.g. "IT Staff's Group") would break the filter
    // syntax, so we escape each single-quote by doubling it ('') per OData spec.
    string safeName = groupInput.Replace("'", "''");

    // Request at most 2 results:
    //   • 0 results  → group not found  (throw)
    //   • 1 result   → unambiguous match (return its Id)
    //   • 2 results  → ambiguous name    (throw and ask for a GUID)
    // Filtering on securityEnabled eq true ensures we only match security groups
    // and not Microsoft 365 groups, distribution lists, or mail-enabled groups.
    var result = await client.Groups.GetAsync(req =>
    {
        req.QueryParameters.Filter = $"displayName eq '{safeName}' and securityEnabled eq true";
        req.QueryParameters.Select = ["id", "displayName"];  // only fetch what we need
        req.QueryParameters.Top    = 2;  // see ambiguity comment above
    });

    var groups = result?.Value;
    if (groups is null || groups.Count == 0)
        throw new InvalidOperationException($"No security group named '{groupInput}' was found.");

    // Two or more groups share this display name – it is unsafe to pick one at random.
    // The operator must re-run with the specific object ID.
    if (groups.Count > 1)
        throw new InvalidOperationException(
            $"Multiple groups named '{groupInput}' exist. Please use the Object ID instead.");

    // Id should never be null for a real group returned by Graph, but the Graph SDK
    // models it as nullable, so we defend here to satisfy the compiler and make any
    // unexpected Graph behaviour immediately visible.
    return groups[0].Id ?? throw new InvalidOperationException("Group returned a null ID.");
}
