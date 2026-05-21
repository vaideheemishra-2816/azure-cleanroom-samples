// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Subcommand dispatcher for the Azure.CleanRoom.Analytics SDK.
// Replaces every `Invoke-Frontend` call in ../README-API.md with a typed
// SDK call. Usage:
//
//   dotnet run -- <verb> <personaTokenFile> [args...]
//
// See ../README-SDK.md (Appendix A/C) for the per-step PowerShell driver
// that calls these verbs.

using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.CleanRoom.Analytics;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.CleanRoom;
using Azure.ResourceManager.CleanRoom.Models;

if (args.Length < 1) { Usage(); return 1; }

string verb = args[0];

// Management-plane (ARM) verbs use DefaultAzureCredential, not a persona JWT.
// They take their own arg layout: `dotnet run -- <arm-verb> [verb-args...]`.
if (verb.StartsWith("arm-"))
{
    return await RunArmAsync(verb, args[1..]);
}

if (args.Length < 2) { Usage(); return 1; }

string tokenArg = args[1];
string[] rest   = args[2..];

string? envFrontend = Environment.GetEnvironmentVariable("CLEANROOM_FRONTEND");
string frontend = envFrontend
    ?? "https://prod.workload-frontendwestus.cleanroom.cloudapp.azure.net";

if (!File.Exists(tokenArg))
{
    Console.Error.WriteLine($"token file not found: {tokenArg}");
    return 2;
}

// DEV-ONLY: skip TLS certificate validation. This mirrors the PowerShell
// helper in README-API.md which uses `SkipCertificateCheck = $true` on every
// Invoke-RestMethod call against the frontend.
Console.Error.WriteLine(
    "WARNING: TLS certificate validation disabled (matches Invoke-Frontend " +
    "-SkipCertificateCheck in README-API.md). Dev/sample use only.");

var options = new CollaborationClientOptions();
options.AddPolicy(
    new FileTokenBearerPolicy(tokenArg),
    HttpPipelinePosition.PerCall);
options.Transport = new HttpClientTransport(new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
}));

var client = new CollaborationClient(new Uri(frontend), options);

try
{
    Response r = verb switch
    {
        // ---- Collaborations -------------------------------------------------
        "list-collaborations" =>
            await client.GetGetsAsync(activeOnly: true, context: null),
        "get-collaboration" =>
            await client.IdGetAsync(rest[0], activeOnly: null, context: null),

        // ---- Invitations ----------------------------------------------------
        "list-invitations" =>
            await client.InvitationsGetAsync(rest[0], pendingOnly: null, context: null),
        "accept-invitation" =>
            await client.InvitationIdAcceptPostAsync(rest[0], rest[1], context: null),

        // ---- OIDC -----------------------------------------------------------
        "get-oidc-keys" =>
            await client.OidcKeysGetAsync(rest[0], context: null),
        "set-issuer-url" =>
            await client.OidcSetIssuerUrlPostAsync(
                rest[0],
                RequestContent.Create(JsonSerializer.Serialize(new { url = rest[1] })),
                context: null),

        // ---- Datasets -------------------------------------------------------
        "list-datasets" =>
            await client.AnalyticsDatasetsListGetAsync(rest[0], context: null),
        "get-dataset" =>
            await client.AnalyticsDatasetsDocumentIdGetAsync(rest[0], rest[1], context: null),
        "publish-dataset" =>
            await client.AnalyticsDatasetsDocumentIdPublishPostAsync(
                rest[0], rest[1],
                RequestContent.Create(File.ReadAllText(rest[2])),
                context: null),
        "set-consent" =>
            await client.ConsentDocumentIdPutAsync(
                rest[0], rest[1],
                RequestContent.Create(JsonSerializer.Serialize(new { consentAction = rest[2] })),
                context: null),

        // ---- Queries --------------------------------------------------------
        "list-queries" =>
            await client.AnalyticsQueriesListGetAsync(rest[0], context: null),
        "get-query" =>
            await client.AnalyticsQueriesDocumentIdGetAsync(rest[0], rest[1], context: null),
        "publish-query" =>
            await client.AnalyticsQueriesDocumentIdPublishPostAsync(
                rest[0], rest[1],
                RequestContent.Create(File.ReadAllText(rest[2])),
                context: null),
        "vote" =>
            await client.AnalyticsQueriesDocumentIdVotePostAsync(
                rest[0], rest[1],
                RequestContent.Create(JsonSerializer.Serialize(new
                {
                    voteAction = rest[2],
                    proposalId = rest[3],
                })),
                context: null),

        // ---- Runs -----------------------------------------------------------
        "run-query" =>
            await client.AnalyticsQueriesDocumentIdRunPostAsync(
                rest[0], rest[1],
                RequestContent.Create(JsonSerializer.Serialize(BuildRunBody(rest))),
                context: null),
        "poll-run" =>
            await client.AnalyticsRunsJobIdGetAsync(rest[0], rest[1], context: null),
        "list-runs" =>
            await client.AnalyticsQueriesDocumentIdRunsGetAsync(rest[0], rest[1], context: null),
        "wait-run" =>
            await WaitForRunAsync(client, rest[0], rest[1]),

        // ---- Audit / misc ---------------------------------------------------
        "audit" =>
            await client.AnalyticsAuditeventsGetAsync(
                rest[0], scope: null, fromSeqno: null, toSeqno: null, context: null),
        "analytics-info" =>
            await client.AnalyticsGetAsync(rest[0], context: null),
        "report" =>
            await client.ReportGetAsync(rest[0], context: null),

        _ => throw new ArgumentException($"unknown verb: {verb}"),
    };

    Console.Error.WriteLine($"HTTP {r.Status}");
    if (r.Content is not null) Console.WriteLine(r.Content.ToString());
    return r.Status >= 200 && r.Status < 300 ? 0 : 3;
}
catch (RequestFailedException ex)
{
    Console.Error.WriteLine($"HTTP {ex.Status} {ex.ErrorCode}: {ex.Message}");
    return 4;
}

