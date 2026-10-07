# 회사 배포 안내

회사 PC에는 Worker와 Watchdog만 설치합니다. **API는 서버에서 호스팅하는 웹 앱이며, 클라이언트에 복사하지 않습니다.** 개발용 `dotnet run`은 [README.md](../README.md)를 참고하세요.

에이전트는 PC의 `appsettings.json`에 적은 서버 URL과 에이전트 토큰으로 API에 접속합니다.

```
회사 PC (클라이언트)                       회사 서버 (여기만 API)
┌──────────────────────────┐              ┌─────────────────────────┐
│ Worker   수집·전송        │  HTTPS      │ ASP.NET Core API        │
│ Watchdog 자체 패치        │  Bearer ──► │ SQL Server              │
│ appsettings.json         │              │ appsettings.json        │
└──────────────────────────┘              └─────────────────────────┘
```

```
deploy/
  examples/
    appsettings.api.company.json       서버 API Kestrel (PC에 복사하지 않음)
    appsettings.api.iis.company.json   서버 API IIS (Kestrel 바인딩 없음)
    appsettings.worker.company.json    PC Worker
    appsettings.watchdog.company.json  PC Watchdog
  scripts/
    New-ApiToken.ps1        토큰 생성 (에이전트용·관리자용을 따로 두 번 실행)
    Apply-DbSchema.ps1      SQL Server 스키마 적용 (API 또는 .sql 파일)
    Install-ApiServer.ps1   서버에 Kestrel Windows Service 설치
    Uninstall-ApiServer.ps1
    Install-Agent.ps1       PC에 Worker + Watchdog만 설치
    Uninstall-Agent.ps1
```

직원 PC에는 PowerShell 대신 전체 Release ZIP(`SwLicenseWatcher-{version}.zip`) 안의 `Packager.exe`로 만든 **회사 Setup.exe 하나**를 배포할 수 있습니다. API가 이미 있으면 더 작은 `SwLicenseWatcher.Packager-{version}.zip`만 받아도 됩니다. 패키저·직원 설치·제거 승인 순서는 [packager-setup.ko.md](packager-setup.ko.md)입니다. Intune/자동화가 있으면 아래 스크립트 경로를 그대로 씁니다.

전체 ZIP의 `api/win-x64`(AOT)와 `api/iis/win-x64`(IIS)는 서버용입니다. `Install-ApiServer.ps1`은 `api/win-x64`만 복사하고, `Install-Agent.ps1`은 `agent-worker`와 `agent-watchdog`만 복사합니다. 루트의 `Packager.exe`와 Setup 스텁은 스크립트가 건드리지 않습니다.

## 1. 구성 요소

| 구성 요소 | 어디에 | 역할 |
| --- | --- | --- |
| API | 서버만 | 스냅샷·하트비트 수신, SQL 저장, 업데이트 manifest |
| Worker | PC | Uninstall 레지스트리로 설치 SW 수집, 서버로 전송 |
| Watchdog | PC | 서버 manifest로 Worker 패키지를 받아 교체·롤백 |
| Packager | IT PC | 회사 Setup.exe 하나를 만듦 (주소·에이전트 키 주입) |
| Setup | PC | 직원 GUI. 설치·인플레이스 업그레이드. 제거는 `/admin` 승인 후 |

소스의 에이전트 `appsettings.json`은 로컬 개발용입니다 (`pc-demo-001`, `http://localhost:5080`, 빈 토큰). 그대로 PC에 복사하면 DeviceCode가 전 장비에서 겹치거나 서비스가 시작되지 않습니다.

## 2. 사전 요구 사항

- PC: Windows x64, 관리자 PowerShell 5.1 이상. Native AOT라 대상 PC에 .NET 런타임은 필요 없습니다.
- 서버 API URL (HTTPS). HTTP는 loopback 진단만 허용됩니다.
- 서버 `Security:AgentToken`(32자 이상). PC `ApiToken`과 동일. 조회·정책 API는 `Security:AdminToken` 또는 (설정한 경우) JWT access token을 씁니다. 두 정적 토큰은 모두 필수이며 서로 달라야 합니다.
- 자체 패치를 쓸 때: 서버가 가리키는 ZIP(`SwLicenseWatcher.Agent.Worker-{version}.zip`)은 Worker 실행 파일이 있는 폴더 안에 `watchdog-update/`를 두고, Worker 실행 파일은 하나. `RequireAuthenticode`가 `true`이면 EXE/DLL이 신뢰된 Authenticode 서명

