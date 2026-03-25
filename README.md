# Entra Security Group – Add Member (C# Console App)

A .NET 8 console application that adds a Microsoft Entra ID user to a security group using the **Microsoft Graph API** with the **Client Credentials (application)** OAuth 2.0 flow.

---

## Table of Contents

- [Architecture & Flow](#architecture--flow)
- [Prerequisites](#prerequisites)
- [Step 1 – Register the App in Microsoft Entra ID](#step-1--register-the-app-in-microsoft-entra-id)
- [Step 2 – Configure API Permissions](#step-2--configure-api-permissions)
- [Step 3 – Grant Admin Consent](#step-3--grant-admin-consent)
- [Step 4 – Configure the Application](#step-4--configure-the-application)
- [Step 5 – Build and Run](#step-5--build-and-run)
- [Project Structure](#project-structure)
- [Code Logic Walkthrough](#code-logic-walkthrough)
- [Error Handling](#error-handling)
- [Security Best Practices](#security-best-practices)
- [Troubleshooting](#troubleshooting)

---

## Architecture & Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                     Console Application                         │
│                                                                 │
│  1. Read config  ──►  appsettings.json / env vars               │
│  2. Prompt user  ──►  UPN or Object ID + Group Object ID        │
│  3. Authenticate ──►  Azure.Identity (ClientSecretCredential)   │
│  4. Call Graph   ──►  GET  /users/{upn-or-id}                   │
│                  ──►  POST /groups/{groupId}/members/$ref        │
└─────────────────────────────────────────────────────────────────┘
          │                          │
          ▼                          ▼
   Microsoft Entra ID         Microsoft Graph API
   (issues access token)      (handles directory changes)
```

### OAuth 2.0 Client Credentials Flow

This app uses **application permissions** (no user sign-in required). The flow is:

```
App  ──POST──►  https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token
               Body: client_id, client_secret, scope=https://graph.microsoft.com/.default
     ◄──────── Access Token (JWT, ~1 hour)

App  ──GET───►  https://graph.microsoft.com/v1.0/users/{upnOrId}
     ◄──────── User object (id, displayName, userPrincipalName)

App  ──POST──►  https://graph.microsoft.com/v1.0/groups/{groupId}/members/$ref
               Body: { "@odata.id": "https://graph.microsoft.com/v1.0/directoryObjects/{userId}" }
     ◄──────── 204 No Content (success)
```

---

## Prerequisites

| Requirement | Details |
|---|---|
| .NET SDK | 8.0 or later – [Download](https://dotnet.microsoft.com/download) |
| Azure Subscription | With access to Microsoft Entra ID |
| Entra ID Role | **Application Administrator** (or higher) to register apps and grant consent |

---

## Step 1 – Register the App in Microsoft Entra ID

### Portal Method

1. Sign in to the [Azure Portal](https://portal.azure.com).
2. Navigate to **Microsoft Entra ID** → **App registrations** → **+ New registration**.
3. Fill in the form:
   - **Name**: `EntraGroupManager` (or any descriptive name)
   - **Supported account types**: `Accounts in this organizational directory only`
   - **Redirect URI**: leave blank (not needed for daemon apps)
4. Click **Register**.
5. On the **Overview** page, note down:
   - **Application (client) ID** → this is your `ClientId`
   - **Directory (tenant) ID** → this is your `TenantId`

### Azure CLI Method

```bash
# Create the app registration
az ad app create --display-name "EntraGroupManager"

# Note the appId (ClientId) and the tenant from `az account show`
az ad app list --display-name "EntraGroupManager" --query "[].{AppId:appId}" -o table
az account show --query tenantId -o tsv

# Create a service principal for the app
az ad sp create --id <AppId>
```

---

## Step 2 – Configure API Permissions

The application requires two **Microsoft Graph application permissions**:

| Permission | Type | Purpose |
|---|---|---|
| `User.Read.All` | Application | Look up a user by UPN or Object ID |
| `GroupMember.ReadWrite.All` | Application | Add/remove members in a security group |

> **Why application permissions?**  
> This app runs as a background service with no user signing in. Application permissions allow it to act on behalf of itself using a client secret, without requiring interactive login.

### Portal Method

1. In your app registration, go to **API permissions** → **+ Add a permission**.
2. Select **Microsoft Graph** → **Application permissions**.
3. Search for and select `User.Read.All`, then click **Add permissions**.
4. Repeat and add `GroupMember.ReadWrite.All`.

### Azure CLI Method

```bash
# Get the Microsoft Graph service principal object ID
GRAPH_SP_ID=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)

# Get the app's service principal object ID
APP_SP_ID=$(az ad sp show --id <AppId> --query id -o tsv)

# Add User.Read.All (id: df021288-bdef-4463-88db-98f22de89214)
az ad app permission add \
  --id <AppId> \
  --api 00000003-0000-0000-c000-000000000000 \
  --api-permissions df021288-bdef-4463-88db-98f22de89214=Role

# Add GroupMember.ReadWrite.All (id: 62a82d76-70ea-41e2-9197-370581804d09)
az ad app permission add \
  --id <AppId> \
  --api 00000003-0000-0000-c000-000000000000 \
  --api-permissions 62a82d76-70ea-41e2-9197-370581804d09=Role
```

---

## Step 3 – Grant Admin Consent

Application permissions require **admin consent** before they can be used.

### Portal Method

1. In **API permissions**, click **Grant admin consent for \<your tenant\>**.
2. Confirm by clicking **Yes**.
3. The status columns should now show a green checkmark: ✅ **Granted for \<tenant\>**

### Azure CLI Method

```bash
az ad app permission admin-consent --id <AppId>
```

---

## Step 4 – Configure the Application

### Create a Client Secret

1. In your app registration, go to **Certificates & secrets** → **+ New client secret**.
2. Set a **Description** (e.g., `EntraGroupManager-secret`) and an **Expiry** (recommended: 6 or 12 months).
3. Click **Add**.
4. **Copy the secret Value immediately** — it is only shown once.

#### Azure CLI Method

```bash
az ad app credential reset --id <AppId> --years 1
# Output includes: "password" (the secret), "appId", "tenant"
```

### Set Configuration Values

Edit [appsettings.json](appsettings.json) and replace the placeholders:

```json
{
  "AzureAd": {
    "TenantId":     "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "ClientId":     "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "ClientSecret": "your-secret-value-here"
  }
}
```

> ⚠️ **Never commit `appsettings.json` with real secrets to source control.**  
> Use environment variables instead (see below).

### Using Environment Variables (Recommended)

Override any config value with an environment variable prefixed `ENTRA_`:

```powershell
# PowerShell
$env:ENTRA_AzureAd__TenantId     = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
$env:ENTRA_AzureAd__ClientId     = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
$env:ENTRA_AzureAd__ClientSecret = "your-secret-value"
dotnet run
```

```bash
# bash / zsh
export ENTRA_AzureAd__TenantId="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
export ENTRA_AzureAd__ClientId="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
export ENTRA_AzureAd__ClientSecret="your-secret-value"
dotnet run
```

---

## Step 5 – Build and Run

```powershell
cd C:\EntraGroupManager
dotnet restore
dotnet build
dotnet run
```

### Example Session

```
=== Entra Security Group – Add Member ===

Enter the user UPN or Object ID  : john.doe@contoso.com
Enter the security group Object ID: 11111111-2222-3333-4444-555555555555

Looking up user 'john.doe@contoso.com'...
  Display name : John Doe
  UPN          : john.doe@contoso.com
  Object ID    : aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee

Adding user to group '11111111-2222-3333-4444-555555555555'...

SUCCESS: User has been added to the security group.
```

### Finding the Group Object ID

In the Azure Portal, navigate to:  
**Microsoft Entra ID** → **Groups** → select your group → the **Object ID** is on the **Overview** page.

Or use Azure CLI:

```bash
az ad group show --group "My Security Group" --query id -o tsv
```

---

## Project Structure

```
EntraGroupManager/
├── EntraGroupManager.csproj   # Project file with NuGet dependencies
├── Program.cs                 # Application entry point and core logic
├── appsettings.json           # Configuration file (do NOT commit secrets)
└── README.md                  # This file
```

### NuGet Packages

| Package | Version | Purpose |
|---|---|---|
| `Azure.Identity` | 1.13.2 | `ClientSecretCredential` for OAuth 2.0 token acquisition |
| `Microsoft.Graph` | 5.68.0 | Strongly-typed Microsoft Graph SDK v5 |
| `Microsoft.Extensions.Configuration.Json` | 8.0.1 | `appsettings.json` support |
| `Microsoft.Extensions.Configuration.EnvironmentVariables` | 8.0.0 | Environment variable overrides |

---

## Code Logic Walkthrough

### 1. Configuration Loading

```csharp
IConfiguration config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables(prefix: "ENTRA_")
    .Build();
```

Environment variables take precedence over `appsettings.json`, so secrets can be injected at runtime without modifying the file.

### 2. Authentication (Client Credentials)

```csharp
var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
var graphClient = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
```

`ClientSecretCredential` from `Azure.Identity` handles token acquisition and automatic refresh. The scope `https://graph.microsoft.com/.default` instructs Entra to issue a token with all the application permissions that were admin-consented.

### 3. User Resolution

```csharp
User? user = await graphClient.Users[userInput].GetAsync(req =>
{
    req.QueryParameters.Select = ["id", "displayName", "userPrincipalName"];
});
```

The Graph API accepts both a UPN (e.g., `user@contoso.com`) and a GUID Object ID in the `{userInput}` path segment, making the app flexible.

### 4. Adding Group Membership

```csharp
await graphClient.Groups[groupId].Members.Ref.PostAsync(new ReferenceCreate
{
    OdataId = $"https://graph.microsoft.com/v1.0/directoryObjects/{user.Id}"
});
```

This calls `POST /v1.0/groups/{groupId}/members/$ref`, which is the Graph API endpoint for adding a directory object as a group member. The body is an OData reference pointing to the user's directory object.

---

## Error Handling

| Scenario | Behaviour |
|---|---|
| User already in group | Prints an INFO message; exits cleanly (no error code) |
| User not found | Prints an ERROR message; exits with code 1 |
| Group not found | ODataError from Graph is caught; error code & message displayed |
| Missing configuration | `InvalidOperationException` thrown at startup |
| Graph API errors | `ODataError` caught; code and message printed |
| Unexpected errors | Generic `Exception` caught; message printed |

---

## Security Best Practices

| Practice | Recommendation |
|---|---|
| **Never commit secrets** | Add `appsettings.json` to `.gitignore` or keep only placeholder values |
| **Use short-lived secrets** | Set expiry ≤ 12 months; rotate before expiry |
| **Prefer certificates** | For production, use a certificate instead of a client secret |
| **Use Managed Identity** | If running on Azure (VM, Container App, Function), use `ManagedIdentityCredential` instead |
| **Least privilege** | Only grant `User.Read.All` and `GroupMember.ReadWrite.All`; nothing broader |
| **Audit logs** | Enable Microsoft Entra ID audit logs to track group membership changes |
| **Key Vault** | Store the client secret in Azure Key Vault and reference it at runtime |

### .gitignore Recommendation

Add this to your `.gitignore` to prevent accidental secret leaks:

```gitignore
# Entra config with real secrets
appsettings*.json
!appsettings.example.json
```

---

## Troubleshooting

| Error | Likely Cause | Fix |
|---|---|---|
| `AADSTS700016` – Application not found | Wrong `ClientId` or `TenantId` | Double-check values in Azure Portal → App registrations |
| `AADSTS7000215` – Invalid client secret | Secret expired or incorrect | Regenerate in **Certificates & secrets** |
| `Authorization_RequestDenied` | Admin consent not granted | Re-run Step 3 (grant admin consent) |
| `Request_ResourceNotFound` on user | User does not exist in the tenant | Verify the UPN or Object ID |
| `Request_ResourceNotFound` on group | Wrong group Object ID | Verify the group Object ID in the portal |
| `Request_BadRequest` – One or more added object references already exist | User already in group | Expected; app handles this gracefully |
| `Insufficient privileges` | Wrong permission type (delegated vs application) | Ensure you added **Application** (not Delegated) permissions |
