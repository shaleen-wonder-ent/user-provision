# Entra Security Group – Bulk User Manager

A .NET 8 console application that **adds or removes bulk users** (from a CSV file) to/from a Microsoft Entra ID security group using the **Microsoft Graph API** with the **Client Credentials** OAuth 2.0 flow.

Designed to run interactively **or** fully unattended via Windows Task Scheduler, cron, Azure Automation, or any CI/CD pipeline.

---

## Table of Contents

- [Architecture & Flow](#architecture--flow)
- [Prerequisites](#prerequisites)
- [CSV File Format](#csv-file-format)
- [Step 1 – Register the App in Microsoft Entra ID](#step-1--register-the-app-in-microsoft-entra-id)
- [Step 2 – Configure API Permissions](#step-2--configure-api-permissions)
- [Step 3 – Grant Admin Consent](#step-3--grant-admin-consent)
- [Step 4 – Configure the Application](#step-4--configure-the-application)
- [Step 5 – Build and Run](#step-5--build-and-run)
- [Scheduling the Application](#scheduling-the-application)
- [Exit Codes](#exit-codes)
- [Project Structure](#project-structure)
- [Error Handling](#error-handling)
- [Security Best Practices](#security-best-practices)
- [Troubleshooting](#troubleshooting)

---

## Architecture & Flow

> **View / edit the diagrams live:**
> - [Application Flow →  mermaid.live](https://mermaid.live/edit#pako:eNqVVU1v2zAM_SuCThuQJM7XUgy7FNuGHXYoitsugyFLtC1UlgxLaZsh_32UZNlJ03TYLpZIPj4-UqKfrBCKWcGa1Z5vNAtdUVRpGFa2E2YfqiEi_2aN8G3xAHqxUvUoJYXKOa3K5i9j0hfyRENarv2q2qJVqqVVZVFgIDRALPqtaW0mYdklXqpLKW3zzM0avMtBNKc0hRyFYBnKwpJJiCIcLdgJCiBRlZjkPJBWKLBIwAiGhZqLc6SyHWF2eiHKXbFCQRoT6bFv0R9KFDHBLRUvCYVkyGKyUMPqCiJmjNcbFjjMCxq7LvEMMRJCLBLo8JA6yw1nBoIJCaRfBzawGVNBsVR8p9DqPaYQS8xFpS4M3O3UkBZiYyX0K9K9J3EVv8hYJIHoFSqfwxYZeERNmpPkFzTIIc-DhnnuWQvKjJo5Zu0grUW-5IJRN8p7PBJVDhYKJZZRIcXWI0KJJvl4fEDsmyy_I3WXbsrIJ_5pJbqd1xUJ1j6S3pMjqPUVA7_3U0FHBJdYMJjKRHl6TnxPT3Q4BIl0jDzMxFXXr5JVxKlBRhlOw2NJGjJOGcYWjnFGoREJLJQ4aN4Y5b_JX3c4XYXCflCFe6j-S1aMSSRFZJTFyFCCaxlCIxloqhFLqHIDtOLy0NTy9IlCihHrVVuNJiI4l2M4bI5TuoZvnROX9iFnL0NX_-CK6o)
> - [Scheduling Flow → mermaid.live](https://mermaid.live/edit#pako:eNp1kU9PwzAMxb9K5BNI7UAphx6QJg7bYRL_Log4pKlXIpok1M6GEPvupK27AgcuiZ_t9_yzU3TWEhbYNk3DOXF0jl-XYUN85bqmBp8xB_nEFiRQXEK9LGYeJGXnpC3p4ZIZM7-WkyZiCZETOQhwV8UxEi0-OzIWVvjSbuwepF11AJGWgQ0BtL0jkYjAI1aJt7qHE1Dg7a5PFqKgTasqkH2e5d-M0RcX8HSmYVSj8TJ3cxrjVzOxpMeqBH5IVLmpakYyIjVkLvf3_8A_sPIuOL68VH7DJBBb)

### Application Flow

```mermaid
flowchart TD
    A([Start]) --> B{Arguments\nprovided?}

    B -- Yes --> C[Parse CLI args\nadd/remove · --csv · --group · --log]
    B -- No  --> D[Interactive prompts\noperation · csv path · group · log]

    C --> E[Load appsettings.json\n+ ENTRA_* env vars]
    D --> E

    E --> F[Read & parse CSV file\nextract UPN list]
    F --> G{CSV valid?}
    G -- No  --> ERR1([Exit 1 – fatal])
    G -- Yes --> H[Authenticate\nClientSecretCredential\nClient Credentials flow]

    H --> I[Resolve group\ndisplay name → Object ID\nvia Graph API]
    I --> J{Group found?}
    J -- No  --> ERR2([Exit 1 – fatal])
    J -- Yes --> K[/For each UPN in CSV/]

    K --> L[GET /users/:upn\nresolve UPN → Object ID]
    L --> M{User found?}
    M -- No  --> SKIP1[SKIP – user not found]
    M -- Yes --> N{Operation?}

    N -- add    --> O[POST /groups/:id/members/ref]
    N -- remove --> P[DELETE /groups/:id/members/:userId/ref]

    O --> Q{Already\na member?}
    Q -- Yes --> SKIP2[SKIP – already member]
    Q -- No  --> OK1[OK – added]

    P --> R{Not\na member?}
    R -- Yes --> SKIP3[SKIP – not a member]
    R -- No  --> OK2[OK – removed]

    SKIP1 & SKIP2 & SKIP3 & OK1 & OK2 --> S{More\nusers?}
    S -- Yes --> K
    S -- No  --> T[Write summary\nconsole + log file]

    T --> U{Any hard\nfailures?}
    U -- No  --> EXIT0([Exit 0 – success])
    U -- Yes --> EXIT2([Exit 2 – partial failure])

    style ERR1  fill:#e74c3c,color:#fff
    style ERR2  fill:#e74c3c,color:#fff
    style EXIT0 fill:#27ae60,color:#fff
    style EXIT2 fill:#e67e22,color:#fff
    style SKIP1 fill:#f39c12,color:#fff
    style SKIP2 fill:#f39c12,color:#fff
    style SKIP3 fill:#f39c12,color:#fff
    style OK1   fill:#2ecc71,color:#fff
    style OK2   fill:#2ecc71,color:#fff
```

### OAuth 2.0 Client Credentials Flow

```mermaid
sequenceDiagram
    autonumber
    participant App  as EntraGroupManager
    participant AAD  as Microsoft Entra ID<br/>(login.microsoftonline.com)
    participant Graph as Microsoft Graph API

    App  ->>  AAD  : POST /oauth2/v2.0/token<br/>client_id · client_secret · scope
    AAD  -->> App  : 200 OK – Access Token (JWT ~1 h)

    App  ->>  Graph : GET /groups?$filter=displayName eq 'GroupName'
    Graph -->> App  : 200 OK – group object ID

    loop For each UPN in CSV
        App  ->>  Graph : GET /users/{upnOrId}?$select=id,displayName,userPrincipalName
        Graph -->> App  : 200 OK – user object ID

        alt Operation = add
            App  ->>  Graph : POST /groups/{id}/members/$ref
            Graph -->> App  : 204 No Content
        else Operation = remove
            App  ->>  Graph : DELETE /groups/{id}/members/{userId}/$ref
            Graph -->> App  : 204 No Content
        end
    end

    App  ->>  App  : Write summary to console + log file
```

### Scheduling Flow

```mermaid
flowchart LR
    SCHED([Scheduler\nTask Scheduler · cron\nAzure Automation]) -->|runs on schedule| EXE

    subgraph EXE ["EntraGroupManager.exe"]
        direction TB
        A[Read config\n& args] --> B[Process CSV]
        B --> C[Call Graph API]
        C --> D[Append to log file]
    end

    EXE -->|Exit 0| OK([✓ All done])
    EXE -->|Exit 1| FATAL([✗ Fatal –\nalert admin])
    EXE -->|Exit 2| PARTIAL([⚠ Partial –\ncheck log])

    style OK      fill:#27ae60,color:#fff
    style FATAL   fill:#e74c3c,color:#fff
    style PARTIAL fill:#e67e22,color:#fff
```

---

## Prerequisites

| Requirement | Details |
|---|---|
| .NET SDK | 8.0 or later – [Download](https://dotnet.microsoft.com/download) |
| Azure Subscription | With access to Microsoft Entra ID |
| Entra ID Role | **Application Administrator** (or higher) to register apps and grant consent |

---

## CSV File Format

The CSV file must have **one user per row**. The application recognises the following column headers (case-insensitive):

| Accepted header names | Example value |
|---|---|
| `UserPrincipalName` | `john.doe@contoso.com` |
| `UPN` | `john.doe@contoso.com` |
| `Email` | `john.doe@contoso.com` |
| `Mail` | `john.doe@contoso.com` |
| `User` | `john.doe@contoso.com` |

Extra columns are ignored. If no recognised header is found, column 0 is used automatically, so a **headerless single-UPN-per-line file** also works.

### Example CSV files

**With header:**
```csv
UserPrincipalName,Department
alice@contoso.com,Engineering
bob@contoso.com,Finance
charlie@contoso.com,HR
```

**Headerless (one UPN per line):**
```csv
alice@contoso.com
bob@contoso.com
charlie@contoso.com
```

Lines starting with `#` are treated as comments and ignored.

---

## Step 1 – Register the App in Microsoft Entra ID

### Portal Method

1. Sign in to the [Azure Portal](https://portal.azure.com).
2. Navigate to **Microsoft Entra ID** → **App registrations** → **+ New registration**.
3. Fill in the form:
   - **Name**: `EntraGroupManager`
   - **Supported account types**: `Accounts in this organizational directory only`
   - **Redirect URI**: leave blank
4. Click **Register**.
5. On the **Overview** page, note:
   - **Application (client) ID** → `ClientId`
   - **Directory (tenant) ID** → `TenantId`

### Azure CLI Method

```bash
az ad app create --display-name "EntraGroupManager"
az ad sp create --id <AppId>
az account show --query tenantId -o tsv
```

---

## Step 2 – Configure API Permissions

In the app registration, go to **API permissions** → **Add a permission** → **Microsoft Graph** → **Application permissions** and add:

> Full permission reference: [Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)

| Permission | App Role ID (GUID) | Purpose |
|---|---|---|
| `User.Read.All` | `df021288-bdef-4463-88db-98f22de89214` | Look up users by UPN or object ID |
| `GroupMember.ReadWrite.All` | `dbaae8cf-10b5-4b86-a4a1-f871c94c6695` | Add and remove group members |
| `Group.Read.All` | `5b567255-7703-4780-807c-7be8301ae99b` | Resolve a group display name to its object ID |

The GUIDs above are the **stable app role IDs** for the Microsoft Graph service principal (`00000003-0000-0000-c000-000000000000`) and are identical in every Entra tenant.

### Azure CLI Method

```bash
# User.Read.All  (df021288-bdef-4463-88db-98f22de89214)
az ad app permission add --id <AppId> \
  --api 00000003-0000-0000-c000-000000000000 \
  --api-permissions df021288-bdef-4463-88db-98f22de89214=Role

# GroupMember.ReadWrite.All  (dbaae8cf-10b5-4b86-a4a1-f871c94c6695)
az ad app permission add --id <AppId> \
  --api 00000003-0000-0000-c000-000000000000 \
  --api-permissions dbaae8cf-10b5-4b86-a4a1-f871c94c6695=Role

# Group.Read.All  (5b567255-7703-4780-807c-7be8301ae99b)
az ad app permission add --id <AppId> \
  --api 00000003-0000-0000-c000-000000000000 \
  --api-permissions 5b567255-7703-4780-807c-7be8301ae99b=Role
```

---

## Step 3 – Grant Admin Consent

In the **API permissions** page, click **Grant admin consent for \<your tenant\>** and confirm.

```bash
az ad app permission admin-consent --id <AppId>
```

---

## Step 4 – Configure the Application

Copy `appsettings.example.json` → `appsettings.json` and fill in your values:

```json
{
  "AzureAd": {
    "TenantId":     "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "ClientId":     "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "ClientSecret": "your-client-secret-value"
  }
}
```

Alternatively, set environment variables (useful in CI/CD or containers):

```bash
# Windows
set ENTRA_AzureAd__TenantId=<value>
set ENTRA_AzureAd__ClientId=<value>
set ENTRA_AzureAd__ClientSecret=<value>

# Linux / macOS
export ENTRA_AzureAd__TenantId=<value>
export ENTRA_AzureAd__ClientId=<value>
export ENTRA_AzureAd__ClientSecret=<value>
```

---

## Step 5 – Build and Run

```bash
dotnet build
dotnet run                          # interactive mode
```

### Interactive mode

Running without arguments prompts for all inputs:

```
=== Entra Security Group – Bulk User Manager ===

Operation [add/remove]: add
CSV file path           : C:\Users\me\users.csv
Security group name/ID  : My-Security-Group
Log file path (optional): C:\logs\entra.log
```

### Non-interactive / command-line mode

```bash
# Add users
dotnet run -- add --csv .\users.csv --group "My-Security-Group" --log .\entra.log

# Remove users
dotnet run -- remove --csv .\users.csv --group "My-Security-Group" --log .\entra.log

# Group object ID is also accepted
dotnet run -- add --csv .\users.csv --group "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
```

After publishing, use the compiled executable directly:

```powershell
EntraGroupManager.exe add --csv .\users.csv --group "My-Security-Group" --log .\entra.log
```

---

## Scheduling the Application

Because the app runs non-interactively with command-line arguments, it integrates with any scheduler.

### Windows Task Scheduler

1. **Publish the app** to a fixed folder:
   ```powershell
   dotnet publish -c Release -r win-x64 --self-contained true -o C:\EntraGroupManager\publish
   ```

2. Open **Task Scheduler** → **Create Task**.

3. **General** tab:
   - Name: `Entra Group Sync`
   - Run whether user is logged on or not
   - Run with highest privileges

4. **Triggers** tab → **New**:
   - Choose your schedule (Daily, Weekly, etc.)

5. **Actions** tab → **New**:
   - **Program/script**: `C:\EntraGroupManager\publish\EntraGroupManager.exe`
   - **Arguments**: `add --csv C:\EntraGroupManager\users.csv --group "My-Security-Group" --log C:\logs\entra.log`
   - **Start in**: `C:\EntraGroupManager\publish`

6. Click **OK**, enter service-account credentials.

### Windows Task Scheduler via PowerShell

```powershell
$action  = New-ScheduledTaskAction `
    -Execute "C:\EntraGroupManager\publish\EntraGroupManager.exe" `
    -Argument 'add --csv C:\EntraGroupManager\users.csv --group "My-Security-Group" --log C:\logs\entra.log' `
    -WorkingDirectory "C:\EntraGroupManager\publish"

$trigger = New-ScheduledTaskTrigger -Daily -At "06:00AM"

$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 1)

Register-ScheduledTask `
    -TaskName "EntraGroupSync" `
    -Action   $action `
    -Trigger  $trigger `
    -Settings $settings `
    -RunLevel Highest
```

### Linux / macOS cron

```cron
# Add users every weekday at 06:00
0 6 * * 1-5 /opt/entra-group-manager/EntraGroupManager \
    add --csv /opt/entra-group-manager/users.csv \
    --group "My-Security-Group" \
    --log /var/log/entra-group-manager.log
```

### Azure Automation Runbook

1. Deploy the app to an Azure Automation Hybrid Worker or use a PowerShell runbook that calls the executable.
2. Store credentials as **Automation Variables** and pass them via `ENTRA_*` environment variables.

---

## Exit Codes

| Code | Meaning |
|---|---|
| `0` | All users processed successfully (succeeded + skipped). |
| `1` | Fatal error before processing started (bad args, missing CSV, group not found, auth failure). |
| `2` | At least one user encountered a hard failure during processing. |

These exit codes allow schedulers and pipelines to detect and alert on failures.

---

## Project Structure

```
EntraGroupManager/
├── Program.cs              # All application logic
├── appsettings.json        # Runtime config (not committed – add to .gitignore)
├── appsettings.example.json# Template – copy to appsettings.json
├── EntraGroupManager.csproj
└── README.md
```

---

## Error Handling

| Scenario | Behaviour |
|---|---|
| User not found in directory | `[SKIP]` – logged, counted as skipped |
| User already a member (add) | `[SKIP]` – idempotent, no error |
| User not a member (remove) | `[SKIP]` – idempotent, no error |
| Graph API error for a user | `[FAIL]` – logged with error code, processing continues |
| Group not found | Fatal – exits with code 1 |
| Multiple groups with same name | Fatal – exits with code 1 (use object ID to disambiguate) |
| CSV file missing | Fatal – exits with code 1 |
| Auth / config error | Fatal – exits with code 1 |

---

## Security Best Practices

- **Never commit `appsettings.json`** – add it to `.gitignore`.
- **Prefer environment variables or Azure Key Vault** for `ClientSecret` in production.
- **Use a dedicated service principal** (`EntraGroupManager`) with only the three permissions above – avoid over-permissive roles.
- **Rotate the client secret** regularly and update the stored value in your scheduler.
- **Scope the log file permissions** so only the service account can read it (it will contain UPNs).

---

## Troubleshooting

| Problem | Solution |
|---|---|
| `Authorization_RequestDenied` | Admin consent has not been granted for the required permissions. |
| `Request_ResourceNotFound` on user lookup | The UPN does not exist in the tenant; check the CSV values. |
| `No security group named '...' was found` | Verify the group name (case-sensitive) or use the object ID instead. |
| `Multiple groups named '...' exist` | Two groups share the same display name – pass the object ID via `--group`. |
| Empty summary (0 processed) | The CSV has no valid UPN column; check the header row. |
| Exit code 2 from scheduler | At least one user failed; inspect the `--log` file for `[FAIL]` lines. |