static object BuildRunBody(string[] rest)
{
    // rest[0]=collabId, rest[1]=queryName, optional rest[2]=startDate, rest[3]=endDate
    string runId = Guid.NewGuid().ToString();
    return rest.Length >= 4
        ? new { runId, startDate = rest[2], endDate = rest[3] }
        : new { runId };
}

static async Task<Response> WaitForRunAsync(CollaborationClient client, string collabId, string jobId)
{
    Response last;
    while (true)
    {
        last = await client.AnalyticsRunsJobIdGetAsync(collabId, jobId, context: null);
        using var doc = JsonDocument.Parse(last.Content.ToString());
        string? state = doc.RootElement
            .GetProperty("status").GetProperty("applicationState").GetProperty("state").GetString();
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] State: {state}");
        if (state is "COMPLETED" or "FAILED" or "SUBMISSION_FAILED") return last;
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}

static void Usage()
{
    Console.Error.WriteLine(@"usage: dotnet run -- <verb> <personaTokenFile> [args...]
       dotnet run -- <arm-verb> [verb-args...]

Frontend (dataplane) verbs — require a persona JWT in <personaTokenFile>:
  list-collaborations
  get-collaboration       <collabId>
  list-invitations        <collabId>
  accept-invitation       <collabId> <invitationId>
  get-oidc-keys           <collabId>
  set-issuer-url          <collabId> <issuerUrl>
  list-datasets           <collabId>
  get-dataset             <collabId> <datasetId>
  publish-dataset         <collabId> <datasetId> <bodyFile>
  set-consent             <collabId> <documentId> <enable|disable>
  list-queries            <collabId>
  get-query               <collabId> <queryName>
  publish-query           <collabId> <queryName> <bodyFile>
  vote                    <collabId> <queryName> <accept|reject> <proposalId>
  run-query               <collabId> <queryName> [startDate endDate]
  poll-run                <collabId> <jobId>
  list-runs               <collabId> <queryName>
  wait-run                <collabId> <jobId>
  audit                   <collabId>
  analytics-info          <collabId>
  report                  <collabId>

Management plane (ARM) verbs — use DefaultAzureCredential (e.g. az login):
  arm-whoami
  arm-list-collabs        <subscriptionId> <resourceGroup>
  arm-show-collab         <subscriptionId> <resourceGroup> <collaborationName>
  arm-create-collab       <subscriptionId> <resourceGroup> <name> <location> <resourceLocation> <ownerUserIdentifier>
  arm-delete-collab       <subscriptionId> <resourceGroup> <name>
  arm-enable-workload     <subscriptionId> <resourceGroup> <name> [workloadType=Analytics]
  arm-add-collaborator    <subscriptionId> <resourceGroup> <name> <userIdentifier>
  arm-recover-collab      <subscriptionId> <resourceGroup> <name> [forceRecover=true]
  arm-get-kubeconfig      <subscriptionId> <resourceGroup> <name> [outFile]
  arm-wait-provisioning   <subscriptionId> <resourceGroup> <name> [intervalSec=15] [timeoutSec=900]
  arm-wait-health         <subscriptionId> <resourceGroup> <name> [intervalSec=15] [timeoutSec=900]

Env:
  CLEANROOM_FRONTEND      Override frontend URL
                          (default: https://prod.workload-frontendwestus.cleanroom.cloudapp.azure.net)
");
}

// ---------- Management-plane (ARM) dispatcher ------------------------------
static async Task<int> RunArmAsync(string verb, string[] a)
{
    try
    {
        switch (verb)
        {
            case "arm-whoami":
            {
                var cred  = new DefaultAzureCredential();
                var ctx   = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
                var token = await cred.GetTokenAsync(ctx, default);
                Console.Error.WriteLine($"Token acquired; expires at {token.ExpiresOn:u}");
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    expiresOn = token.ExpiresOn.ToString("u"),
                    tokenLength = token.Token.Length,
                }));
                return 0;
            }
            case "arm-list-collabs":
            {
                if (a.Length < 2) { Console.Error.WriteLine("usage: arm-list-collabs <subId> <rg>"); return 1; }
                string subId = a[0], rg = a[1];
                var arm = new ArmClient(new DefaultAzureCredential());
                var rgRes = arm.GetResourceGroupResource(
                    new ResourceIdentifier($"/subscriptions/{subId}/resourceGroups/{rg}"));
                int n = 0;
                await foreach (var c in rgRes.GetCollaborations().GetAllAsync())
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        c.Data.Name,
                        Id = c.Data.Id.ToString(),
                        Location = c.Data.Location.ToString(),
                    }));
                    n++;
                }
                Console.Error.WriteLine($"Found {n} collaboration(s) in {rg}");
                return 0;
            }
            case "arm-show-collab":
            {
                if (a.Length < 3) { Console.Error.WriteLine("usage: arm-show-collab <subId> <rg> <name>"); return 1; }
                string subId = a[0], rg = a[1], name = a[2];
                var arm = new ArmClient(new DefaultAzureCredential());
                var rgRes = arm.GetResourceGroupResource(
                    new ResourceIdentifier($"/subscriptions/{subId}/resourceGroups/{rg}"));
                var resp = await rgRes.GetCollaborations().GetAsync(name);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    resp.Value.Data.Name,
                    Id = resp.Value.Data.Id.ToString(),
                    Location = resp.Value.Data.Location.ToString(),
                    ProvisioningState = resp.Value.Data.ProvisioningState?.ToString(),
                    HealthState = resp.Value.Data.Health?.HealthState.ToString(),
                }));
                Console.Error.WriteLine($"HTTP {resp.GetRawResponse().Status}");
                return resp.GetRawResponse().Status is >= 200 and < 300 ? 0 : 3;
            }
            case "arm-create-collab":
            {
                // Mirrors PUT collaboration in README-API.md (sec. "Create the collaboration").
                if (a.Length < 6)
                {
                    Console.Error.WriteLine("usage: arm-create-collab <subId> <rg> <name> <location> <resourceLocation> <ownerUserIdentifier>");
                    return 1;
                }
                string subId = a[0], rg = a[1], name = a[2], loc = a[3], resLoc = a[4], owner = a[5];
                var arm = new ArmClient(new DefaultAzureCredential());
                var rgRes = arm.GetResourceGroupResource(
                    new ResourceIdentifier($"/subscriptions/{subId}/resourceGroups/{rg}"));
                var data = new CollaborationData(new AzureLocation(loc))
                {
                    ResourceLocation = new AzureLocation(resLoc),
                };
                data.Collaborators.Add(new Collaborator { UserIdentifier = owner });
                Console.Error.WriteLine($"PUT collaboration {name} (location={loc}, resourceLocation={resLoc}); waiting for completion...");
                var op = await rgRes.GetCollaborations()
                    .CreateOrUpdateAsync(WaitUntil.Completed, name, data);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    op.Value.Data.Name,
                    Id = op.Value.Data.Id.ToString(),
                    Location = op.Value.Data.Location.ToString(),
                    ProvisioningState = op.Value.Data.ProvisioningState?.ToString(),
                }));
                return 0;
            }
            case "arm-delete-collab":
            {
                if (a.Length < 3) { Console.Error.WriteLine("usage: arm-delete-collab <subId> <rg> <name>"); return 1; }
                string subId = a[0], rg = a[1], name = a[2];
                var arm = new ArmClient(new DefaultAzureCredential());
                var collabId = CollaborationResource.CreateResourceIdentifier(subId, rg, name);
                var collab = arm.GetCollaborationResource(collabId);
                Console.Error.WriteLine($"DELETE collaboration {name}; waiting for completion...");
                var op = await collab.DeleteAsync(WaitUntil.Completed);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    deleted = name,
                    status = op.GetRawResponse().Status,
                }));
                return 0;
            }
            case "arm-enable-workload":
            {
                // POST /enableWorkload — README-API.md "Enable workload".
                if (a.Length < 3) { Console.Error.WriteLine("usage: arm-enable-workload <subId> <rg> <name> [workloadType=Analytics]"); return 1; }
                string subId = a[0], rg = a[1], name = a[2];
                string wireType = a.Length >= 4 ? a[3] : "Analytics";
                // Only "Analytics" is currently in the SDK enum (maps to AnalyticsStrict on wire "Analytics").
                if (!string.Equals(wireType, "Analytics", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"Unsupported workloadType '{wireType}'. Only 'Analytics' is supported by the SDK.");
                    return 1;
                }
                var arm = new ArmClient(new DefaultAzureCredential());
                var collabId = CollaborationResource.CreateResourceIdentifier(subId, rg, name);
                var collab = arm.GetCollaborationResource(collabId);
                var content = new EnableWorkloadContent(WorkloadType.AnalyticsStrict);
                Console.Error.WriteLine($"POST {name}/enableWorkload (type={wireType}); waiting for completion...");
                var op = await collab.EnableWorkloadAsync(WaitUntil.Completed, content);
                Console.WriteLine(JsonSerializer.Serialize(new { name, workloadType = wireType, status = op.GetRawResponse().Status }));
                return 0;
            }
            case "arm-add-collaborator":
            {
                // POST /addCollaborator — README-API.md "Add collaborator".
                if (a.Length < 4) { Console.Error.WriteLine("usage: arm-add-collaborator <subId> <rg> <name> <userIdentifier>"); return 1; }
                string subId = a[0], rg = a[1], name = a[2], userId = a[3];
                var arm = new ArmClient(new DefaultAzureCredential());
                var collabId = CollaborationResource.CreateResourceIdentifier(subId, rg, name);
                var collab = arm.GetCollaborationResource(collabId);
                var content = new AddCollaboratorContent { UserIdentifier = userId };
                Console.Error.WriteLine($"POST {name}/addCollaborator (userIdentifier={userId}); waiting for completion...");
                var op = await collab.AddCollaboratorAsync(WaitUntil.Completed, content);
                Console.WriteLine(JsonSerializer.Serialize(new { name, added = userId, status = op.GetRawResponse().Status }));
                return 0;
            }
            case "arm-recover-collab":
            {
                // POST /recover — README-API.md "Recover the collaboration".
                if (a.Length < 3) { Console.Error.WriteLine("usage: arm-recover-collab <subId> <rg> <name> [forceRecover=true]"); return 1; }
                string subId = a[0], rg = a[1], name = a[2];
                bool force = true;
                if (a.Length >= 4 && !bool.TryParse(a[3], out force)) force = true;
                var arm = new ArmClient(new DefaultAzureCredential());
                var collabId = CollaborationResource.CreateResourceIdentifier(subId, rg, name);
                var collab = arm.GetCollaborationResource(collabId);
                var content = new RecoverCollaborationContent(force);
                Console.Error.WriteLine($"POST {name}/recover (forceRecover={force}); waiting for completion...");
                var op = await collab.RecoverAsync(WaitUntil.Completed, content);
                Console.WriteLine(JsonSerializer.Serialize(new { name, recovered = true, force, status = op.GetRawResponse().Status }));
                return 0;
            }
            case "arm-get-kubeconfig":
            {
                // POST /getReadonlyKubeConfig — README-API.md "Retrieve the kubeconfig".
                if (a.Length < 3) { Console.Error.WriteLine("usage: arm-get-kubeconfig <subId> <rg> <name> [outFile]"); return 1; }
                string subId = a[0], rg = a[1], name = a[2];
                string? outFile = a.Length >= 4 ? a[3] : null;
                var arm = new ArmClient(new DefaultAzureCredential());
                var collabId = CollaborationResource.CreateResourceIdentifier(subId, rg, name);
                var collab = arm.GetCollaborationResource(collabId);
                var resp = await collab.GetReadonlyKubeConfigAsync();
                string b64 = resp.Value.Kubeconfig ?? string.Empty;
                string decoded;
                try
                {
                    decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                }
                catch (FormatException)
                {
                    // Service occasionally returns the kubeconfig already decoded.
                    decoded = b64;
                }
                if (outFile is not null)
                {
                    File.WriteAllText(outFile, decoded);
                    Console.Error.WriteLine($"Wrote kubeconfig to {outFile} ({decoded.Length} chars)");
                    Console.WriteLine(JsonSerializer.Serialize(new { name, path = outFile, length = decoded.Length }));
                }
                else
                {
                    Console.Out.Write(decoded);
                }
                return 0;
            }
            case "arm-wait-provisioning":
            {
                if (a.Length < 3) { Console.Error.WriteLine("usage: arm-wait-provisioning <subId> <rg> <name> [intervalSec=15] [timeoutSec=900]"); return 1; }
                string subId = a[0], rg = a[1], name = a[2];
                int interval = a.Length >= 4 && int.TryParse(a[3], out var i) ? i : 15;
                int timeout  = a.Length >= 5 && int.TryParse(a[4], out var t) ? t : 900;
                var arm = new ArmClient(new DefaultAzureCredential());
                var collabId = CollaborationResource.CreateResourceIdentifier(subId, rg, name);
                var collab = arm.GetCollaborationResource(collabId);
                var deadline = DateTime.UtcNow.AddSeconds(timeout);
                while (true)
                {
                    var r = await collab.GetAsync();
                    var state = r.Value.Data.ProvisioningState?.ToString() ?? "<null>";
                    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] ProvisioningState={state}");
                    if (state == "Succeeded") { Console.WriteLine(JsonSerializer.Serialize(new { name, provisioningState = state })); return 0; }
                    if (state is "Failed" or "Canceled") { Console.Error.WriteLine($"terminal state: {state}"); return 6; }
                    if (DateTime.UtcNow >= deadline) { Console.Error.WriteLine($"timeout after {timeout}s"); return 7; }
                    await Task.Delay(TimeSpan.FromSeconds(interval));
                }
            }
            case "arm-wait-health":
            {
                if (a.Length < 3) { Console.Error.WriteLine("usage: arm-wait-health <subId> <rg> <name> [intervalSec=15] [timeoutSec=900]"); return 1; }
                string subId = a[0], rg = a[1], name = a[2];
                int interval = a.Length >= 4 && int.TryParse(a[3], out var i) ? i : 15;
                int timeout  = a.Length >= 5 && int.TryParse(a[4], out var t) ? t : 900;
                var arm = new ArmClient(new DefaultAzureCredential());
                var collabId = CollaborationResource.CreateResourceIdentifier(subId, rg, name);
                var collab = arm.GetCollaborationResource(collabId);
                var deadline = DateTime.UtcNow.AddSeconds(timeout);
                while (true)
                {
                    var r = await collab.GetAsync();
                    var hs = r.Value.Data.Health?.HealthState.ToString() ?? "<null>";
                    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] HealthState={hs}");
                    if (string.Equals(hs, "Ok", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine(JsonSerializer.Serialize(new { name, healthState = hs })); return 0; }
                    if (string.Equals(hs, "Error", StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("terminal state: Error"); return 6; }
                    if (DateTime.UtcNow >= deadline) { Console.Error.WriteLine($"timeout after {timeout}s"); return 7; }
                    await Task.Delay(TimeSpan.FromSeconds(interval));
                }
            }
            default:
                Console.Error.WriteLine($"unknown arm verb: {verb}");
                return 1;
        }
    }
    catch (RequestFailedException ex)
    {
        Console.Error.WriteLine($"HTTP {ex.Status} {ex.ErrorCode}: {ex.Message}");
        return 4;
    }
    catch (AuthenticationFailedException ex)
    {
        Console.Error.WriteLine($"AuthenticationFailed: {ex.Message}");
        return 5;
    }
}