토큰과 서명용 PFX는 저장소에 커밋하지 마세요.

### GitHub Release 서명 (선택)

CD는 다음 시크릿이 있으면 publish 산출물의 EXE/DLL에 Authenticode 서명을 합니다. 없으면 바이너리는 서명되지 않습니다.

| 시크릿 | 필수 | 설명 |
| --- | --- | --- |
| `SIGNING_CERTIFICATE_PFX_BASE64` | 서명할 때 | PFX의 Base64 |
| `SIGNING_CERTIFICATE_PASSWORD` | 서명할 때 | PFX 암호 |
| `SIGNING_TIMESTAMP_URL` | | 없으면 `http://timestamp.digicert.com` |

## 3. 서버 API

API는 서버에서만 호스팅합니다. PC 에이전트 설치 대상이 아닙니다. Kestrel(Native AOT)은 `Install-ApiServer.ps1`로 Windows Service로 등록하고, IIS in-process는 아래 수동 절차를 따릅니다. Release ZIP에는 두 가지 서버 산출물이 있습니다.

| 산출물 | 언제 | 설정 예시 |
| --- | --- | --- |
| `api/win-x64/` Native AOT | Kestrel Windows Service (`Install-ApiServer.ps1`) | [appsettings.api.company.json](../deploy/examples/appsettings.api.company.json) |
| `api/iis/win-x64/` framework-dependent | IIS in-process (`web.config`) | [appsettings.api.iis.company.json](../deploy/examples/appsettings.api.iis.company.json) |

공통 키:

| 키 | 필수 | 설명 |
| --- | --- | --- |
| `Security:AgentToken` | 예 | 32자 이상. 스냅샷·하트비트·제거 요청·manifest 조회. PC `ApiToken`과 동일 |
| `Security:AdminToken` | 예 | 32자 이상. 조회·정책·위반·design/schema·업데이트 핀. `AgentToken`과 달라야 함. JWT를 쓰더라도 비상용으로 유지 |
| `Security:RequireHttps` | | 운영은 `true`. 원격 HTTP는 거부, loopback HTTP는 허용 |
| `Security:Jwt:Authority` | | OAuth 2 Resource Server. 비우면 JWT 비활성. HTTPS만 허용. 예: `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| `Security:Jwt:Audience` | Authority가 있을 때 예 | access token `aud`. Entra Application ID URI 또는 클라이언트 ID |
| `Security:Jwt:MetadataAddress` | | 비우면 `{Authority}/.well-known/openid-configuration`. 적으면 HTTPS만 허용 |
| `Security:Jwt:RequiredScope` | Authority가 있을 때 scope 또는 role 중 하나 | `scp`/`scope`에 이 값이 있어야 함. Entra에 `admin` 같은 scope를 둔다 |
| `Security:Jwt:RequiredRole` | Authority가 있을 때 scope 또는 role 중 하나 | `roles`(또는 `RoleClaimType`)에 이 값이 있어야 함. Scope를 쓰면 비워도 된다 |
| `Security:Jwt:ClockSkew` | | 기본 5분 |
| `Storage:SqlServer:ConnectionString` | 예 | `TrustServerCertificate=False` 권장 |
| `Storage:SqlServer:SchemaName` 및 테이블/컬럼 | 예 | 기본 예시는 `inventory.company_pc`, `inventory.company_stale_heartbeat_notification`, `inventory.company_pc_uninstall_request`, `inventory.company_pc_sw_license`, `inventory.company_worker_update_pin` 등. 정책 테이블에는 `default_license_source` 컬럼이 있습니다. 식별자는 영문·숫자·밑줄만 |
| `Database:ApplySchemaInBackground` | | 기본 `true`. 기동 후 백그라운드에서 없는 테이블/컬럼을 `CREATE`/`ALTER TABLE ADD`. 실패 시 재시도 |
| `Database:ApplySchemaOnStartup` | | 기본 `false`. `true`면 요청을 받기 전에 같은 DDL을 적용하고, 실패하면 기동하지 않음 |
| `Updates:Worker:PackageUrl` | 예 | 기동 시드용 절대 HTTPS URI. 살아 있는 핀은 DB이며 `/admin` **업데이트** 또는 `PUT /api/updates/worker/manifest`로 바꿈. GitHub에서 가져오면 캐시된 패키지 URL로 바뀜 |
| `Updates:GitHub:Owner` / `Repository` | | GitHub 저장소. 비우면 가져오기 UI는 숨겨지고 수동 핀만 사용 |
| `Updates:GitHub:Token` | 비공개 repo | `repo` 범위 PAT. 공개 저장소는 비워도 됨 |
| `Updates:GitHub:PackageDirectory` | | Worker ZIP 캐시 폴더. 기본은 API 콘텐츠 루트의 `update-packages` |
| `Updates:Worker:Sha256` | | 시드용. 64자 hex 또는 플레이스홀더. 첫 패키지 전까지 플레이스홀더라도 API는 기동함 |
| `Updates:Worker:Version` | | 시드용. Watchdog는 DB 핀의 Version을 Worker `.version`과 비교 |
| `Updates:Worker:RequireAuthenticode` | | 시드용. 패키지 가져오기는 이 값을 `false`로 저장한다. 서명을 요구하려면 가져온 뒤 핀 저장에서 켠다 |
| `Kestrel:Endpoints` | AOT만 | Kestrel 단독 호스트의 HTTPS 바인딩. **IIS에서는 넣지 않습니다.** 인증서와 포트는 IIS 사이트 바인딩으로 엽니다. |

환경 변수 예: `Security__AgentToken`, `Security__AdminToken`, `Security__Jwt__Authority`, `Security__Jwt__Audience`, `Storage__SqlServer__ConnectionString`, `Database__ApplySchemaInBackground`.

```powershell
$agentToken = .\New-ApiToken.ps1
$adminToken = .\New-ApiToken.ps1
```

`$agentToken`은 서버 `Security:AgentToken`과 PC `ApiToken`에, `$adminToken`은 서버 `Security:AdminToken`에 넣습니다.

### OAuth 2 Resource Server (JWT)

API는 Authorization Server가 아닙니다. Entra ID·Keycloak 등 IdP가 발급한 Bearer access token을 검증합니다. `Security:Jwt:Authority`가 비어 있으면 이전과 같이 정적 토큰만 받습니다.

1. IdP에서 API를 리소스로 등록하고 Application ID URI(예: `api://sw-license-watcher`)를 `Audience`에 넣습니다. Authority와 MetadataAddress는 HTTPS여야 하며, 아니면 API가 기동하지 않습니다.
2. Entra는 **Expose an API**로 `admin` 같은 scope를 만들고 `RequiredScope`에 맞춥니다. `RequiredRole`만 넣어도 됩니다. 둘 다 비우면 API가 기동하지 않고, audience만 맞는 토큰은 관리자가 되지 않습니다.
3. 관리자 API와 `/admin` 입력란에는 `Authorization: Bearer <access token>`을 씁니다. 인증 실패 시 `WWW-Authenticate: Bearer`와 401을 반환합니다.
4. 에이전트 수집·하트비트 경로는 JWT로 열리지 않습니다.

### Kestrel (Native AOT) Windows Service

`Install-ApiServer.ps1`이 `api/win-x64`를 `C:\Program Files\SwLicenseWatcher\Api`에 복사하고, `SwLicenseWatcher.Api` 서비스를 **LocalSystem** 자동 시작·실패 시 재시작으로 등록합니다. 설치 스크립트만 관리자 권한이 필요하고, 서비스 로그온은 설치한 관리자 계정이 아닙니다. API는 `AddWindowsService`로 SCM에 상태를 보고하고, 서비스로 실행 중이면 Application 이벤트 로그(원본 `SwLicenseWatcher.Api`)에 기록합니다. 콘텐츠 루트는 실행 파일 폴더이므로 `appsettings.json`도 그 위치에서 읽습니다. Native AOT(`CreateSlimBuilder`)에서도 `UseKestrelHttpsConfiguration`이 켜져 있어 회사 템플릿의 `Kestrel:Endpoints:Https`가 적용됩니다. 회사 템플릿 [appsettings.api.company.json](../deploy/examples/appsettings.api.company.json)에 연결 문자열과 토큰을 넣습니다. 대상 서버에 .NET 런타임은 필요 없습니다.

관리자 PowerShell에서:

```powershell
.\Install-ApiServer.ps1 `
  -SourcePath D:\SwLicenseWatcher-1.0.1 `
  -ConnectionString $env:Storage__SqlServer__ConnectionString `
  -AgentToken $agentToken `
  -AdminToken $adminToken `
  -ListenUrl "https://0.0.0.0:443" `
  -FirewallPort 443
```

