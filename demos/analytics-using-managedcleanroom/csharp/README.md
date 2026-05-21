# Analytics Frontend — C# SDK Sample

Minimal C# consumer of the **`Azure.CleanRoom.Analytics`** SDK that mirrors the
PowerShell flow in [`../README-API.md`](../README-API.md).

## What's in this folder

| File | Purpose |
|---|---|
| `AnalyticsDemo.csproj` | Console app csproj. Targets `net10.0`. |
| `Program.cs` | Translation of "List all collaborations" from `README-API.md` §3, with the same TLS cert-skip the PowerShell `Invoke-Frontend` helper uses. |
| `nuget.config` | Points NuGet at `./nupkgs` so the package resolves offline. |
| `nupkgs/Azure.CleanRoom.Analytics.1.0.0-beta.1.nupkg` | Pre-built SDK package — no external feed required. |

No checkout of the `azure-cleanroom` repo is needed. Everything to build and
run is in this folder.

## Prerequisites

- .NET SDK **10.0** or later (`dotnet --version` → `10.0.x`).
- PowerShell 7+ (only for the token-acquisition step below; the sample itself
  runs from any shell once the token file exists).
- A JWT for the frontend service, written to a file. The exact PowerShell
  snippet from `../README-API.md` §1.5 — verified working against the prod
  frontend on 2026-05-21:
  ```powershell
  $persona = "woodgrove"
  $personaTokenFile = Join-Path ([IO.Path]::GetTempPath()) "msal-idtoken-$persona.txt"

  az login
  az account get-access-token --resource "https://management.azure.com/" `
      --query accessToken -o tsv | Out-File -FilePath $personaTokenFile -NoNewline
  ```
  (MSAL device-code flow from §1.5.1 of `README-API.md` also works for MSA /
  external accounts.)

## Build & run — verified commands

These are the exact commands that produced `HTTP 200` end-to-end against
`https://prod.workload-frontendwestus.cleanroom.cloudapp.azure.net` on
2026-05-21:

```powershell
# 1. Acquire token (see Prerequisites above), then:
cd demos/analytics-using-managedcleanroom/csharp
dotnet build -c Release
dotnet run -- $personaTokenFile
```

Observed output (Woodgrove account with no collaborations yet):

```text
WARNING: TLS certificate validation disabled (matches Invoke-Frontend -SkipCertificateCheck in README-API.md). Dev/sample use only.
HTTP 200
{"collaborations":[]}
```

Once a collaboration has been created (PowerShell `../README-API.md` §2),
the `collaborations` array will be populated; re-running the same `dotnet
run` command surfaces it through the SDK with no code changes.

### Why the WARNING line appears

The prod frontend currently presents a TLS certificate that doesn't validate
against its public hostname. The PowerShell helper in `../README-API.md`
works around this with `SkipCertificateCheck = $true` on every
`Invoke-RestMethod`. `Program.cs` does the equivalent by attaching an
`HttpClientTransport` whose handler accepts any server cert, and prints the
warning line above so it's never mistaken for safe-by-default behavior.
Delete the `// DEV-ONLY` block from `Program.cs` once the service cert is
rotated to one that chains correctly.

## How auth is wired

The SDK ships a `FileTokenBearerPolicy` that stamps
`Authorization: Bearer <jwt>` onto every outgoing request by re-reading the
token file on each call. This mirrors the `Invoke-Frontend` PowerShell helper
in `../README-API.md` (which calls `Get-Content $personaTokenFile -Raw` per
invocation) and the `LocalBearerTokenPolicy` in the Python
`managedcleanroom` CLI extension.

```csharp
var options = new CollaborationClientOptions();
options.AddPolicy(
    new FileTokenBearerPolicy(personaTokenFile),
    HttpPipelinePosition.PerCall);
var client = new CollaborationClient(new Uri(frontend), options);
```

After that, every method on `client` automatically carries the header — no
manual `Authorization: Bearer ...` plumbing per call.

## Mapping the rest of `README-API.md` to SDK calls

| `README-API.md` step | PowerShell call | C# SDK call |
|---|---|---|
| §3 List active collaborations | `Invoke-Frontend -Path ""` | `client.GetGetsAsync(activeOnly: true, ctx)` |
| §3 Get one collaboration | `Invoke-Frontend -Path "$cid"` | `client.IdGetAsync(cid, activeOnly: null, ctx)` |
| §3 Accept invitation | `Invoke-Frontend -Method POST -Path "$cid/invitations/$iid/accept"` | `client.InvitationIdAcceptPostAsync(cid, iid, ctx)` |
| §6 List datasets | `Invoke-Frontend -Path "$cid/analytics/datasets"` | `client.AnalyticsDatasetsListGetAsync(cid, ctx)` |
| §6 Publish dataset | `Invoke-Frontend -Method POST -Path "$cid/analytics/datasets/$did/publish" -Body $body` | `client.AnalyticsDatasetsDocumentIdPublishPostAsync(cid, did, RequestContent.Create($body), ctx)` |
| §7 Publish query | `Invoke-Frontend -Method POST -Path "$cid/analytics/queries/$qid/publish" -Body $body` | `client.AnalyticsQueriesDocumentIdPublishPostAsync(cid, qid, RequestContent.Create($body), ctx)` |
| §8 Vote on query | `Invoke-Frontend -Method POST -Path "$cid/analytics/queries/$qid/vote" -Body $body` | `client.AnalyticsQueriesDocumentIdVotePostAsync(cid, qid, RequestContent.Create($body), ctx)` |
| §9 Run query | `Invoke-Frontend -Method POST -Path "$cid/analytics/queries/$qid/run"` | `client.AnalyticsQueriesDocumentIdRunPostAsync(cid, qid, ctx)` |
| §10 Poll runs | `Invoke-Frontend -Path "$cid/analytics/runs/$jobId"` | `client.AnalyticsRunsJobIdGetAsync(cid, jobId, ctx)` |
| §11 Get audit events | `Invoke-Frontend -Path "$cid/analytics/auditevents"` | `client.AnalyticsAuditeventsGetAsync(cid, ctx)` |

> **Note on `GetGetsAsync`** — this is the SDK method name for the `/collaborations` list
> endpoint. It's an `@autorest/csharp` emitter quirk: the operationId
> `collaboration_list_get` triggers a "list-operation" rename that pluralizes
> the verb. The behavior is identical to the Python `list_get` and the
> PowerShell call against the same route.

## Updating the SDK

When a newer build of the SDK is available, replace the file under `nupkgs/`
and bump the `Version=` in `AnalyticsDemo.csproj` to match. Then:

```bash
rm -rf bin obj
dotnet restore
dotnet build
```
