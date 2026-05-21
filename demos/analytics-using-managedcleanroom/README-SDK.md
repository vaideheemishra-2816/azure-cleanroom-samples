# Big Data Analytics — C# SDK (`Azure.ResourceManager.CleanRoom` + `Azure.CleanRoom.Analytics`)

This guide is the **full-SDK** companion to [README-API.md](README-API.md).
Every REST/CLI call in README-API.md is replaced with a typed SDK call:

| Plane | NuGet package | Replaces |
|---|---|---|
| **Management (ARM)** | `Azure.ResourceManager.CleanRoom` | every `az rest --url $collabArmUrl...` call |
| **Data (frontend)**  | `Azure.CleanRoom.Analytics`       | every `Invoke-Frontend ...` call |

Both SDKs ship as NuGet packages and are vendored under [csharp/nupkgs/](csharp/nupkgs/).
The sample driver at [csharp/Program.cs](csharp/Program.cs) is a single subcommand
dispatcher: frontend verbs use a persona JWT, ARM verbs use `DefaultAzureCredential`
(`az login`).

For the underlying REST flow, see [README-API.md](README-API.md). For the CLI
variant using `az managedcleanroom`, see [README-CLI.md](README-CLI.md).

---

## Scenario

Identical to [README-API.md §Scenario](README-API.md#scenario). Woodgrove
(advertiser) and Northwind (publisher) collaborate in a confidential clean room
to compute an overlap analysis over their sensitive datasets.

## Overview

| Aspect | Details |
|---|---|
| **API mode** | `Azure.ResourceManager.CleanRoom` SDK (ARM) + `Azure.CleanRoom.Analytics` SDK (frontend) |
| **Language** | C# / .NET 10 for all API calls, PowerShell 7 for orchestration & resource scripts |
| **Auth** | `DefaultAzureCredential` for ARM, persona JWT file for frontend |
| **Data Encryption** | SSE (Microsoft Managed Keys) or CPK (Customer Provided Keys) |
| **Parties** | Woodgrove (owner / advertiser), Northwind (publisher) |
| **Data format** | CSV (Parquet and JSON also supported) |
| **Query engine** | Confidential Spark SQL |

### What changes vs. README-API.md

| Step | What it touches | This guide uses |
|---|---|---|
| 01.5 — Acquire token | n/a (writes a JWT file) | **PowerShell** — unchanged |
| 02 — Create collaboration | ARM | **ARM SDK** (`arm-create-collab`, `arm-wait-provisioning`, `arm-enable-workload`, `arm-wait-health`, `arm-add-collaborator`) |
| 03 — Accept invitation | Frontend | **Frontend SDK** |
| 04 — Provision resources | Storage / KV / MI | **PowerShell scripts** — unchanged |
| 05.1 — Fetch JWKS | Frontend | **Frontend SDK** |
| 05.2 — Setup OIDC storage | Storage | **PowerShell script** — unchanged |
| 05.3 — Set issuer URL | Frontend | **Frontend SDK** |
| 05.4 — Grant access | RBAC / FIC | **PowerShell script** — unchanged |
| 06 — Publish datasets | Frontend | **Frontend SDK** |
| 07 — Publish query | Frontend | **Frontend SDK** |
| 08 — Approve query | Frontend | **Frontend SDK** |
| 09 — Execute query | Frontend | **Frontend SDK** |
| 10 — Monitor query | Frontend | **Frontend SDK** |
| 11 — Results & audit | Frontend + Storage | **Frontend SDK** + PowerShell script |
| 12 — Grafana dashboards | ARM | **ARM SDK** (`arm-get-kubeconfig`) |
| App. G — Recover / Delete | ARM | **ARM SDK** (`arm-recover-collab`, `arm-delete-collab`) |

> **Mental model**: anywhere README-API.md says `Invoke-Frontend`, this guide
> calls a `CollaborationClient` method on `Azure.CleanRoom.Analytics`.
> Anywhere README-API.md says `az rest --url $collabArmUrl...`, this guide
> calls a `CollaborationResource` (or `CollaborationCollection`) method on
> `Azure.ResourceManager.CleanRoom`. Everything else is identical.

---

## Table of Contents

- [Step 00: SDK Setup](#step-00-sdk-setup) `[ONCE]`
- [Step 01: Prerequisites](#step-01-prerequisites) `[ALL]`
- [Step 02: Create Collaboration](#step-02-create-collaboration) `[OWNER]`
- [Step 03: Accept Invitations](#step-03-accept-invitations) `[EACH COLLABORATOR]`
- [Step 04: Provision Resources & Upload Data](#step-04-provision-resources--upload-data) `[EACH COLLABORATOR]`
- [Step 05: OIDC Identity & Access](#step-05-oidc-identity--access) `[EACH COLLABORATOR]`
- [Step 06: Publish Datasets](#step-06-publish-datasets) `[EACH COLLABORATOR]`
- [Step 07: Publish Query](#step-07-publish-query) `[WOODGROVE]`
- [Step 08: Approve Query](#step-08-approve-query) `[EACH COLLABORATOR]`
- [Step 09: Execute Query](#step-09-execute-query) `[WOODGROVE]`
- [Step 10: Monitor Query](#step-10-monitor-query) `[ANY]`
- [Step 11: Results & Audit](#step-11-results--audit) `[WOODGROVE]`
- [Step 12: Grafana Dashboards](#step-12-grafana-dashboards) `[OWNER]`
- [Appendix A: `Program.cs` Subcommand Dispatcher](#appendix-a-programcs-subcommand-dispatcher)
- [Appendix B: SDK Method Reference](#appendix-b-sdk-method-reference)
- [Appendix C: Mapping to README-API.md Lines](#appendix-c-mapping-to-readme-apimd-lines)
- [Appendix D: Troubleshooting](#appendix-d-troubleshooting)
- [Appendix E: Collaboration Management](#appendix-e-collaboration-management)

---

## Step 00: SDK Setup `[ONCE]`

### 0.1 Build the sample app

The SDK and a runnable sample live under `csharp/` next to this guide:

```bash
cd demos/analytics-using-managedcleanroom/csharp
dotnet restore
dotnet build -c Release
```

The csproj already references the SDK from `./nupkgs/` — no external feed needed.
See [`csharp/README.md`](csharp/README.md) for details.

### 0.2 Invoke the dispatcher

[csharp/Program.cs](csharp/Program.cs) is already a complete subcommand
dispatcher across both planes. Every step below invokes it in one of two
shapes:

```bash
# Frontend (dataplane) verb — first arg is the persona JWT file
dotnet run -- <verb> $personaTokenFile <args...>

# ARM (management plane) verb — uses DefaultAzureCredential (no token file)
dotnet run -- <arm-verb> <args...>
```

Routing is by prefix: any verb starting with `arm-` is handled by the ARM
dispatcher ([Appendix A.4](#a4-arm-dispatcher-core)); everything else goes
through the frontend dispatcher.

It's a drop-in replacement for the helpers in README-API.md:

```powershell
# Replaces:  Invoke-Frontend -Path "" -Method GET
$collabs = dotnet run -- list-collaborations $personaTokenFile | ConvertFrom-Json

# Replaces:  az rest --method GET --url $collabArmUrl... -o json
$state = (dotnet run -- arm-show-collab $subId $collabRg $collabName | ConvertFrom-Json).ProvisioningState
```

Run `dotnet run --` with no args for the live verb list, or see
[Appendix A.2](#a2-verb-catalog).

---

## Step 01: Prerequisites `[ALL]`

Sections **1.1, 1.2, 1.3, 1.4** are identical to
[README-API.md §1](README-API.md#step-01-prerequisites-all). Variables, quota
notes, owner setup, and per-collaborator variables all carry over.

### 1.5 Acquire Token & Extract OID `[EACH COLLABORATOR]`

Token acquisition stays in PowerShell — the C# side only *reads* the token
file. Use either Option A (`az login` for corporate accounts) or Option B
(MSAL device-code for MSA/external) from
[README-API.md §1.5.1](README-API.md#151-acquire-token), then extract `oid`
per [§1.5.2](README-API.md#152-extract-oid-from-token).

End state:
- `$personaTokenFile` points at a file with a JWT.
- `$personaOid` holds the JWT's `oid` claim.

> **C# side**: no helper needed — `FileTokenBearerPolicy` re-reads
> `$personaTokenFile` on every request, so an external refresher can rotate
> the JWT without recreating the client.

### 1.6 Sanity-check the SDK can reach the frontend

```powershell
cd demos/analytics-using-managedcleanroom/csharp
dotnet run -- list-collaborations $personaTokenFile
```

Expected: `HTTP 200` and `{"collaborations":[]}` (or your active collaborations
if Step 02 already ran). This is the SDK equivalent of
[README-API.md L342](README-API.md#L342) `(Invoke-Frontend -Path "" -Method GET).collaborations`.

---

## Step 02: Create Collaboration `[OWNER]`

All ARM calls below use `Azure.ResourceManager.CleanRoom` via the `arm-*` verbs
in [Appendix A](#appendix-a-programcs-subcommand-dispatcher). They authenticate
with `DefaultAzureCredential`, so any of the following is sufficient:

```powershell
az login
az account set --subscription $subscriptionId
```

### 2.1 Create Resource Group

**Unchanged** — the resource group itself isn't a CleanRoom resource:

```powershell
az group create --name $collabRg --location $rpLocation -o none
```

### 2.2 Create Collaboration

Replaces [README-API.md L248](README-API.md#L248) (`az rest --method PUT ...`).

```powershell
$collaboratorEmail = "<woodgrove-email>"   # or objectId=<oid> / upn=<upn>

cd demos/analytics-using-managedcleanroom/csharp
dotnet run -- arm-create-collab $subscriptionId $collabRg $collabName `
    $rpLocation $resourceLocation $collaboratorEmail
```

C# behind the verb:
```csharp
var arm   = new ArmClient(new DefaultAzureCredential());
var rgRes = arm.GetResourceGroupResource(
    new ResourceIdentifier($"/subscriptions/{subId}/resourceGroups/{rg}"));

var data = new CollaborationData(new AzureLocation(rpLocation))
{
    ResourceLocation = new AzureLocation(resourceLocation),
};
data.Collaborators.Add(new Collaborator { UserIdentifier = collaboratorEmail });

await rgRes.GetCollaborations()
    .CreateOrUpdateAsync(WaitUntil.Completed, collabName, data);
```

> The `Collaborators` list adds collaborators at creation time itself.
> To add more collaborators later, see [Step 2.4](#24-add-more-collaborators-optional).

> **NOTE**: `rpLocation` is the ARM RP location. `resourceLocation` controls where
> actual resources (AKS cluster, CACI instances) are deployed.

**Runtime**: ~25 minutes. `WaitUntil.Completed` blocks until the LRO finishes,
so the verb exits with success only when ARM reports `provisioningState=Succeeded`.
If you'd rather poll manually, run the verb with `WaitUntil.Started` semantics
by invoking `arm-create-collab` and following up with:

```powershell
dotnet run -- arm-wait-provisioning $subscriptionId $collabRg $collabName
```

Replaces the poll loop in [README-API.md L265-L270](README-API.md#L265).

C# behind the verb (per-iteration):
```csharp
var collab = arm.GetCollaborationResource(
    CollaborationResource.CreateResourceIdentifier(subId, rg, name));
var r = await collab.GetAsync();
var state = r.Value.Data.ProvisioningState?.ToString();   // "Succeeded" | "Failed" | ...
```

### 2.3 Enable Analytics Workload

Replaces [README-API.md L276](README-API.md#L276) (`POST .../enableWorkload`).

```powershell
dotnet run -- arm-enable-workload $subscriptionId $collabRg $collabName Analytics
```

C# behind the verb:
```csharp
var collab = arm.GetCollaborationResource(
    CollaborationResource.CreateResourceIdentifier(subId, rg, name));
await collab.EnableWorkloadAsync(
    WaitUntil.Completed,
    new EnableWorkloadContent(WorkloadType.AnalyticsStrict));   // wire value = "Analytics"
```

**Runtime**: ~7 minutes. The verb blocks until the LRO completes; afterwards
wait for `healthState` to become `Ok`:

```powershell
dotnet run -- arm-wait-health $subscriptionId $collabRg $collabName
```

Replaces the loop in [README-API.md L287-L304](README-API.md#L287).

C# behind the verb (per-iteration):
```csharp
var r  = await collab.GetAsync();
var hs = r.Value.Data.Health?.HealthState.ToString();        // "Ok" | "Error" | ...
```

> Issues are surfaced via `r.Value.Data.Health?.HealthIssues` if `HealthState`
> is anything but `Ok`. The verb prints `HealthState=...` per poll to stderr.

### 2.4 Add More Collaborators (Optional)

> The owner was already added as a collaborator during `create` (Step 2.2).
> Use this step to invite additional collaborators (e.g. Northwind in a multi-party scenario).

Replaces [README-API.md L320](README-API.md#L320) (`POST .../addCollaborator`).

```powershell
# Add Northwind
$collaboratorEmail = "<northwind-email>"   # or objectId=<oid> / upn=<upn>
dotnet run -- arm-add-collaborator $subscriptionId $collabRg $collabName $collaboratorEmail
```

C# behind the verb:
```csharp
var collab  = arm.GetCollaborationResource(
    CollaborationResource.CreateResourceIdentifier(subId, rg, name));
var content = new AddCollaboratorContent { UserIdentifier = collaboratorEmail };
await collab.AddCollaboratorAsync(WaitUntil.Completed, content);
```

> The SDK serializes the content into the on-wire shape README-API.md uses:
> `{ "collaborator": { "userIdentifier": "..." } }`. You only set the
> top-level `UserIdentifier` property — no wrapper object needed in C#.

**Verify**:
```powershell
dotnet run -- arm-show-collab $subscriptionId $collabRg $collabName
```

C# behind the verb: `rgRes.GetCollaborations().GetAsync(name)` — prints
`Name`, `Id`, `Location`, `ProvisioningState`, `HealthState`.

---

## Step 03: Accept Invitations `[EACH COLLABORATOR]`

### 3.1 List collaborations & pick yours

Replaces [README-API.md L342](README-API.md#L342)
(`Invoke-Frontend -Path "" -Method GET`).

```powershell
$json = dotnet run -- list-collaborations $personaTokenFile | ConvertFrom-Json
$json.collaborations | Format-Table @{L='#';E={[array]::IndexOf($json.collaborations,$_)+1}}, collaborationName, collaborationId, userStatus

$choice = Read-Host "Enter the number of your collaboration"
$collabId = $json.collaborations[[int]$choice - 1].collaborationId
Write-Host "Selected: $collabId"
```

C# behind the verb:
```csharp
Response resp = await client.GetGetsAsync(activeOnly: true, context: null);
Console.WriteLine(resp.Content.ToString());
```

### 3.2 Accept Invitation

Replaces [README-API.md L353, L358](README-API.md#L353).

```powershell
$invitations = (dotnet run -- list-invitations $personaTokenFile $collabId | ConvertFrom-Json).invitations
$invitations | Format-Table invitationId, accountType, status

$invitationId = $invitations[0].invitationId
dotnet run -- accept-invitation $personaTokenFile $collabId $invitationId
```

C# behind the verbs:
```csharp
// list-invitations
Response inv = await client.InvitationsGetAsync(collabId, pendingOnly: null, context: null);

// accept-invitation
await client.InvitationIdAcceptPostAsync(collabId, invitationId, context: null);
```

---

## Step 04: Provision Resources & Upload Data `[EACH COLLABORATOR]`

**Unchanged.** None of Step 04 touches the frontend. Run the existing scripts:

```powershell
./scripts/04-prepare-resources.ps1 -resourceGroup $personaRg -persona $persona -location $location
./demos/generate-data.ps1 -persona $persona

$iteration++
$suffix = if ($EncryptionMode -eq "CPK") { "-cpk-v$iteration" } else { "-v$iteration" }
$queryName = "query1$suffix"

$variant = if ($EncryptionMode -eq "CPK") { "cpk" } else { "sse" }
./scripts/05-prepare-data.ps1 -resourceGroup $personaRg `
    -variant $variant -persona $persona `
    -dataDir "./generated/datasource/$persona/csv" `
    -datasetSuffix "$suffix"
```

See [README-API.md §4](README-API.md#step-04-provision-resources--upload-data-each-collaborator).

---

## Step 05: OIDC Identity & Access `[EACH COLLABORATOR]`

### 5.1 Fetch JWKS from Frontend

Replaces [README-API.md L419](README-API.md#L419) (`Invoke-Frontend -Path "$collabId/oidc/keys"`).

```powershell
$jwksDir = "generated/$personaRg"
New-Item -ItemType Directory -Path $jwksDir -Force | Out-Null
dotnet run -- get-oidc-keys $personaTokenFile $collabId | Out-File "$jwksDir/jwks.json" -Encoding utf8
```

C# behind the verb:
```csharp
Response jwks = await client.OidcKeysGetAsync(collabId, context: null);
Console.Write(jwks.Content.ToString());
```

### 5.2 Setup OIDC Storage & Upload Documents

**Unchanged.** PowerShell script (Storage + blob upload):

```powershell
$oidcParams = @{
    resourceGroup   = $personaRg
    persona         = $persona
    collaborationId = $collabId
    JwksFile        = "generated/$personaRg/jwks.json"
}
if ($oidcStorageUrl) { $oidcParams["OidcStorageUrl"] = $oidcStorageUrl }
./scripts/06-setup-oidc-storage.ps1 @oidcParams
```

### 5.3 Register Issuer URL with Frontend

Replaces [README-API.md L442](README-API.md#L442) (`POST .../oidc/setIssuerUrl`).

```powershell
$issuerUrl = (Get-Content "generated/$personaRg/issuer-url.txt" -Raw).Trim()
dotnet run -- set-issuer-url $personaTokenFile $collabId $issuerUrl
```

C# behind the verb:
```csharp
string body = JsonSerializer.Serialize(new { url = issuerUrl });
await client.OidcSetIssuerUrlPostAsync(collabId, RequestContent.Create(body), context: null);
```

### 5.4 Grant Access & Create Federated Credentials

**Unchanged.** RBAC + federated identity credentials are ARM/AAD ops:

```powershell
./scripts/07-grant-access.ps1 -resourceGroup $personaRg `
    -collaborationId $collabId -contractId "Analytics" `
    -userId $personaOid -EncryptionMode $EncryptionMode
```

> `contractId` must be `"Analytics"` (capital A). `-userId` must be the JWT
> `oid` from Step 01.5.2. See [README-API.md §5.4](README-API.md#54-grant-access--create-federated-credentials).

---

## Step 06: Publish Datasets `[EACH COLLABORATOR]`

### 6.1 Build Dataset Body JSON

**Unchanged** — the script writes JSON files under `generated/publish/`:

```powershell
./scripts/08-build-dataset-body.ps1 -resourceGroup $personaRg -persona $persona
```

### 6.2 Publish Input Dataset

Replaces [README-API.md L487](README-API.md#L487)
(`POST $collabId/analytics/datasets/$persona-input-csv$suffix/publish`).

```powershell
dotnet run -- publish-dataset $personaTokenFile $collabId `
    "$persona-input-csv$suffix" `
    "generated/publish/$persona-input-dataset.json"
```

### 6.3 Publish Output Dataset (Woodgrove only)

Replaces [README-API.md L497](README-API.md#L497).

```powershell
if ($persona -eq "woodgrove") {
    dotnet run -- publish-dataset $personaTokenFile $collabId `
        "woodgrove-output-csv$suffix" `
        "generated/publish/woodgrove-output-dataset.json"
}
```

C# behind the verb (same method, both 6.2 and 6.3):
```csharp
string body = File.ReadAllText(bodyFile);
await client.AnalyticsDatasetsDocumentIdPublishPostAsync(
    collabId, datasetId, RequestContent.Create(body), context: null);
```

> **Consent**: To disable/enable execution consent after publish, replaces
> [README-API.md L504](README-API.md#L504) (`PUT $collabId/consent/<docName>`):
> ```powershell
> dotnet run -- set-consent $personaTokenFile $collabId "<doc-name>" disable
> ```
> C#: `client.ConsentDocumentIdPutAsync(collabId, doc, RequestContent.Create("{\"consentAction\":\"disable\"}"), null)`

### 6.4 Prepare CPK Keys (CPK mode only)

**Unchanged.** Uses Key Vault + SKR policy from the published dataset:

```powershell
if ($EncryptionMode -eq "CPK") {
    ./scripts/08-prepare-dataset-keys.ps1 -collaborationId $collabId `
        -resourceGroup $personaRg -persona $persona `
        -frontendEndpoint $frontend -TokenFile $personaTokenFile
}
```

**Verify** (replaces [L527](README-API.md#L527)):
```powershell
dotnet run -- get-dataset $personaTokenFile $collabId "$persona-input-csv$suffix"
```

C#: `client.AnalyticsDatasetsDocumentIdGetAsync(collabId, datasetId, context: null)`

---

## Step 07: Publish Query `[WOODGROVE]`

### 7.1 Build Query Body

**Unchanged** — script writes `generated/publish/$queryName.json`:

```powershell
# Single-collaborator
./scripts/09-build-query-body.ps1 -queryName $queryName `
    -queryDir "./demos/query/woodgrove/query1" `
    -publisherInputDataset "woodgrove-input-csv$suffix" `
    -consumerInputDataset "woodgrove-input-csv$suffix" `
    -outputDataset "woodgrove-output-csv$suffix"
```

For multi-collaborator (Northwind + Woodgrove), Woodgrove first lists
datasets to discover Northwind's exact name:

```powershell
$datasets = (dotnet run -- list-datasets $personaTokenFile $collabId | ConvertFrom-Json).datasets
$datasets | Where-Object { $_.id -match "northwind" } | ForEach-Object { Write-Host $_.id }
```

C#: `client.AnalyticsDatasetsListGetAsync(collabId, context: null)` (replaces [README-API.md L552](README-API.md#L552)).

### 7.2 Publish Query

Replaces [README-API.md L573](README-API.md#L573).

```powershell
dotnet run -- publish-query $personaTokenFile $collabId $queryName `
    "generated/publish/$queryName.json"
```

C# behind the verb:
```csharp
string body = File.ReadAllText(bodyFile);
await client.AnalyticsQueriesDocumentIdPublishPostAsync(
    collabId, queryName, RequestContent.Create(body), context: null);
```

---

## Step 08: Approve Query `[EACH COLLABORATOR]`

Replaces [README-API.md L590, L596](README-API.md#L590).

```powershell
# Inspect query & extract proposalId
$queryInfo = dotnet run -- get-query $personaTokenFile $collabId $queryName | ConvertFrom-Json
$queryInfo.data.queryData | Format-Table executionSequence, preConditions, postFilters, data -Wrap
$proposalId = $queryInfo.proposalId
Write-Host "Proposal ID: $proposalId"

# Vote
dotnet run -- vote $personaTokenFile $collabId $queryName accept $proposalId
```

C# behind the verbs:
```csharp
// get-query
Response qInfo = await client.AnalyticsQueriesDocumentIdGetAsync(collabId, queryName, context: null);

// vote
var body = JsonSerializer.Serialize(new { voteAction = "accept", proposalId });
await client.AnalyticsQueriesDocumentIdVotePostAsync(
    collabId, queryName, RequestContent.Create(body), context: null);
```

**Verify state** (replaces [L611](README-API.md#L611)):
```powershell
$state = (dotnet run -- get-query $personaTokenFile $collabId $queryName | ConvertFrom-Json).state
Write-Host "Query state: $state"
```

> Northwind: if you don't have `$queryName`, list published queries first —
> ```powershell
> dotnet run -- list-queries $personaTokenFile $collabId
> ```
> C#: `client.AnalyticsQueriesListGetAsync(collabId, context: null)` (replaces [L602](README-API.md#L602)).

---

## Step 09: Execute Query `[WOODGROVE]`

Replaces [README-API.md L621](README-API.md#L621).

```powershell
$jobId = (dotnet run -- run-query $personaTokenFile $collabId $queryName | ConvertFrom-Json).id
Write-Host "Job ID: $jobId"
```

With date-range filter (replaces [L640](README-API.md#L640)):

```powershell
$jobId = (dotnet run -- run-query $personaTokenFile $collabId $queryName 2025-09-01 2025-09-02 | ConvertFrom-Json).id
```

C# behind the verb:
```csharp
object body = (startDate, endDate) is (string s, string e)
    ? new { runId = Guid.NewGuid().ToString(), startDate = s, endDate = e }
    : (object)new { runId = Guid.NewGuid().ToString() };
Response runResp = await client.AnalyticsQueriesDocumentIdRunPostAsync(
    collabId, queryName, RequestContent.Create(JsonSerializer.Serialize(body)), context: null);
```

> Same network-connectivity caveats as README-API.md §9 (NSG / AVNM rules).
> `"status": "success"` means accepted for scheduling, not completed.

---

## Step 10: Monitor Query `[ANY]`

Replaces [README-API.md L650](README-API.md#L650).

The CLI verb returns the current state in one shot; loop in PowerShell:

```powershell
do {
    $result = dotnet run -- poll-run $personaTokenFile $collabId $jobId | ConvertFrom-Json
    $state  = $result.status.applicationState.state
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] State: $state"
    Start-Sleep -Seconds 30
} while ($state -notin @("COMPLETED", "FAILED", "SUBMISSION_FAILED"))

$result | ConvertTo-Json -Depth 10
```

C# behind the verb (single poll):
```csharp
Response poll = await client.AnalyticsRunsJobIdGetAsync(collabId, jobId, context: null);
Console.Write(poll.Content.ToString());
```

If you'd rather loop in C#, the dispatcher in [Appendix A](#appendix-a-programcs-subcommand-dispatcher)
includes a `wait-run` verb that polls internally and returns once the job
reaches a terminal state.

| Time | State | Key Events |
|---|---|---|
| +0 min | `SUBMITTED` | `SparkApplicationSubmitted` |
| +5-8 min | `RUNNING` | `SparkDriverRunning` |
| +10-15 min | `RUNNING` | `QUERY_SEGMENT_EXECUTION_*` |
| +15-20 min | `COMPLETED` | `SparkDriverCompleted` |

`PENDING_RERUN` is normal — transitions to `SUBMITTED` automatically.

---

## Step 11: Results & Audit `[WOODGROVE]`

### 11.1 Run History

Replaces [README-API.md L691](README-API.md#L691).

```powershell
dotnet run -- list-runs $personaTokenFile $collabId $queryName | ConvertFrom-Json | ConvertTo-Json -Depth 10
```

C#: `client.AnalyticsQueriesDocumentIdRunsGetAsync(collabId, queryName, context: null)`.

### 11.2 Audit Events

Replaces [README-API.md L700](README-API.md#L700).

```powershell
dotnet run -- audit $personaTokenFile $collabId | ConvertFrom-Json | ConvertTo-Json -Depth 10
```

C#:
```csharp
Response audit = await client.AnalyticsAuditeventsGetAsync(
    collabId, scope: null, fromSeqno: null, toSeqno: null, context: null);
```

> The method exposes optional `scope`, `fromSeqno`, `toSeqno` filters; pass
> `null` for all unless you need them.

### 11.3 Download Output

**Unchanged.** Same azcopy-based script (auto-detects SSE/CPK from metadata):

```powershell
./scripts/11-download-output.ps1 -resourceGroup $personaRg `
    -datasetSuffix "$suffix" -JobId $jobId
```

---

## Step 12: Grafana Dashboards `[OWNER]`

Replaces [README-API.md L725](README-API.md#L725) (`POST .../getReadonlyKubeConfig`).

```powershell
dotnet run -- arm-get-kubeconfig $subscriptionId $collabRg $collabName ./readonly.kubeconfig

./scripts/12-open-grafana-dashboard.ps1 -KubeConfigPath "./readonly.kubeconfig"
```

C# behind the verb:
```csharp
var collab = arm.GetCollaborationResource(
    CollaborationResource.CreateResourceIdentifier(subId, rg, name));
var resp  = await collab.GetReadonlyKubeConfigAsync();
// service returns the kubeconfig base64-encoded; verb decodes & writes to disk
File.WriteAllText(outFile,
    System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(resp.Value.Kubeconfig)));
```

> When `outFile` is omitted, the decoded YAML is written to stdout. The verb
> falls back to printing the raw value verbatim if the service ever returns
> the kubeconfig already decoded.

---

## Appendix A: `Program.cs` Subcommand Dispatcher

Source of truth: [csharp/Program.cs](csharp/Program.cs). The dispatcher
implements every verb used in Steps 02–12, across both planes.

### A.1 Dispatch architecture

```
                 ┌───────────────────────────┐
  dotnet run -- ──▶ args[0] starts with "arm-"? ──┐
                 └───────────────────────────┘     │
                            │ yes              │ no
                            ▼                  ▼
              ┌───────────────────┐   ┌──────────────────────┐
              │ RunArmAsync(verb) │   │ Frontend dispatcher   │
              │ DefaultAzureCred. │   │ FileTokenBearerPolicy │
              │ ArmClient + Mgmt  │   │ CollaborationClient   │
              │ plane resources   │   │ (Azure.CleanRoom.      │
              │ (Azure.Resource    │   │  Analytics)           │
              │  Manager.CleanRoom)│   └──────────────────────┘
              └───────────────────┘
```

- Frontend verbs take `<personaTokenFile>` as `args[1]`.
- ARM verbs do **not** take a token file; they call `new ArmClient(new DefaultAzureCredential())` and use `az login` context.

### A.2 Verb catalog

Run `dotnet run --` with no args for the live list. As of this guide:

| Group | Verbs |
|---|---|
| Collabs | `list-collaborations`, `get-collaboration` |
| Invitations | `list-invitations`, `accept-invitation` |
| OIDC | `get-oidc-keys`, `set-issuer-url` |
| Datasets | `list-datasets`, `get-dataset`, `publish-dataset`, `set-consent` |
| Queries | `list-queries`, `get-query`, `publish-query`, `vote` |
| Runs | `run-query`, `poll-run`, `list-runs`, `wait-run` |
| Misc | `audit`, `analytics-info`, `report` |
| **ARM** | `arm-whoami`, `arm-list-collabs`, `arm-show-collab`, `arm-create-collab`, `arm-delete-collab`, `arm-enable-workload`, `arm-add-collaborator`, `arm-recover-collab`, `arm-get-kubeconfig`, `arm-wait-provisioning`, `arm-wait-health` |

### A.3 Project file

[csharp/AnalyticsDemo.csproj](csharp/AnalyticsDemo.csproj) pins both SDKs from
the vendored feed under `csharp/nupkgs/`:

```xml
<ItemGroup>
  <PackageReference Include="Azure.CleanRoom.Analytics"        Version="1.0.0-beta.1" />
  <PackageReference Include="Azure.ResourceManager.CleanRoom"  Version="1.0.0-beta.1" />
  <PackageReference Include="Azure.Identity"                   Version="1.17.1" Aliases="AzId" />
</ItemGroup>
```

> **The `Aliases="AzId"` is load-bearing.** `Azure.Core 1.56` (pulled by
> `Azure.ResourceManager.CleanRoom`) ships its own `DefaultAzureCredential`
> and `AuthenticationFailedException`, colliding with `Azure.Identity 1.17.1`
> (pulled by `Azure.CleanRoom.Analytics`). The alias resolves the CS0433 type
> clash. Do not remove it.

### A.4 ARM dispatcher core

For reference (the frontend dispatcher is documented inline at each step):

```csharp
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.CleanRoom;
using Azure.ResourceManager.CleanRoom.Models;

static async Task<int> RunArmAsync(string verb, string[] a)
{
    try
    {
        var arm = new ArmClient(new DefaultAzureCredential());
        switch (verb)
        {
            case "arm-create-collab":
            {
                var rgRes = arm.GetResourceGroupResource(
                    new ResourceIdentifier($"/subscriptions/{a[0]}/resourceGroups/{a[1]}"));
                var data = new CollaborationData(new AzureLocation(a[3]))
                {
                    ResourceLocation = new AzureLocation(a[4]),
                };
                data.Collaborators.Add(new Collaborator { UserIdentifier = a[5] });
                await rgRes.GetCollaborations()
                    .CreateOrUpdateAsync(WaitUntil.Completed, a[2], data);
                return 0;
            }
            case "arm-enable-workload":
            {
                var collab = arm.GetCollaborationResource(
                    CollaborationResource.CreateResourceIdentifier(a[0], a[1], a[2]));
                await collab.EnableWorkloadAsync(WaitUntil.Completed,
                    new EnableWorkloadContent(WorkloadType.AnalyticsStrict));
                return 0;
            }
            case "arm-add-collaborator":
            {
                var collab = arm.GetCollaborationResource(
                    CollaborationResource.CreateResourceIdentifier(a[0], a[1], a[2]));
                await collab.AddCollaboratorAsync(WaitUntil.Completed,
                    new AddCollaboratorContent { UserIdentifier = a[3] });
                return 0;
            }
            // ... arm-show-collab, arm-list-collabs, arm-delete-collab,
            // arm-recover-collab, arm-get-kubeconfig, arm-wait-provisioning,
            // arm-wait-health
        }
    }
    catch (RequestFailedException ex)        { /* HTTP {ex.Status} {ex.ErrorCode}: ... */ return 4; }
    catch (AuthenticationFailedException ex) { /* AAD failure */                            return 5; }
    return 1;
}
```

> **Build**: from `csharp/`, run `dotnet build -c Release`.
> **Run**: `dotnet run -- <verb> [args...]`.

---

## Appendix B: SDK Method Reference

### B.1 Frontend SDK (`Azure.CleanRoom.Analytics`)

All methods live on `Azure.CleanRoom.Analytics.CollaborationClient`. Each has a
sync (`Xxx(...)`) and async (`XxxAsync(...)`) variant. The trailing
`RequestContext context` is always optional — pass `null`. Bodies use
`RequestContent.Create(string|stream|BinaryData)`.

| Area | Method | Notes |
|---|---|---|
| Collabs | `GetGetsAsync(bool? activeOnly, ctx)` | List. Name is an autorest emitter quirk. |
| Collabs | `IdGetAsync(collabId, bool? activeOnly, ctx)` | Get one. |
| Invitations | `InvitationsGetAsync(collabId, bool? pendingOnly, ctx)` | List. |
| Invitations | `InvitationIdGetAsync(collabId, invitationId, ctx)` | Get one. |
| Invitations | `InvitationIdAcceptPostAsync(collabId, invitationId, ctx)` | Accept; returns 204, empty body. |
| OIDC | `OidcKeysGetAsync(collabId, ctx)` | JWKS. |
| OIDC | `OidcSetIssuerUrlPostAsync(collabId, content, ctx)` | Body `{ "url": "..." }`. |
| OIDC | `OidcIssuerInfoGetAsync(collabId, ctx)` | Current issuer info. |
| Datasets | `AnalyticsDatasetsListGetAsync(collabId, ctx)` | List. |
| Datasets | `AnalyticsDatasetsDocumentIdGetAsync(collabId, datasetId, ctx)` | Get one. |
| Datasets | `AnalyticsDatasetsDocumentIdPublishPostAsync(collabId, datasetId, content, ctx)` | Publish; body is dataset JSON. |
| Datasets | `AnalyticsDatasetsDocumentIdQueriesGetAsync(collabId, datasetId, ctx)` | Queries using this dataset. |
| Consent | `ConsentDocumentIdGetAsync(collabId, documentId, ctx)` | Get. |
| Consent | `ConsentDocumentIdPutAsync(collabId, documentId, content, ctx)` | Set; body `{ "consentAction": "enable\|disable" }`. |
| Queries | `AnalyticsQueriesListGetAsync(collabId, ctx)` | List. |
| Queries | `AnalyticsQueriesDocumentIdGetAsync(collabId, queryName, ctx)` | Get; contains `proposalId`, `state`. |
| Queries | `AnalyticsQueriesDocumentIdPublishPostAsync(collabId, queryName, content, ctx)` | Publish; body is query JSON. |
| Queries | `AnalyticsQueriesDocumentIdVotePostAsync(collabId, queryName, content, ctx)` | Body `{ "voteAction": "...", "proposalId": "..." }`. |
| Queries | `AnalyticsQueriesDocumentIdRunPostAsync(collabId, queryName, content, ctx)` | Body `{ "runId": "...", "startDate"?, "endDate"? }`. Returns `{ "id": "<jobId>", ... }`. |
| Queries | `AnalyticsQueriesDocumentIdRunsGetAsync(collabId, queryName, ctx)` | Run history. |
| Runs | `AnalyticsRunsJobIdGetAsync(collabId, jobId, ctx)` | Poll; `.status.applicationState.state` is the value to watch. |
| Audit | `AnalyticsAuditeventsGetAsync(collabId, scope, fromSeqno, toSeqno, ctx)` | All filters optional. |
| Misc | `AnalyticsGetAsync(collabId, ctx)` | Workload info. |
| Misc | `ReportGetAsync(collabId, ctx)` | Compliance report. |
| Misc | `AnalyticsSecretsSecretNamePutAsync(collabId, secretName, content, ctx)` | CPK secrets. |
| Misc | `AnalyticsSkrPolicyGetAsync(collabId, datasetId, ctx)` | CPK SKR policy. |

### B.2 ARM SDK (`Azure.ResourceManager.CleanRoom`)

All long-running operations take a `WaitUntil` (`Started` returns immediately
with an `ArmOperation` you can `WaitForCompletionAsync` on; `Completed` blocks
until the LRO finishes). Strongly-typed resource handles let you skip a GET
before an instance call.

**Entry points**

| Type | Usage |
|---|---|
| `ArmClient(new DefaultAzureCredential())` | Top-level handle. |
| `arm.GetResourceGroupResource(new ResourceIdentifier($"/subscriptions/{s}/resourceGroups/{r}"))` | Get an RG without a GET. |
| `rgRes.GetCollaborations()` | The `CollaborationCollection` under that RG. |
| `CollaborationResource.CreateResourceIdentifier(subId, rg, name)` | Build a `ResourceIdentifier` for an existing collaboration. |
| `arm.GetCollaborationResource(id)` | Get a strongly-typed handle without a GET. |

**Collection methods** — `CollaborationCollection`

| Method | Notes |
|---|---|
| `CreateOrUpdateAsync(WaitUntil, name, CollaborationData)` | PUT collaboration. Pass `Collaborators` via `data.Collaborators.Add(new Collaborator { UserIdentifier = ... })`. |
| `GetAsync(name)` | GET by name. |
| `GetAllAsync()` | List in RG. |
| `ExistsAsync(name)` | Cheap existence check. |

**Resource methods** — `CollaborationResource`

| Method | Notes |
|---|---|
| `GetAsync()` | Refresh data; inspect `Data.ProvisioningState`, `Data.Health?.HealthState`, `Data.Workloads`. |
| `UpdateAsync(WaitUntil, ResourceTags)` | Tag update only. |
| `DeleteAsync(WaitUntil)` | DELETE collaboration. |
| `EnableWorkloadAsync(WaitUntil, EnableWorkloadContent)` | `new EnableWorkloadContent(WorkloadType.AnalyticsStrict)` (wire value: `"Analytics"`). |
| `AddCollaboratorAsync(WaitUntil, AddCollaboratorContent)` | `new AddCollaboratorContent { UserIdentifier = ... }`. SDK wraps it in `{ collaborator: {...} }` on the wire. |
| `RecoverAsync(WaitUntil, RecoverCollaborationContent)` | `new RecoverCollaborationContent(forceRecover: true)`. |
| `GetReadonlyKubeConfigAsync()` | Returns `Response<ReadonlyKubeConfigResult>`; `result.Value.Kubeconfig` is base64 — decode with `Convert.FromBase64String`. |
| `AddTagAsync(string key, string value)` | Single-tag helper. |

**Model types** — `Azure.ResourceManager.CleanRoom.Models`

| Type | Notes |
|---|---|
| `CollaborationData` (`new(AzureLocation)`) | Resource data. `Collaborators` (`IList<Collaborator>`, get-only — use `.Add`), `ResourceLocation`, `Tags`. Read-only on the resulting resource: `ProvisioningState`, `Health`, `Workloads`. |
| `Collaborator` | `UserIdentifier`, `TenantId`, `ObjectId` (settable). `IsCollaborationOwner` is read-only (server-set). |
| `EnableWorkloadContent(WorkloadType)` | Only `WorkloadType.AnalyticsStrict` is in the beta SDK (wire value `"Analytics"`). |
| `AddCollaboratorContent` | `UserIdentifier`, `TenantId`, `ObjectId` (settable). Serializes as `{ "collaborator": {...} }`. |
| `RecoverCollaborationContent(bool forceRecover)` | Set `forceRecover: true` for the README-API.md scenario. |
| `ReadonlyKubeConfigResult.Kubeconfig` | Base64 string. |
| `ProvisioningState` | `Succeeded`, `Failed`, `Canceled`, `Creating`, `Updating`, `Deleting`, `Accepted`. |
| `HealthState` | `Ok`, `Error`. |
| `Health` | Contains `HealthState` and `HealthIssues`. |

---

## Appendix C: Mapping to README-API.md Lines

### C.1 ARM (`az rest`) → ARM SDK

| README-API.md line | `az rest` call | SDK call (via `arm-*` verb → method) |
|---|---|---|
| [L248](README-API.md#L248) | `PUT $collabArmUrl` | `arm-create-collab` → `CollaborationCollection.CreateOrUpdateAsync` |
| [L265](README-API.md#L265) | `GET $collabArmUrl` (poll provisioningState) | `arm-wait-provisioning` → `CollaborationResource.GetAsync` (loop) |
| [L276](README-API.md#L276) | `POST .../enableWorkload` | `arm-enable-workload` → `CollaborationResource.EnableWorkloadAsync` |
| [L287](README-API.md#L287) | `GET $collabArmUrl` (poll workload endpoint) | `arm-wait-provisioning` (state) + `arm-show-collab` (workloads) |
| [L298](README-API.md#L298) | `GET $collabArmUrl` (poll healthState) | `arm-wait-health` → `CollaborationResource.GetAsync` (loop on `Data.Health.HealthState`) |
| [L320](README-API.md#L320) | `POST .../addCollaborator` | `arm-add-collaborator` → `CollaborationResource.AddCollaboratorAsync` |
| [L332](README-API.md#L332) | `GET $collabArmUrl` (verify) | `arm-show-collab` → `CollaborationCollection.GetAsync` |
| [L725](README-API.md#L725) | `POST .../getReadonlyKubeConfig` | `arm-get-kubeconfig` → `CollaborationResource.GetReadonlyKubeConfigAsync` |
| [L880](README-API.md#L880) | `POST .../recover` | `arm-recover-collab` → `CollaborationResource.RecoverAsync` |
| [L893](README-API.md#L893) | `DELETE $collabArmUrl` | `arm-delete-collab` → `CollaborationResource.DeleteAsync` |

### C.2 Frontend (`Invoke-Frontend`) → Frontend SDK

Every `Invoke-Frontend` line in [README-API.md](README-API.md) and its SDK
replacement, in source order:

| README-API.md line | PowerShell call | SDK method |
|---|---|---|
| [L342](README-API.md#L342) | `Invoke-Frontend -Path ""` | `GetGetsAsync` |
| [L353](README-API.md#L353) | `Invoke-Frontend -Path "$cid/invitations"` | `InvitationsGetAsync` |
| [L358](README-API.md#L358) | `Invoke-Frontend -Path "$cid/invitations/$iid/accept" -Method POST` | `InvitationIdAcceptPostAsync` |
| [L419](README-API.md#L419) | `Invoke-Frontend -Path "$cid/oidc/keys"` | `OidcKeysGetAsync` |
| [L442](README-API.md#L442) | `Invoke-Frontend -Path "$cid/oidc/setIssuerUrl" -Method POST -Body @{url=...}` | `OidcSetIssuerUrlPostAsync` |
| [L487](README-API.md#L487) | `Invoke-Frontend -Path ".../analytics/datasets/$did/publish" -Method POST -Body $body` | `AnalyticsDatasetsDocumentIdPublishPostAsync` |
| [L497](README-API.md#L497) | same, output dataset | same method, different `datasetId` |
| [L504](README-API.md#L504) | `Invoke-Frontend -Path "$cid/consent/<doc>" -Method PUT -Body @{consentAction=...}` | `ConsentDocumentIdPutAsync` |
| [L527](README-API.md#L527) | `Invoke-Frontend -Path "$cid/analytics/datasets/$did"` | `AnalyticsDatasetsDocumentIdGetAsync` |
| [L552](README-API.md#L552) | `Invoke-Frontend -Path "$cid/analytics/datasets"` | `AnalyticsDatasetsListGetAsync` |
| [L573](README-API.md#L573) | `Invoke-Frontend -Path ".../queries/$qid/publish" -Method POST -Body $body` | `AnalyticsQueriesDocumentIdPublishPostAsync` |
| [L590](README-API.md#L590) | `Invoke-Frontend -Path ".../queries/$qid"` | `AnalyticsQueriesDocumentIdGetAsync` |
| [L596](README-API.md#L596) | `Invoke-Frontend -Path ".../queries/$qid/vote" -Method POST -Body $body` | `AnalyticsQueriesDocumentIdVotePostAsync` |
| [L602](README-API.md#L602) | `Invoke-Frontend -Path ".../queries"` | `AnalyticsQueriesListGetAsync` |
| [L611](README-API.md#L611) | get query state (same path as L590) | `AnalyticsQueriesDocumentIdGetAsync` |
| [L621](README-API.md#L621) | `Invoke-Frontend -Path ".../queries/$qid/run" -Method POST -Body @{runId=...}` | `AnalyticsQueriesDocumentIdRunPostAsync` |
| [L640](README-API.md#L640) | same with `startDate`/`endDate` | same method, augmented body |
| [L650](README-API.md#L650) | `Invoke-Frontend -Path ".../runs/$jobId"` | `AnalyticsRunsJobIdGetAsync` |
| [L691](README-API.md#L691) | `Invoke-Frontend -Path ".../queries/$qid/runs"` | `AnalyticsQueriesDocumentIdRunsGetAsync` |
| [L700](README-API.md#L700) | `Invoke-Frontend -Path "$cid/analytics/auditevents"` | `AnalyticsAuditeventsGetAsync` |

The `Invoke-Frontend` **helper definition** at
[README-API.md L175-L189](README-API.md#L175-L189) is no longer needed if you
go full SDK — its `Authorization`, base URL, `api-version`, and TLS-skip
behaviors are all baked into the dispatcher in
[Appendix A](#appendix-a-programcs-subcommand-dispatcher).

---

## Appendix D: Troubleshooting

| Error | Cause | Fix |
|---|---|---|
| `The SSL connection could not be established ... RemoteCertificateNameMismatch` | Frontend cert does not validate against hostname | Keep the `ServerCertificateCustomValidationCallback` block; matches `-SkipCertificateCheck` in README-API.md. |
| `Authentication failed ... AADSTS50076: must use multi-factor authentication` | Wrong tenant during `az login` | `az login --tenant <correct-tenant-id>` then re-acquire token. |
| `AuthenticationFailedException` on an `arm-*` verb | No `az login`, expired session, or wrong subscription | `az login && az account set --subscription <id>`. The verb exits with code `5`. |
| `401 Unauthorized` from frontend SDK | Stale/empty token file | Re-run `az account get-access-token ... \| Out-File $personaTokenFile -NoNewline`. The SDK re-reads on each call. |
| `404 Not Found` on a frontend verb | Using ARM resource ID instead of frontend UUID | Use `collaborationId` from `list-collaborations`. |
| `CS0433: type DefaultAzureCredential exists in both Azure.Core and Azure.Identity` | `Azure.Core 1.56` (transitively pulled by `Azure.ResourceManager.CleanRoom`) now defines identity primitives | Keep `Aliases="AzId"` on the `Azure.Identity` `PackageReference`. See [Appendix A.3](#a3-project-file). |
| `RequestFailedException` from an `arm-*` verb (exit 4) | Service-side ARM rejection | Read the `HTTP {status} {errorCode}` line on stderr; check the operation's preconditions (e.g. resource group exists, collaboration exists, workload not already enabled). |
| `CS0103: HttpPipelinePosition does not exist` | Missing `using Azure.Core;` | Add it to `Program.cs`. |
| `Could not load file or assembly Azure.Core, Version=1.50.0.0` | Older Azure.Core in transitive deps | Pin `<PackageReference Include="Azure.Core" Version="1.50.0" />` (only matters for tooling that loads the DLL outside the project). |
| `Already voted / Conflict` | Idempotent vote replay | Safe to ignore. |
| `PENDING_RERUN` while polling | Normal scheduling | Keep polling; transitions to `SUBMITTED`. |

For all other errors, see [README-API.md Appendix B](README-API.md#appendix-b-troubleshooting) — the underlying service behavior is identical.

---

## Appendix E: Collaboration Management

Mirrors [README-API.md Appendix G](README-API.md#appendix-g-collaboration-management).

### E.1 Force Recover

If the collaboration becomes unresponsive (e.g., `ContractNotFound`, frontend
errors on all operations):

```powershell
dotnet run -- arm-recover-collab $subscriptionId $collabRg $collabName
```

C# behind the verb:
```csharp
var collab = arm.GetCollaborationResource(
    CollaborationResource.CreateResourceIdentifier(subId, rg, name));
await collab.RecoverAsync(WaitUntil.Completed,
    new RecoverCollaborationContent(forceRecover: true));
```

> Last-resort operation. Resets internal state. Existing datasets and queries
> need not be republished after recovery. Pass `false` as the optional fourth
> argument to attempt a non-forced recovery.

### E.2 Delete Collaboration

```powershell
dotnet run -- arm-delete-collab $subscriptionId $collabRg $collabName
```

C# behind the verb:
```csharp
var collab = arm.GetCollaborationResource(
    CollaborationResource.CreateResourceIdentifier(subId, rg, name));
await collab.DeleteAsync(WaitUntil.Completed);
```

> Permanently deletes the collaboration and all associated resources.

---

## See Also

- [README-API.md](README-API.md) — raw REST flow with `az rest` + `Invoke-Frontend`.
- [README-CLI.md](README-CLI.md) — `az managedcleanroom` CLI variant.
- [csharp/README.md](csharp/README.md) — SDK package details and offline-build setup.
- [csharp/Program.cs](csharp/Program.cs) — dispatcher source of truth.