같은 스크립트를 다시 실행하면 서비스를 멈춘 뒤 파일을 교체하고 다시 시작합니다. `-SourcePath`는 압축을 푼 Release 폴더이거나 `api\win-x64` 자체입니다. IIS 산출물(`api\iis\win-x64`)은 거부합니다.

확인:

```powershell
Get-Service SwLicenseWatcher.Api
Invoke-RestMethod -Uri "https://license-watcher.contoso.local/health"
```

브라우저에서 `https://license-watcher.contoso.local/admin` 에 접속합니다. 경로는 그대로이며 화면은 API가 같이 제공하는 Blazor WebAssembly입니다. 데이터 조회에는 `AdminToken`이 필요합니다.

`-ListenUrl`을 생략하면 템플릿의 `Kestrel:Endpoints:Https:Url`을 유지합니다. `-FirewallPort`를 주면 인바운드 TCP 허용 규칙(`SW License Watcher API`)을 만듭니다. 스키마는 기본으로 백그라운드에서 맞춥니다. `-ApplySchemaOnStartup`을 주면 기동을 스키마 적용에 묶습니다.

HTTPS 인증서는 스크립트가 설치하지 않습니다. 서버 인증서를 `LocalMachine\My`에 넣고, `appsettings.json`의 `Kestrel:Endpoints:Https:Certificate:Subject`(예: `CN=license-watcher.contoso.local`)와 맞춥니다. LocalSystem이 개인 키를 읽을 수 있어야 합니다. 진단용으로 콘솔에서 직접 실행할 때만:

```powershell
& "C:\Program Files\SwLicenseWatcher\Api\SwLicenseWatcher.Api.exe"
```

제거:

```powershell
.\Uninstall-ApiServer.ps1                 # 서비스만 삭제, 파일·방화벽 규칙 유지
.\Uninstall-ApiServer.ps1 -RemoveFiles -RemoveFirewall
```

### IIS in-process

Native AOT는 IIS in-process를 지원하지 않습니다. `api/iis/win-x64`를 사용합니다.

1. 서버에 [.NET 10 ASP.NET Core Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0)을 설치합니다. (AspNetCoreModuleV2 + 공유 런타임)
2. IIS에서 애플리케이션 풀을 만듭니다. **.NET CLR 버전: No Managed Code**, 32비트 사용 안 함.
3. 사이트를 만들고 실제 경로를 `api/iis/win-x64` 폴더로 지정합니다. HTTPS 바인딩과 인증서는 IIS에서 설정합니다.
4. [appsettings.api.iis.company.json](../deploy/examples/appsettings.api.iis.company.json)을 해당 폴더의 `appsettings.json`으로 복사하고 토큰·연결 문자열을 넣습니다. `Kestrel:Endpoints`는 넣지 않습니다.
5. 게시 산출물의 `web.config`는 `hostingModel="InProcess"`, `processPath="dotnet"`, `arguments=".\SwLicenseWatcher.Api.dll"`입니다. SSE(`/api/agents/events`)가 끊기지 않도록 `requestTimeout="20:00:00"`이 들어 있습니다. stdout 로그를 쓰려면 `stdoutLogEnabled="true"`로 바꾸고 `logs` 폴더를 만듭니다.
6. 풀을 재순환한 뒤 사이트 URL로 `/health`를 확인하고, 브라우저에서 `/admin` 대시보드가 열리는지 봅니다.

IIS가 TLS를 종료하므로 에이전트의 `ServerBaseUrl`은 사이트 HTTPS 주소입니다.

스키마는 API가 백그라운드에서 맞춥니다. `/health`는 Bearer가 필요 없습니다. 테이블이 아직 없으면 `/health`는 503일 수 있고, 적용이 끝나면 200이 됩니다.

1. 기본: `Database:ApplySchemaInBackground=true`. 기동 후 없는 테이블/컬럼을 추가하고, SQL이 안 되면 재시도합니다.
2. 즉시 적용: `POST /api/schema/sql` (AdminToken). 상태는 `GET /api/schema`.
3. 기동을 스키마에 묶으려면 `Database:ApplySchemaOnStartup=true`. 실패하면 기동하지 않습니다.
4. DBA가 직접 적용하려면 [Apply-DbSchema.ps1](../deploy/scripts/Apply-DbSchema.ps1)에 `-SqlPath`를 넘깁니다. `-WhatIf`로 배치를 검토할 수 있습니다.

