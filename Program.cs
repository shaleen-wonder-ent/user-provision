using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

// ---------------------------------------------------------------------------
// Load configuration from appsettings.json and environment variables.
// Environment variables prefixed with ENTRA_ override appsettings values.
//   e.g.  ENTRA_AzureAd__TenantId, ENTRA_AzureAd__ClientId, ENTRA_AzureAd__ClientSecret
// ---------------------------------------------------------------------------
IConfiguration config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "ENTRA_")
    .Build();

string tenantId     = config["AzureAd:TenantId"]     ?? throw new InvalidOperationException("AzureAd:TenantId is not configured.");
string clientId     = config["AzureAd:ClientId"]     ?? throw new InvalidOperationException("AzureAd:ClientId is not configured.");
string clientSecret = config["AzureAd:ClientSecret"] ?? throw new InvalidOperationException("AzureAd:ClientSecret is not configured.");

// ---------------------------------------------------------------------------
// Collect inputs
// ---------------------------------------------------------------------------
Console.WriteLine("=== Entra Security Group – Add Member ===");
Console.WriteLine();

Console.Write("Enter the user UPN or Object ID  : ");
string userInput = Console.ReadLine()?.Trim() ?? string.Empty;
if (string.IsNullOrEmpty(userInput))
{
    Console.Error.WriteLine("ERROR: User identifier cannot be empty.");
    Environment.Exit(1);
}

Console.Write("Enter the security group Object ID: ");
string groupId = Console.ReadLine()?.Trim() ?? string.Empty;
if (string.IsNullOrEmpty(groupId))
{
    Console.Error.WriteLine("ERROR: Group Object ID cannot be empty.");
    Environment.Exit(1);
}

// ---------------------------------------------------------------------------
// Build Graph client using Client Credentials (application permissions)
// Required application permissions (grant admin consent in Entra portal):
//   • User.Read.All            – resolve user UPN → object ID
//   • GroupMember.ReadWrite.All – add member to group
// ---------------------------------------------------------------------------
var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
var graphClient = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);

try
{
    // ------------------------------------------------------------------
    // Step 1: Resolve the user (works for both UPN and object ID)
    // ------------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine($"Looking up user '{userInput}'...");

    User? user = await graphClient.Users[userInput].GetAsync(req =>
    {
        req.QueryParameters.Select = ["id", "displayName", "userPrincipalName"];
    });

    if (user?.Id is null)
    {
        Console.Error.WriteLine($"ERROR: User '{userInput}' was not found.");
        Environment.Exit(1);
    }

    Console.WriteLine($"  Display name : {user.DisplayName}");
    Console.WriteLine($"  UPN          : {user.UserPrincipalName}");
    Console.WriteLine($"  Object ID    : {user.Id}");

    // ------------------------------------------------------------------
    // Step 2: Add the user as a member of the security group
    // ------------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine($"Adding user to group '{groupId}'...");

    await graphClient.Groups[groupId].Members.Ref.PostAsync(new ReferenceCreate
    {
        OdataId = $"https://graph.microsoft.com/v1.0/directoryObjects/{user.Id}"
    });

    Console.WriteLine();
    Console.WriteLine("SUCCESS: User has been added to the security group.");
}
catch (ODataError odataEx)
    when (odataEx.Error?.Code == "Request_BadRequest"
          && odataEx.Error.Message?.Contains("already exist", StringComparison.OrdinalIgnoreCase) == true)
{
    // Graph returns this specific error when the user is already a member
    Console.WriteLine();
    Console.WriteLine("INFO: User is already a member of this security group. No changes made.");
}
catch (ODataError odataEx)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"ERROR (Graph API) [{odataEx.Error?.Code}]: {odataEx.Error?.Message}");
    Environment.Exit(1);
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"ERROR (Unexpected): {ex.Message}");
    Environment.Exit(1);
}