```powershell
$headers = @{ Authorization = "Bearer $adminToken" }
.\Apply-DbSchema.ps1 `
  -ConnectionString $env:Storage__SqlServer__ConnectionString `
  -ApiBaseUrl "https://license-watcher.contoso.local" `
  -ApiToken $adminToken `
  -WhatIf
.\Apply-DbSchema.ps1 `
  -ConnectionString $env:Storage__SqlServer__ConnectionString `
  -ApiBaseUrl "https://license-watcher.contoso.local" `
  -ApiToken $adminToken
Invoke-RestMethod -Uri "https://license-watcher.contoso.local/health"
```

로컬 파일로 적용:

```powershell
Invoke-RestMethod -Uri "https://license-watcher.contoso.local/api/schema/sql" -Headers $headers |
  Set-Content -Encoding utf8 .\schema.sql
.\Apply-DbSchema.ps1 -Server sql.contoso.local -Database SwLicenseWatcher -SqlPath .\schema.sql
```

## 4. 클라이언트 appsettings.json

`Install-Agent.ps1`이 회사 템플릿을 복사한 뒤 파라미터로 덮어씁니다. 설치 후 PC의 JSON을 직접 고쳐도 됩니다. 환경 변수(`Section__Key`)와 커맨드라인(`--Section:Key=`)이 JSON보다 우선합니다. 서버 키는 PC JSON에 넣지 않습니다.

### Worker (`Agent`, `LocalState`)

| 키 | 필수 | 회사 기본 / 스크립트 동작 |
| --- | --- | --- |
| `Agent:DeviceCode` | 예 | 미지정 시 컴퓨터 이름. 전 PC가 같으면 서버에서 한 자산으로 UPSERT됩니다. |
| `Agent:DomainName` | 예 | `USERDOMAIN`, 없으면 `WORKGROUP` |
| `Agent:ServerBaseUrl` | 예 | 서버 API 주소, 예: `https://license-watcher.contoso.local` |
| `Agent:ApiToken` | 예 | 서버 `Security:AgentToken`과 **동일**, 32자 이상 |
| `Agent:SnapshotPath` | | `/api/inventory/snapshots` |
| `Agent:HeartbeatPath` | | `/api/agents/heartbeats` |
| `Agent:EventsPath` | | `/api/agents/events` |
| `Agent:PollInterval` / `MaxJitter` | | `00:15:00` / `00:05:00` |
| `Agent:HealthFilePath` | 예 | `C:\ProgramData\SwLicenseWatcher\state\worker-health.json` |
| `LocalState:QueueDirectory` | 예 | `C:\ProgramData\SwLicenseWatcher\state\queue` |
| `LocalState:DpapiScope` | | `LocalMachine` (LocalSystem 서비스) |
| `LocalState:MaxQueuedSnapshots` / `MaxQueueBytes` | | 48 / 64MiB |

환경 변수 예: `Agent__ApiToken`, `Agent__ServerBaseUrl`.

시작 시 실패하는 경우: 빈 `ApiToken`, 비-loopback HTTP `ServerBaseUrl`, 빈 `DeviceCode`.

### Watchdog (`Watchdog`)

Worker와 **같은** `DeviceCode`, `ServerBaseUrl`, `ApiToken`을 넣습니다.

| 키 | 필수 | 회사 기본 / 스크립트 동작 |
| --- | --- | --- |
| `Watchdog:ManifestPath` | | `/api/updates/worker/manifest` |
| `Watchdog:WorkerServiceName` | | `SwLicenseWatcher.Agent.Worker` |
| `Watchdog:WorkerInstallDirectory` | 예 | Worker 설치 경로와 일치해야 함 |
| `Watchdog:WorkerHealthFilePath` | 예 | Worker `HealthFilePath`와 동일 |
| `Watchdog:StagingDirectory` / `BackupDirectory` | 예 | `C:\ProgramData\SwLicenseWatcher\staging` / `backup` |
| `Watchdog:CheckInterval` / `MaxJitter` | | `04:00:00` / `01:00:00` |

환경 변수 예: `Watchdog__ApiToken`, `Watchdog__WorkerInstallDirectory`.

템플릿: [appsettings.worker.company.json](../deploy/examples/appsettings.worker.company.json), [appsettings.watchdog.company.json](../deploy/examples/appsettings.watchdog.company.json).

## 5. 패키저로 회사 Setup.exe 만들기

USB나 그룹웨어로 뿌릴 때는 IT가 Packager GUI에서 회사 설치본 **파일 하나**를 만듭니다. 직원 PC에 .NET SDK는 필요 없습니다. 화면 칸·공지 문안·제거 승인·집 시험은 [packager-setup.ko.md](packager-setup.ko.md)에 모아 두었습니다.

핵심만 적으면:

1. GitHub Release의 `SwLicenseWatcher-{version}.zip`을 풉니다. 같은 폴더에 API와 `Packager.exe`, 런처 스텁 `SwLicenseWatcher-Setup.exe`, `setup-ui/`, 에이전트가 있습니다. **스텁은 직원에게 주지 마세요.** API가 이미 있으면 `SwLicenseWatcher.Packager-{version}.zip`만 풀어도 됩니다.
2. `Packager.exe`를 실행하고 서버 HTTPS 주소와 `Security:AgentToken`을 넣습니다. 패키저 옆에 에이전트가 있으면 공식 Release ZIP 칸은 비워도 됩니다.
3. 저장 위치(기본 바탕화면)의 `SwLicenseWatcher-Setup.exe`만 USB·공지에 올립니다. 전체 ZIP이나 Packager ZIP을 직원에게 주지 않습니다.

직원 설치 화면:

- 서버 주소와 키는 설치본에 들어 있어 다시 묻지 않습니다.
- PC 관리 식별자/자산번호는 선택입니다. 비우면 컴퓨터 이름을 씁니다. 추측해서 쓰지 마세요.
- 설치 후 설정 앱에 SW License Watcher가 보이며, 제거는 `/admin` **제거 요청** 승인 뒤에만 진행됩니다.

업그레이드는 새 릴리스로 다시 포장한 Setup.exe를 실행하면 됩니다. 기존 서비스가 있으면 인플레이스 업그레이드입니다. 제거 요청은 남기지 않습니다. 0.1.0 이상은 서버에서 장치 키를 가져와 확인하고, 그 이전 버전은 비대칭키 확인을 건너뜁니다.

로컬 해제 키는 없습니다. `sc.exe delete`로 서비스를 지우면 서버에는 하트비트 두절로 남습니다.

## 6. PC에 에이전트 설치 (PowerShell / Intune)

서버 API가 이미 떠 있고, 토큰과 HTTPS URL을 알고 있어야 합니다.

Worker를 먼저 기동하고 Watchdog을 올립니다. 두 서비스 모두 **LocalSystem**으로 등록합니다. 설치 PowerShell만 관리자 권한이 필요하고, Watchdog이 Worker를 멈추고 파일을 교체하려면 실행 계정은 LocalSystem이어야 합니다. 로컬 관리자 사용자로 서비스를 돌리면 SCM `Access Denied`가 납니다. 재실행하면 계정까지 LocalSystem으로 되돌린 뒤 에이전트 파일만 갱신합니다. `api` 폴더는 복사하지 않습니다.

```powershell
.\Install-Agent.ps1 `
  -SourcePath D:\SwLicenseWatcher-1.0.1 `
  -ServerBaseUrl "https://license-watcher.contoso.local" `
  -ApiToken $token
```

`-DeviceCode`와 `-DomainName`을 생략하면 컴퓨터 이름과 `USERDOMAIN`을 씁니다. 자산 코드 체계가 있으면 `-DeviceCode`를 명시하세요. 이미 설치된 서비스를 다시 실행하면 인플레이스 업그레이드입니다. 제거 요청을 서버에 남기지 않고, `-DeviceCode`/`-DomainName`을 생략하면 기존 `appsettings.json` 값과 ProgramData의 장치 키를 유지합니다.

확인:

```powershell
Get-Service SwLicenseWatcher.Agent.Worker, SwLicenseWatcher.Agent.Watchdog
Get-CimInstance Win32_Service -Filter "Name='SwLicenseWatcher.Agent.Watchdog'" |
  Select-Object Name, StartName, State
Get-Content C:\ProgramData\SwLicenseWatcher\state\worker-health.json
Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = (Get-Date).AddMinutes(-15) } -MaxEvents 30
```

`StartName`은 `LocalSystem`이어야 합니다.

### Intune / SCCM 무인 설치

MSI는 제공하지 않습니다. Win32 앱 설치 명령으로 스크립트를 호출합니다.

```powershell
powershell.exe -ExecutionPolicy Bypass -File Install-Agent.ps1 -SourcePath D:\SwLicenseWatcher-1.0.1 -ServerBaseUrl https://license-watcher.contoso.local -ApiToken <token>
```

제거 키는 설치본에 없습니다. 현장 PC가 `POST /api/agents/uninstall-requests`로 요청하면 `/admin`의 **제거 요청** 탭에서 승인한 뒤에야 서버가 일회용 코드를 그 PC에 발급합니다. 관리자는 PC 상세에서 **원격 제거 지시**를 할 수 있고, 그러면 다음 heartbeat에서 Worker가 그랜트를 소비한 뒤 Watchdog·Worker 서비스를 지웁니다. `Uninstall-Agent.ps1`은 코드를 소비한 뒤에만 서비스를 지웁니다. 승인 없이 설정 앱에서 지우면 그대로 남습니다. `sc.exe delete`로 우회하면 서버에는 하트비트 두절로 남습니다.

```powershell
powershell.exe -ExecutionPolicy Bypass -File Uninstall-Agent.ps1 `
  -ServerBaseUrl https://license-watcher.contoso.local `
  -ApiToken <token>
```

`-DeviceCode`를 생략하면 컴퓨터 이름을 씁니다. 큐와 백업까지 지울 때만 `-RemoveState`를 붙입니다.

## 7. 에이전트 자체 패치 (클라이언트 동작)

Watchdog이 서버 `GET /api/updates/worker/manifest`를 읽고 Worker를 교체합니다. 패키지 URL은 HTTPS여야 합니다. Release의 `SwLicenseWatcher.Agent.Worker-{version}.zip`은 Worker 실행 파일이 있는 폴더에 `watchdog-update/`를 함께 두고, Worker 실행 파일은 하나여야 합니다. 설치된 Watchdog는 그 폴더만 복사하므로, 다음 버전 ZIP이면 Watchdog 파일도 같이 전달됩니다. Watchdog는 Worker를 멈추고 백업한 뒤 교체하고, health가 확인되면 커밋 표시를 남깁니다. 그다음 Worker가 Watchdog를 백업하고 교체한 뒤 서비스를 다시 시작합니다. 예전 Worker-only ZIP은 `watchdog-update/`가 없어 Worker만 갱신합니다.

appsettings `Updates:Worker`는 **기동 시드**입니다. 테이블에 행이 없을 때만 복사되고, 이후 핀은 DB가 권위입니다. 파일을 고쳐도 이미 시드된 핀은 바뀌지 않습니다.

운영 절차:

1. API `Updates:GitHub:Owner`와 `Repository`에 CD가 올리는 저장소를 넣습니다. 비공개면 `Token`도 넣습니다.
2. `/admin` **업데이트**에서 릴리스를 고르고 **패키지 가져오고 핀 저장**을 누릅니다. 서버가 `SHA256SUMS.txt`로 Worker ZIP을 검증한 뒤 `Updates:GitHub:PackageDirectory`에 캐시하고, 핀의 `PackageUrl`을 `https://<API>/api/updates/worker/package/{version}`으로 저장합니다. `RequireAuthenticode`는 가져오기에서 `false`가 됩니다.
3. Watchdog은 `GET /api/updates/worker/manifest`의 `PackageUrl`을 따라갑니다. 그 주소는 `https://<API>/api/updates/worker/package/{version}`이고, 이 GET은 토큰 없이 ZIP을 돌려줍니다. 0.1.8을 포함한 구 클라이언트는 패키지 주소에 Authorization을 넣지 않습니다. PC가 GitHub에 나갈 필요가 없습니다.

직접 GitHub 자산 URL을 핀에 넣을 수도 있습니다. PC가 GitHub에 닿을 때만 그렇게 하세요. 별도 회사 파일 서버에 ZIP을 올리는 절차는 더 이상 필요하지 않습니다.

Watchdog은 설치 디렉터리를 패키지 내용으로 교체하기 전에 PC의 `appsettings.json`과 `appsettings.*.json`을 보존하고, 복사 후 다시 덮어씁니다. 패키지에 들어 있는 개발용 `appsettings.json`은 이미 설치된 PC에서는 쓰이지 않습니다.

서명이 없는 릴리스(`SIGNING_CERTIFICATE_PFX_BASE64` 없음)는 ZIP 안의 EXE/DLL을 회사 인증서로 서명한 뒤 SHA-256을 다시 계산하거나, 핀의 `RequireAuthenticode`를 `false`로 둡니다. 후자는 권장하지 않습니다.

```powershell
(Get-FileHash -Algorithm SHA256 D:\packages\SwLicenseWatcher.Agent.Worker-1.0.2.zip).Hash
```

실패 시 Watchdog은 백업에서 Worker를 롤백합니다. API 프로세스 자체는 PC에 없습니다.

## 8. 문제 해결

| 증상 | 확인할 것 |
| --- | --- |
| 서비스가 바로 종료 | Event Log. 빈 토큰, 비-loopback HTTP `ServerBaseUrl` |
| `401` | PC `ApiToken`과 서버 `Security:AgentToken`이 다름 |
| 스냅샷이 한 PC로만 보임 | 모든 에이전트가 같은 `DeviceCode`(예: 개발용 `pc-demo-001`) |
| Watchdog이 Worker 서비스에 Access Denied | Watchdog이 LocalSystem이 아님. `Install-Agent.ps1`을 다시 실행하거나 `sc.exe config SwLicenseWatcher.Agent.Watchdog obj= LocalSystem` |
| Watchdog이 업데이트를 안 함 | `/admin` 업데이트 핀 `Version`이 이미 `.version`과 같음, `PackageUrl`이 HTTP, SHA-256 불일치, 또는 DB 핀이 아직 플레이스홀더 |
| Authenticode 실패 | ZIP 안의 EXE/DLL이 회사 신뢰 루트로 서명되지 않음 |
| 업데이트 후 롤백 | Worker가 `HealthFilePath`에 버전을 못 씀. 경로가 Worker/Watchdog JSON에서 같은지 |
| IIS `500.30` / `500.31` | Hosting Bundle(.NET 10) 설치, 앱 풀 No Managed Code, `api/iis`를 쓰는지(AOT 폴더 아님) |
| IIS에서 Kestrel 포트 충돌 | IIS용 JSON에 `Kestrel:Endpoints`가 있으면 제거. HTTPS는 사이트 바인딩만 사용 |
| API 서비스가 바로 종료 | Event Log. 빈/짧은 토큰, AgentToken=AdminToken, 남아 있는 `Security:Token`, 연결 문자열, `Kestrel` 인증서 Subject가 LocalMachine\My와 다른지 |
| 서비스 시작 1053 오류 | Application 이벤트 로그(원본 `SwLicenseWatcher.Api`). `appsettings.json`이 실행 파일과 같은 폴더(`C:\Program Files\SwLicenseWatcher\Api`)에 있는지, 토큰·연결 문자열·Kestrel HTTPS 인증서를 확인 |
| `Install-ApiServer.ps1`이 IIS 폴더를 거부 | `api\win-x64`(Native AOT)를 넘기세요. IIS는 위 IIS in-process 절을 따릅니다 |

```powershell
.\Uninstall-ApiServer.ps1                         # API 서비스만
.\Uninstall-ApiServer.ps1 -RemoveFiles -RemoveFirewall
.\Uninstall-Agent.ps1 -ServerBaseUrl https://license-watcher.contoso.local -ApiToken $agentToken
.\Uninstall-Agent.ps1 -ServerBaseUrl https://license-watcher.contoso.local -ApiToken $agentToken -RemoveState
```

## 설치 경로

| 항목 | 기본 경로 |
| --- | --- |
| API (Kestrel Windows Service) | `C:\Program Files\SwLicenseWatcher\Api` |
| API 서비스 이름 | `SwLicenseWatcher.Api` (LocalSystem) |
| Worker | `C:\Program Files\SwLicenseWatcher\Agent.Worker` |
| Watchdog | `C:\Program Files\SwLicenseWatcher\Agent.Watchdog` |
| 에이전트 서비스 계정 | LocalSystem (`SwLicenseWatcher.Agent.Worker`, `SwLicenseWatcher.Agent.Watchdog`) |
| Worker 업데이트 핀 | `inventory.company_worker_update_pin` (기본 예시 이름) |
| 헬스 파일 | `C:\ProgramData\SwLicenseWatcher\state\worker-health.json` |
| 스냅샷 큐 | `C:\ProgramData\SwLicenseWatcher\state\queue` |
| 업데이트 staging / backup | `C:\ProgramData\SwLicenseWatcher\staging`, `backup` |
