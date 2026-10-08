# sw-license-watcher

.NET 10 기반의 기업용 소프트웨어 라이선스 감시 서비스입니다. 로컬 PC 에이전트는 **Watchdog + Worker**의 투 프로세스 Windows Service 구조를 사용하고, 서버는 **ASP.NET Core API**와 SQL Server로 소프트웨어 수집과 업데이트 메타데이터를 제공합니다.

## 구현된 핵심 요구사항

- **Windows Service 2개** (로그온은 **LocalSystem**. 설치·제거 Setup과 설치 스크립트는 관리자 권한이 필요함)
  - `SwLicenseWatcher.Agent.Watchdog`: 자체 패치, SHA-256/Authenticode 검증, 백업/롤백 정책 담당. Worker 서비스를 멈추고 교체하므로 사용자 계정(로컬 관리자 포함)으로 돌리지 않음
  - `SwLicenseWatcher.Agent.Worker`: 설치 소프트웨어 수집, heartbeat/snapshot 전송 담당. heartbeat `Status`는 스냅샷이 전달되면 `Healthy`, 재시도를 위해 큐에 적재되면 `Degraded`, API가 거절하면 `Rejected`
- **로컬 상태 저장 설계**
  - 전송 실패 스냅샷을 원자적 파일 큐에 저장하고 다음 주기에 재전송
  - 큐가 모두 전송되기 전에는 새 스냅샷을 전송하지 않고 큐에 적재하여 오래된 전체 스냅샷이 최신 스냅샷을 덮어쓰지 않도록 방지
  - 큐 페이로드는 DPAPI(`LocalMachine` 기본값)로 보호
  - `LocalState:MaxQueuedSnapshots`, `LocalState:MaxQueueBytes` 할당량 초과 시 가장 오래된 스냅샷부터 제거
- **서버 측 API**
  - 수집 API: `/api/inventory/snapshots`, `/api/agents/heartbeats`
  - 사용자 알림: 관리자가 `/admin`에서 명시적으로 보낸 제목·본문만. Worker는 `GET /api/agents/events` SSE로 받고, 끊기면 heartbeat/snapshot 응답의 `UserMessageCommand`로 보조 전달. Session 0 서비스가 장치 ML-DSA-87 키로 서명한 페이로드만 `toast/SwLicenseWatcher.Agent.Toast.exe`가 받아 사용자 세션에서 OS 표준 Toast를 표시
  - 조회 API: `/api/inventory/devices`, `/api/inventory/software` (JSON 및 `?format=csv`, `?classification=` 필터)
  - SQL Server 트랜잭션 기반 PC UPSERT 및 설치 소프트웨어 교체 저장(정책 매칭 분류 포함)
  - 현장 설치 시 자산코드는 임시값(기본 컴퓨터 이름)이고, 관리자가 서버에서 바꿀 수 있다. 기기의 고윳값은 서버 CA가 발급하는 **ML-DSA-87**(FIPS 204 최상위, NIST Level 5) 사설 인증서다. 서명·검증은 44/65보다 느리다.
  - Bearer 토큰 인증(에이전트/관리자 역할 분리). 관리자 API는 선택적으로 OAuth 2 Resource Server JWT도 허용. 원격 요청 HTTPS 강제, 수집 POST 본문 크기 제한(스냅샷 8 MiB, 하트비트 64 KiB)
  - 헬스체크: `/health`(인증 제외)는 SQL Server에 `SELECT 1`로 연결을 확인하고, 실패 시 503과 일반화된 사유만 반환
  - 설계/스키마 API: `/api/design`, `/api/schema`, `GET`/`POST` `/api/schema/sql`. 기동 후 백그라운드에서 없는 테이블/컬럼을 맞춤
  - 업데이트: `/api/updates/worker/manifest`, GitHub Release 가져오기 `/api/updates/worker/github`, 캐시된 Worker ZIP `GET /api/updates/worker/package/{version}` (토큰 없음. 0.1.x 클라이언트가 이 주소로 파일을 받음)
  - 소프트웨어 정책 CRUD: `/api/policies` (목록은 페이징·검색·분류 필터·CSV)
  - 블랙리스트 위반 목록: `/api/violations` (페이징·검색·기간 필터·CSV)
  - 관리자 대시보드: `/admin` (Blazor WebAssembly, 외부 CDN/npm 없음). 페이지는 인증 없이 열리고, 데이터 API는 `AdminToken` 또는 설정된 JWT access token이 필요. API가 `_framework`를 같이 제공합니다. PC 상세에서 원격 제거를 지시하면 다음 heartbeat/스냅샷 응답으로 Worker가 일회용 그랜트를 소비하고 서비스를 지웁니다.
  - 제거 그랜트: 현장 Setup/`Uninstall-Agent.ps1` 요청은 `/admin` 승인 후에만 해제되고, 관리자 `POST /api/uninstall-requests`는 이미 승인된 원격 지시(7일)로 기록됩니다. 대시보드에는 해제 코드를 보여 주지 않습니다.
  - 스냅샷 수신 시 설치 SW를 정책과 매칭해 white/managed/black/unclassified로 분류하고, 분류 결과를 설치 SW 테이블에 저장하며, 블랙리스트 적발을 `company_sw_violation`에 기록
- **서버 알림 (웹훅 + SMTP)**
  - Teams/Slack incoming webhook(`{ "text": "..." }`)과 SMTP로 운영 알림 전송
  - 신규 스냅샷에서 이전에 없던 소프트웨어가 보이면 PC·이름·버전 요약 알림
  - 스냅샷에서 해당 PC에 없던 블랙리스트 위반이 새로 적발되면 PC·소프트웨어·정책 패턴 알림(기존 위반은 재알림하지 않음)
  - heartbeat가 설정된 시간(기본 24시간) 이상 두절된 PC는 PC당 1회만 알림(복구 후 다시 두절되면 재알림)
  - 알림 전송 실패는 로그만 남기고 스냅샷/heartbeat 저장에는 영향을 주지 않음
- **SQL Server 스키마 커스터마이징**
  - PC entity / PC installed software / software policy(white|managed|black) / software violation 테이블 이름과 컬럼 이름 모두 `appsettings.json`에서 변경 가능
- **수집 방식 안전장치**
  - `Win32_Product` / WMI 미사용
  - `HKLM(64)`, `HKLM(32)`, `HKCU`, 로드된 `HKEY_USERS` 사용자 SID의 `Uninstall` 키 순회
  - 접근이 거부된 레지스트리 키는 개별 건너뛰기
  - PC OS 설명은 `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion`에서 `Windows 11 Pro 24H2 (10.0.26100.4351) x64`처럼 제품명·기능 업데이트·커널 빌드·아키텍처를 조합한다(Windows 11도 ProductName이 Windows 10으로 나오면 빌드 22000 이상으로 보정)
- **자체 패치 안정성**
  - 랜덤 Jitter 기반 업데이트 주기
  - HTTPS 패키지 다운로드, SHA-256 및 WinVerifyTrust Authenticode 검증
  - ZIP 경로 이탈 방지, 서비스 중지/교체/시작, 헬스체크 실패 시 자동 롤백
  - 헬스체크는 Worker가 직접 기록하는 로컬 health 신호 파일(설치 버전 + 기록 시각)을 롤백 제한 시간 내에서 확인
- **Native AOT 컴파일**
  - Worker/Watchdog와 API 단독 호스트는 `PublishAot=true`로 네이티브 바이너리 publish
  - API는 IIS in-process용으로 `PublishAot=false` 산출물도 함께 배포합니다
  - Source-generated 구성 바인딩(`EnableConfigurationBindingGenerator`)과 System.Text.Json source generator를 사용해 리플렉션 없이 동작

## 프로젝트 구조

- `/src/SwLicenseWatcher.Core`: 에이전트·API·Admin이 공유하는 요청/응답 계약과 옵션, 검증·정책 매칭·수집 등 공통 기능
- `/src/SwLicenseWatcher.Crypto`: BouncyCastle ML-DSA-87 사설 기기 인증서(서버 CA 발급, 클라이언트 키 생성). 44/65보다 느린 최상위 파라미터
- `/src/SwLicenseWatcher.Application`: 저장 기술과 분리된 애플리케이션 결정 로직 및 snapshot, heartbeat, 조회, 정책, 위반, 제거 요청, 업데이트 핀별 persistence port
- `/src/SwLicenseWatcher.Infrastructure`: Application port의 SQL Server 구현, 데이터 컨텍스트, 스키마 적용 및 DI 등록
- `/src/SwLicenseWatcher.Api`: 인증·HTTP endpoint·호스팅을 구성하고 Application/Infrastructure를 조립하는 ASP.NET Core API
- `/src/SwLicenseWatcher.Admin`: 기능별 Devices/Software/Policies/Violations/Uninstall/Updates 컴포넌트와 공통 drawer·pagination으로 구성된 `/admin` Blazor WebAssembly (API `wwwroot/admin`으로 publish)
- `/src/SwLicenseWatcher.Agent.Worker`: inventory 수집 Windows Service. 사용자 Toast 헬퍼를 `toast/`에 같이 publish
- `/src/SwLicenseWatcher.Agent.Toast`: OS 표준 Toast만 띄우고 종료하는 사용자 세션 헬퍼
- `/src/SwLicenseWatcher.Agent.Watchdog`: manifest 조회·다운로드·검증·안전한 압축 해제·서비스 제어·배포·health 확인/롤백 collaborator로 구성된 self-update Windows Service
- `/src/SwLicenseWatcher.Setup.Core`: 페이로드 생성/검증과 회사 설정, 에이전트 설치·서비스 등록, 관리자 승인 기반 제거를 조율하는 공통 orchestration
- `/src/SwLicenseWatcher.Setup.Launcher`: Native AOT 런처. 뒤에 붙은 zip을 풀어 설치 GUI를 실행
- `/src/SwLicenseWatcher.Setup`: 직원용 WinForms 설치/제거
- `/src/SwLicenseWatcher.Packager`: IT용 WinForms 패키저. 런처에 회사 설정을 붙여 Setup.exe 하나를 만듦
- `/tests/SwLicenseWatcher.*.Tests`: Core, Application, Infrastructure, API, Admin, 에이전트와 Setup 계층별 테스트
- `/deploy/examples`: 서버 API·PC 에이전트용 `appsettings.json` 템플릿
- `/deploy/scripts`: 서버 API(Kestrel Windows Service) 및 PC Worker·Watchdog 설치/제거 스크립트, SQL Server 스키마 적용 스크립트
- `/docs/company-deployment.md`: 서버 설정 예시와 회사 PC 클라이언트 배포 절차
- `/docs/packager-setup.ko.md`: USB·그룹웨어용 회사 Setup 사용 안내 (패키저·직원 설치·제거 승인)
- `/docs/hands-on-practice.ko.md`: 집 PC IIS+SQL 실습
- `.github/workflows/ci.yaml`: CI (빌드/테스트, Native AOT publish 검증, Dependabot auto-merge 트리거)
- `.github/workflows/auto-merge.yaml`: Dependabot PR 자동 머지
- `.github/workflows/cd.yaml`: CD (버전 스탬프 후 publish, GitHub Release에 통합 ZIP·Worker 자체 패치 ZIP·Packager ZIP·SHA256SUMS)

의존성 방향은 `Core <- Application <- Infrastructure`이며, API가 persistence 구현을 주입합니다. Admin과 에이전트는 Core의 공유 계약을 사용하고, Setup/Packager UI는 Setup.Core의 orchestration을 호출합니다.

## CI/CD

- **CI (`ci.yaml`)**: `main` 대상 push/PR에서 `windows-latest`로 솔루션 Restore/Build/Test를 검증합니다. `build-test`는 SQL Server LocalDB를 기동한 뒤 회사 스키마 매핑으로 스냅샷·정책·위반·heartbeat 통합 테스트를 돌립니다(로컬에 LocalDB가 없으면 해당 테스트는 skip, CI에서는 실패). 이와 병행해 win-x64 Native AOT publish를 수행합니다. AOT 스모크는 의도적으로 DB 없이 `/health` 503을 확인합니다. IL trim/AOT 경고는 자체 코드(SwLicenseWatcher.*)에서 발생하면 실패하고, `Microsoft.Data.SqlClient` 등 서드파티 어셈블리 경고는 요약만 보고합니다. 산출된 native 실행 파일을 smoke-run합니다. Dependabot PR이 두 job을 모두 통과하면 auto-merge 워크플로우를 트리거합니다.
- **CD (`cd.yaml`)**: `main`에서 CI가 성공하면 다음 릴리스 버전을 먼저 계산합니다(최신 `x.y.z` 태그 patch+1). 커밋 메시지 `Update Version To x.y.z`(대소문자·공백 무시, 선택적 `v`)가 있으면 그 버전으로 재정의하고, Actions에서 CD를 `workflow_dispatch`로 돌릴 때 `version` 입력을 넣어도 됩니다. 그 버전으로 `Agent.Watchdog`/`Agent.Worker` Native AOT와 API Native AOT(Kestrel)·IIS in-process, Setup 런처 AOT, Setup/Packager WinForms self-contained를 publish하고, 에이전트 폴더에 `.version`을 씁니다. GitHub 시크릿 `SIGNING_CERTIFICATE_PFX_BASE64`·`SIGNING_CERTIFICATE_PASSWORD`(선택 `SIGNING_TIMESTAMP_URL`)가 있으면 EXE/DLL에 Authenticode 서명을 합니다. Release 자산은 전체 구성 `SwLicenseWatcher-{version}.zip`(API·에이전트·Packager·Setup), Worker·Watchdog 자체 패치용 `SwLicenseWatcher.Agent.Worker-{version}.zip`, API 없이 더 작은 IT용 `SwLicenseWatcher.Packager-{version}.zip`, `SHA256SUMS.txt`입니다. OAuth 2 Resource Server(JWT) 관리자 인증은 0.2.0부터입니다.

Release ZIP 구조:

```
SwLicenseWatcher-{version}.zip
  SwLicenseWatcher-{version}/
    agent-watchdog/win-x64/   Watchdog (.version 포함)
    agent-worker/win-x64/     Worker (.version 포함)
    api/win-x64/              API Native AOT (Kestrel 단독)
    api/iis/win-x64/          API IIS in-process (web.config 포함)
    Packager.exe              IT 패키저 (주소·키를 Setup에 심음)
    SwLicenseWatcher-Setup.exe   런처 스텁 (페이로드 없음)
    setup-ui/SwLicenseWatcher.Setup.exe

SwLicenseWatcher.Agent.Worker-{version}.zip   자체 패치 (Worker 파일과 watchdog-update/)
SwLicenseWatcher.Packager-{version}.zip       위와 같되 API 없음 (IT만 받을 때)
SHA256SUMS.txt                                위 ZIP의 SHA-256
```

## 커스터마이징 포인트

서버 테이블/컬럼 매핑은 `/src/SwLicenseWatcher.Api/appsettings.json`의 `Storage:SqlServer`에서 바꿀 수 있습니다.

예:

- `PcTable.TableName`
- `PcTable.DeviceCodeColumn`
- `InstalledSoftwareTable.TableName`
- `InstalledSoftwareTable.DisplayNameColumn`
- `InstalledSoftwareTable.ClassificationColumn`
- `SoftwarePolicyTable.TableName`
- `SoftwarePolicyTable.ClassificationColumn`
- `SoftwareViolationTable.TableName`
- `StaleHeartbeatNotificationTable.TableName`
- `UninstallRequestTable.TableName`
- `SoftwareLicenseTable.TableName`
- `WorkerUpdatePinTable.TableName`
- `UserMessageTable.TableName`
- `SoftwarePolicyTable.DefaultLicenseSourceColumn`

현재 기본 예시는 다음처럼 커스텀되어 있습니다.

- PC 테이블: `company_pc`
- 설치 SW 테이블: `company_pc_installed_sw`
- 정책 테이블: `company_sw_policy`
- 위반 테이블: `company_sw_violation`
- 하트비트 두절 알림 상태 테이블: `company_stale_heartbeat_notification`
- 제거 요청 테이블: `company_pc_uninstall_request`
- PC별 SW 라이선스 할당 테이블: `company_pc_sw_license`
- Worker 업데이트 핀 테이블: `company_worker_update_pin`
- 정책 테이블 기본 라이선스 컬럼: `default_license_source` (`managed`만)

## 회사 배포

서버 API는 [deploy/examples/appsettings.api.company.json](deploy/examples/appsettings.api.company.json)(Kestrel) 또는 [deploy/examples/appsettings.api.iis.company.json](deploy/examples/appsettings.api.iis.company.json)(IIS)으로 맞추고, 회사 PC에는 Worker·Watchdog만 설치합니다. 서버·Intune 절차는 [docs/company-deployment.md](docs/company-deployment.md), USB·그룹웨어용 설치 관리자는 [docs/packager-setup.ko.md](docs/packager-setup.ko.md), 집 PC 재현은 [docs/hands-on-practice.ko.md](docs/hands-on-practice.ko.md)를 참고하세요.

Kestrel(Native AOT, `api/win-x64`)은 서버에서 [deploy/scripts/Install-ApiServer.ps1](deploy/scripts/Install-ApiServer.ps1)로 Windows Service(`SwLicenseWatcher.Api`)를 **LocalSystem**으로 등록합니다. API는 `AddWindowsService`로 자신을 호스팅하므로 SCM에서 시작해도 콘텐츠 루트는 실행 파일 폴더이고, Application 이벤트 로그 원본은 `SwLicenseWatcher.Api`입니다. Native AOT slim builder에서도 `UseKestrelHttpsConfiguration`으로 `Kestrel:Endpoints:Https`가 적용됩니다. IIS in-process(`api/iis/win-x64`)는 Hosting Bundle과 사이트 바인딩을 쓰는 수동 절차입니다.

```powershell
$agentToken = .\deploy\scripts\New-ApiToken.ps1
$adminToken = .\deploy\scripts\New-ApiToken.ps1
.\deploy\scripts\Install-ApiServer.ps1 `
  -SourcePath D:\SwLicenseWatcher-1.0.1 `
  -ConnectionString $env:Storage__SqlServer__ConnectionString `
  -AgentToken $agentToken `
  -AdminToken $adminToken `
  -ListenUrl "https://0.0.0.0:443" `
  -FirewallPort 443
```

제거는 `.\deploy\scripts\Uninstall-ApiServer.ps1`입니다. 설치 디렉터리와 방화벽 규칙까지 지울 때만 `-RemoveFiles -RemoveFirewall`을 붙입니다.

## 클라이언트 원격 서버 설정

Worker/Watchdog 클라이언트가 접속할 서버 주소는 설정 파일로 지정합니다. Native AOT 빌드에서도 source-generated 구성 바인딩으로 동일하게 동작합니다.

- Worker: `src/SwLicenseWatcher.Agent.Worker/appsettings.json`의 `Agent:ServerBaseUrl`
- Watchdog: `src/SwLicenseWatcher.Agent.Watchdog/appsettings.json`의 `Watchdog:ServerBaseUrl`

원격 서버 예:

```json
{
  "Agent": {
    "ServerBaseUrl": "https://license-watcher.contoso.com"
  }
}
```

환경 변수(`Agent__ServerBaseUrl`, `Watchdog__ServerBaseUrl`) 또는 커맨드라인 인자(`--Agent:ServerBaseUrl=...`)로도 재정의할 수 있습니다. 원격 주소는 HTTPS여야 하며 HTTP는 loopback 진단에만 허용됩니다.

## 필수 보안 및 저장소 설정

토큰과 SQL Server 연결 문자열은 소스에 저장하지 말고 환경 변수 또는 비밀 저장소로 주입합니다. 설정한 토큰은 각각 32자 이상이어야 합니다. `New-ApiToken.ps1`을 두 번 실행해 에이전트용과 관리자용을 따로 만드세요.

토큰은 역할을 분리합니다. `AgentToken`은 에이전트 수집·하트비트·SSE 이벤트·제거 요청·업데이트 manifest 조회만, `AdminToken`은 조회·정책 CRUD·위반·CSV·제거 지시/승인·사용자 알림 발송·`/api/design`·`/api/schema/sql`과 업데이트 핀 저장을 허용합니다. 두 토큰은 모두 필수이며 서로 달라야 합니다. 예전 공용 `Security:Token`은 더 이상 받지 않습니다.

관리자 API는 OAuth 2 Resource Server로도 받을 수 있습니다. `Security:Jwt:Authority`와 `Audience`를 넣으면 해당 IdP가 발급한 access token을 `AdminToken` 대신 쓸 수 있습니다. 에이전트 전용 경로는 정적 `AgentToken`만 허용합니다. `Authority`가 비어 있으면 JWT는 꺼지고 동작은 이전과 같습니다.

```text
Security__AgentToken=<agent-token>
Security__AdminToken=<admin-token>
Security__Jwt__Authority=https://login.microsoftonline.com/<tenant-id>/v2.0
Security__Jwt__Audience=api://sw-license-watcher
Security__Jwt__RequiredScope=admin
Storage__SqlServer__ConnectionString=<sql-server-connection-string>
Agent__ApiToken=<agent-token>
Watchdog__ApiToken=<agent-token>
```

운영 SQL Server 연결에서는 서버 인증서를 검증하고 `TrustServerCertificate=False`를 유지하십시오.

수집 POST 본문은 경로별로 상한을 둡니다. 스냅샷(`/api/inventory/snapshots`)은 8 MiB, 하트비트(`/api/agents/heartbeats`)는 64 KiB입니다. Native AOT slim builder는 MVC `RequestSizeLimit`를 쓰지 않으므로, 인증 미들웨어가 `IHttpMaxRequestBodySizeFeature`로 적용합니다. 한도를 넘기면 413입니다.

스키마 DDL은 idempotent입니다. 없는 테이블은 `IF OBJECT_ID`로 만들고, 없는 컬럼은 `IF COL_LENGTH`로 `ALTER TABLE ... ADD` 합니다. API는 기동 후 백그라운드에서 이 스크립트를 SQL Server에 적용합니다(기본 `Database:ApplySchemaInBackground=true`). 연결이 안 되면 30초마다 재시도하고, 성공하면 그 프로세스에서는 멈춥니다. 기동을 막지 않으므로 SQL이 늦게 떠도 API는 뜹니다.

즉시 적용하려면 관리자 토큰으로 `POST /api/schema/sql`을 호출합니다. 적용 상태는 `GET /api/schema`입니다. `GET /api/schema/sql`은 같은 DDL을 `text/plain`으로만 내려 줍니다(미리보기·[Apply-DbSchema.ps1](deploy/scripts/Apply-DbSchema.ps1)용). DBA가 직접 적용하려면 스크립트에 로컬 `.sql`을 넘기면 됩니다.

```powershell
# 실행 중인 API에 즉시 적용 (AdminToken)
Invoke-RestMethod -Method POST -Uri http://127.0.0.1:5080/api/schema/sql `
  -Headers @{ Authorization = "Bearer $env:Security__AdminToken" }

# 적용 상태
Invoke-RestMethod -Uri http://127.0.0.1:5080/api/schema `
  -Headers @{ Authorization = "Bearer $env:Security__AdminToken" }

# 로컬 파일로 적용 (API 기동 전·DBA 작업)
.\deploy\scripts\Apply-DbSchema.ps1 `
  -Server sql.contoso.local `
  -Database SwLicenseWatcher `
  -SqlPath .\schema.sql

# 미리보기 (DB에 쓰지 않음)
.\deploy\scripts\Apply-DbSchema.ps1 -ConnectionString $cs -SqlPath .\schema.sql -WhatIf
```

기동 전에 반드시 스키마가 있어야 하면 `Database:ApplySchemaOnStartup=true`로 둡니다. 켜면 요청을 받기 전에 같은 DDL을 적용하고, 실패하면 기동하지 않습니다.

```text
Database__ApplySchemaInBackground=true
Database__ApplySchemaOnStartup=false
```

기존 스키마를 사용 중이라면 `discovery_scope` 컬럼이 `NVARCHAR(256)`으로 확장되었으므로 다음과 같이 마이그레이션합니다(테이블/컬럼 이름은 설정값에 맞게 변경).

```sql
ALTER TABLE [inventory].[pc_installed_sw] ALTER COLUMN [discovery_scope] NVARCHAR(256) NOT NULL;
```

설치 소프트웨어 분류(`classification`: `white` | `managed` | `black` | `unclassified`)를 저장하려면 기존 DB에 컬럼을 추가합니다. 이미 있는 행은 다음 스냅샷이 들어올 때까지 `unclassified`로 둡니다.

```sql
ALTER TABLE [inventory].[pc_installed_sw] ADD [classification] NVARCHAR(32) NOT NULL CONSTRAINT [DF_pc_installed_sw_classification] DEFAULT (N'unclassified');
```

하트비트 두절 알림은 PC별로 한 행만 유지하는 `stale_heartbeat_notification` 테이블에 기록합니다. API를 재시작해도 같은 두절에 대해 알림을 다시 보내지 않고, 해당 PC가 하트비트를 보내 정상으로 돌아오면 행을 지워 이후 재두절 때 새 알림이 나갑니다. 기존 DB에는 `Database:ApplySchemaOnStartup` 또는 `Apply-DbSchema.ps1` / `GET /api/schema/sql`로 테이블을 만들면 됩니다(이미 있으면 `IF OBJECT_ID` 가드가 건너뜁니다). 기본 식별자는 다음과 같습니다.

```sql
IF OBJECT_ID(N'[inventory].[stale_heartbeat_notification]', N'U') IS NULL
BEGIN
    CREATE TABLE [inventory].[stale_heartbeat_notification] (
        [stale_heartbeat_notification_id] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [pc_id] BIGINT NOT NULL,
        [notified_at_utc] DATETIMEOFFSET NOT NULL,
        CONSTRAINT [FK_stale_heartbeat_notification_pc_entity] FOREIGN KEY ([pc_id]) REFERENCES [inventory].[pc_entity]([pc_id]) ON DELETE CASCADE,
        CONSTRAINT [UX_stale_heartbeat_notification_pc_id] UNIQUE ([pc_id])
    );
END
```

회사 예시 매핑(`company_pc` / `company_stale_heartbeat_notification`)을 쓰는 기존 DB는 테이블·컬럼·FK 이름을 설정값에 맞게 바꿉니다.

Watchdog에는 Worker 실행 파일이 설치된 `Watchdog__WorkerInstallDirectory`와 업데이트 후 확인할 `Watchdog__WorkerHealthFilePath`도 설정합니다. 이 경로는 Worker의 `Agent__HealthFilePath`와 동일해야 하며, Worker 서비스 계정이 쓰고 Watchdog 서비스 계정이 읽을 수 있어야 합니다. 자체 패치 ZIP(`SwLicenseWatcher.Agent.Worker-{version}.zip`)은 Worker 실행 파일이 있는 폴더에 `watchdog-update/`를 함께 넣습니다. Worker 실행 파일은 하나여야 합니다. Watchdog가 그 폴더로 Worker를 교체하고 health가 확인되면, Worker가 Watchdog를 백업한 뒤 교체하고 서비스를 다시 시작합니다. 두 서비스 모두 설치 폴더의 `appsettings.json`(및 `appsettings.*.json`)을 유지합니다. `RequireAuthenticode`가 `true`(기본)이면 ZIP의 모든 EXE/DLL이 신뢰된 Authenticode 서명을 가져야 합니다. CD에 서명 시크릿이 없으면 릴리스는 서명되지 않으므로, 회사 인증서로 ZIP 내용을 서명한 뒤 재해시하거나 `/admin` **업데이트** 탭에서 `RequireAuthenticode`를 끄세요(비권장).

## 서버 알림 (웹훅 / SMTP)

API 서버는 수집 결과를 바탕으로 Teams/Slack incoming webhook과 SMTP 메일로 알림을 보낼 수 있습니다. 두 채널은 독립적으로 켜고 끌 수 있으며, 둘 다 꺼 두면 알림을 보내지 않습니다. 자격 증명과 webhook URL은 소스에 넣지 말고 환경 변수로 주입하세요.

설정 예시는 [deploy/examples/appsettings.api.json](deploy/examples/appsettings.api.json)을 참고하세요.

```json
{
  "Notifications": {
    "Webhook": {
      "Enabled": true,
      "Url": "https://outlook.office.com/webhook/REPLACE_ME",
      "Timeout": "00:00:10"
    },
    "Smtp": {
      "Enabled": true,
      "Host": "smtp.contoso.local",
      "Port": 587,
      "EnableSsl": true,
      "AllowedCertificateThumbprints": [],
      "UserName": "sw-license-watcher",
      "Password": "",
      "From": "sw-license-watcher@contoso.local",
      "Recipients": [ "it-ops@contoso.local" ]
    },
    "Events": {
      "NewSoftware": true,
      "StaleHeartbeat": true,
      "BlacklistViolation": true
    },
    "StaleHeartbeatThreshold": "1.00:00:00",
    "StaleHeartbeatCheckInterval": "00:15:00"
  }
}
```

| 키 | 설명 |
| --- | --- |
| `Notifications:Webhook:Enabled` | Teams/Slack incoming webhook 사용 여부. 페이로드는 `{ "text": "..." }` |
| `Notifications:Webhook:Url` | webhook이 켜져 있을 때 필수. HTTP 또는 HTTPS 절대 URI |
| `Notifications:Webhook:Timeout` | HTTP 타임아웃. 기본 `00:00:10` |
| `Notifications:Smtp:Enabled` | SMTP 메일 사용 여부 |
| `Notifications:Smtp:Host` / `Port` / `EnableSsl` | SMTP가 켜져 있을 때 호스트·포트·SSL |
| `Notifications:Smtp:AllowedCertificateThumbprints` | 사내 사설 CA SMTP 인증서를 허용할 SHA-1 또는 SHA-256 지문 목록. 시스템 검증이 실패해도 지문이 일치하면 TLS를 허용합니다. 공백·콜론·하이픈은 무시합니다 |
| `Notifications:Smtp:UserName` / `Password` | 비워 두면 익명(또는 서버 기본 자격 증명) 릴레이 |
| `Notifications:Smtp:From` / `Recipients` | SMTP가 켜져 있을 때 발신자와 수신자 목록(1명 이상) |
| `Notifications:Events:NewSoftware` | 이전에 없던 소프트웨어가 스냅샷에 나타나면 알림 |
| `Notifications:Events:StaleHeartbeat` | heartbeat 두절 PC 알림. 발송 여부는 DB에 영속화되어 API 재시작 후에도 같은 두절을 재알림하지 않음 |
| `Notifications:Events:BlacklistViolation` | 해당 PC에 없던 블랙리스트 위반이 새로 적발되면 알림. 이미 기록된 위반은 재알림하지 않음 |
| `Notifications:StaleHeartbeatThreshold` | 두절로 볼 경과 시간. 기본 24시간(`1.00:00:00`) |
| `Notifications:StaleHeartbeatCheckInterval` | 백그라운드 검사 주기. 기본 15분 |

환경 변수 예:

```text
Notifications__Webhook__Url=<teams-or-slack-incoming-webhook>
Notifications__Smtp__Password=<smtp-password>
```

Webhook이 켜진 상태에서 URL이 없거나, SMTP가 켜진 상태에서 Host/From/Recipients가 비어 있으면 API는 시작 시 실패합니다. 알림은 백그라운드 큐로 보내며, SMTP는 Native AOT를 위해 `System.Net.Mail.SmtpClient`를 사용합니다(MailKit 전체 패키지는 AOT 비호환). `EnableSsl`이 켜진 사내 SMTP가 사설 인증서를 쓰면 인증서 오류로 발송이 거절됩니다. 그 경우 서버 인증서 SHA-1 또는 SHA-256 지문을 `AllowedCertificateThumbprints`에 넣으면 해당 지문만 예외 허용합니다. 공인 CA 인증서는 목록이 비어 있어도 시스템 기본 검증을 통과합니다.

## 수동 실행 예시

```bash
dotnet build SwLicenseWatcher.slnx
dotnet run --project src/SwLicenseWatcher.Api
```

API 실행 후:

- `GET /api/design`: 전체 설계 요약
- `GET /api/schema`: 스키마 자동 적용 상태(백그라운드/기동 시 적용 여부, 마지막 성공·오류)
- `GET /api/schema/sql`: 현재 설정 기준 SQL Server DDL (`text/plain`)
- `POST /api/schema/sql`: 같은 DDL을 SQL Server에 즉시 적용
- `GET /health`: SQL Server 연결 확인(인증 불필요). 실패 시 503
- `GET /api/inventory/devices`: 수집된 PC 목록 (페이징·검색·stale heartbeat 필터)
- `GET /api/inventory/software`: 소프트웨어별 설치 PC 수 집계 (`?classification=`으로 분류 필터)

조회 API는 `AdminToken` 또는 JWT access token이 필요합니다. 에이전트 `AgentToken`으로는 호출할 수 없습니다. `?format=csv`를 붙이면 UTF-8 BOM이 포함된 CSV를 내려받아 Excel에서 한글을 깨지지 않게 열 수 있습니다.

| 메서드 | 경로 | 설명 | 주요 쿼리 |
| --- | --- | --- | --- |
| GET | `/api/inventory/devices` | PC 목록 (자산코드, 호스트명, 관리자 PC 명, 메모, 도메인, OS, 에이전트 버전, 마지막 heartbeat/inventory 시각) | `skip`, `take`, `search`(호스트명·지정 PC 명 또는 자산코드), `staleAfterHours`, `format=csv` |
| GET | `/api/inventory/devices/{deviceCode}` | 단일 PC 상세와 설치 소프트웨어 전체(항목별 `classification`) | `classification`, `format=csv` |
| PUT | `/api/inventory/devices/{deviceCode}` | 관리자 자산코드(`assignedDeviceCode`), PC 명(`assignedHostName`), 메모(`adminNotes`). 다음 스냅샷/하트비트 응답으로 클라이언트에 부여됨. 기기 고유값(`deviceId`)은 ML-DSA-87 인증서 | 본문 `assignedDeviceCode?`, `assignedHostName?`, `adminNotes?` |
| GET | `/api/inventory/software` | SW 이름/버전/분류별 설치 PC 수. `managed`는 `companyCount` / `byoCount` / `unassignedCount`. CSV에는 설치 PC의 자산코드·PC 명 목록(`DeviceCodes` / `DeviceNames`, `; ` 구분)이 추가됨 | `skip`, `take`, `search`(이름), `classification`, `format=csv` |
| GET | `/api/inventory/software/{name}/devices` | 해당 SW가 설치된 PC 목록. 항목별 유효 `licenseSource`와 PC 할당 `licenseSourceOverride` | `skip`, `take`, `classification`, `format=csv` |
| PUT | `/api/inventory/software/{name}/classification` | 정확 일치 활성 정책을 만들거나 갱신한 뒤, 이미 모인 설치 행의 분류를 즉시 다시 칠함 | 본문 `classification`, `publisher?`, `defaultLicenseSource?`, `notes?` |
| PUT | `/api/inventory/software/classifications` | 소프트웨어 분류를 한 번에 여러 개 지정(최대 100). 한 트랜잭션 | 본문 `items: [{ name, classification, publisher?, defaultLicenseSource? }]` |
| PUT | `/api/inventory/devices/{deviceCode}/software/{name}/license-source` | PC별 회사/BYO 할당. `null`이면 할당을 지워 정책 기본값으로 복귀 | 본문 `licenseSource`: `company` \| `byo` \| `null` |
| POST | `/api/inventory/devices/{deviceCode}/user-messages` | 해당 PC에 사용자 OS Toast 대기열 추가. SSE가 붙어 있으면 즉시 전달 | 본문 `title`, `body` |
| POST | `/api/inventory/user-messages/broadcast` | 등록된 모든 PC에 같은 알림을 복제 | 본문 `title`, `body` |
| GET | `/api/agents/events` | 에이전트 SSE (`text/event-stream`). `user-message` 이벤트 | `deviceCode` |
| POST | `/api/agents/user-messages/{id}/consume` | Toast 표시 성공 후 대기열 소비(멱등) | 본문 `deviceCode` |

페이징 기본값은 JSON `take=100`, CSV `take=10000`이며 최대 10000입니다. `staleAfterHours`는 마지막 heartbeat가 없거나 지정 시간보다 오래된 PC만 남깁니다. `search`는 SQL `LIKE` 와일드카드가 이스케이프된 부분 일치입니다. `classification`은 `white` | `managed` | `black` | `unclassified`이며, 설치 SW 행에 저장된 분류로 필터링합니다(예: `?classification=unclassified`).

스냅샷 접수 `Location`은 `/api/inventory/devices/{deviceCode}`를 가리키며, 이전 경로인 `GET /api/inventory/snapshots/{deviceCode}`도 같은 상세 응답을 반환합니다.

## 관리자 대시보드

브라우저에서 `https://<server>/admin` 으로 관리자 화면을 엽니다. UI는 Blazor WebAssembly이며, Interactive Server/Auto 회로는 쓰지 않습니다. API가 같은 출처의 `/admin/_framework`를 제공하므로 외부 CDN이 없습니다. curl이나 CSV 없이 PC 목록, 소프트웨어 집계, 위반, 정책, 제거 요청을 조회하고 정책을 만들고 고칠 수 있습니다. 자산코드·PC 명·소프트웨어 이름을 누르면 서랍에서 관련 목록을 보고, 자산코드·PC 명·메모를 남기거나 소프트웨어 분류·메모를 저장할 수 있습니다. PC 상세에서 **이 PC에 알림 보내기**, 장치 탭에서 **전체 PC에 알림 보내기**로 OS Toast를 명시 발송합니다. SSE가 붙은 에이전트는 바로 받고, 아니면 다음 수집(기본 15분 ± 5분, 최대 약 20분)에 실립니다. PC 상세의 **원격 제거 지시**는 승인된 제거 그랜트를 만들고, 해당 PC Worker가 다음 heartbeat에서 서비스를 지웁니다. 관리자가 지정한 자산코드와 PC 명은 다음 에이전트 heartbeat/스냅샷 응답으로 클라이언트에 부여됩니다. 기기 고유값은 서버가 서명하는 ML-DSA-87 사설 인증서이며, 44/65보다 느린 대신 최상위 등급을 씁니다. 소프트웨어 목록에서 행을 체크한 뒤 `white` / `managed` / `black`을 현재 페이지 선택 항목에 일괄 지정할 수 있고, 서랍에서는 개별 분류와 `managed`의 정책 기본 라이선스(회사/BYO)·PC별 덮어쓰기를 편집합니다.

정적 파일(`/admin`, `/admin/`, CSS, `_framework`)은 인증 없이 내려갑니다. 비밀은 없고, 인벤토리·정책 데이터는 모두 `AdminToken` 또는 JWT access token이 있어야 합니다. 값은 브라우저 `sessionStorage`에만 두고, 탭을 닫으면 사라집니다. 쿠키와 `localStorage`는 쓰지 않습니다. 이 화면은 OIDC 로그인 UI를 붙이지 않으며, IdP에서 받은 access token을 같은 입력란에 붙여 넣습니다.

페이지는 외부 CDN·npm 없이 동작하므로 인터넷이 없는 사내망에서도 열립니다. `/admin` 응답에는 `Content-Security-Policy`(`script-src 'self' 'wasm-unsafe-eval'`, 인라인 스크립트/스타일 없음), `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`를 붙입니다.

## 소프트웨어 정책과 위반

정책은 `company_sw_policy`(이름은 설정으로 변경 가능)에 저장합니다. 스냅샷 `POST /api/inventory/snapshots`를 받을 때 설치 SW 이름·버전을 정책과 매칭해 `white` / `managed` / `black` / `unclassified`로 분류하고, 그 결과를 설치 SW 테이블 `classification` 컬럼에 저장합니다. 블랙리스트에 걸린 항목은 `company_sw_violation`에 기록하며, 같은 PC+소프트웨어 이름은 한 행만 유지합니다(최초 적발 시각은 유지하고 마지막 발견 시각만 갱신). 더 이상 설치되어 있지 않거나 블랙이 아니면 해당 PC의 위반 행을 삭제합니다. 스냅샷 저장 후 해당 PC에 없던 위반이 새로 생기면 `Notifications:Events:BlacklistViolation`이 켜져 있을 때 webhook/SMTP 알림을 보내며, 이미 적발된 위반은 다시 알리지 않습니다.

이름 패턴은 정확 일치이며, `*` / `?` 와일드카드로 prefix·부분 일치도 됩니다(`Google Chrome*`, `*Torrent*`). 선택적 `versionPattern`은 정확/와일드카드(`16.*`)이거나 비교식(`>=17.0`, `<18.0`)이고, 쉼표로 AND 조건을 연결할 수 있습니다(`>=17.0,<18.0`). 게시자(`publisher`)가 있으면 이름과 같은 방식으로 매칭합니다. 여러 정책이 동시에 맞으면 **black > managed > white** 순으로 더 강한 분류를 씁니다.

정책·위반 API도 `AdminToken`이 필요합니다. 목록 JSON은 인벤토리 조회와 같이 `skip`, `take`, `totalCount`, `items`를 반환합니다. 쿼리 파라미터를 생략하면 JSON `take=100`, CSV `take=10000`(최대 10000)으로 동작합니다.

| 메서드 | 경로 | 설명 | 주요 쿼리 |
| --- | --- | --- | --- |
| GET | `/api/policies` | 정책 목록 | `skip`, `take`, `search`(정책 이름·버전 패턴·게시자), `classification`, `format=csv` |
| GET | `/api/policies/{id}` | 정책 단건 | |
| POST | `/api/policies` | 정책 생성 (`201` + `Location`) | |
| PUT | `/api/policies/{id}` | 정책 수정 | |
| DELETE | `/api/policies/{id}` | 정책 삭제(관련 위반은 FK CASCADE로 함께 삭제) | |
| GET | `/api/violations` | 현재 블랙리스트 위반 목록 | `skip`, `take`, `search`(디바이스 코드·호스트명·소프트웨어 이름), `since`(최초 적발 시각 ISO 8601), `format=csv` |

목록 `search`는 인벤토리 조회와 같이 SQL `LIKE` 와일드카드가 이스케이프된 부분 일치입니다. 정책 목록 `classification`은 `white` | `managed` | `black`입니다.

`POST` / `PUT` 본문 예:

```json
{
  "productName": "uTorrent*",
  "publisher": null,
  "versionPattern": null,
  "classification": "black",
  "notes": "P2P 금지",
  "enabled": true,
  "defaultLicenseSource": null
}
```

`classification`은 `white` | `managed` | `black` 입니다. `defaultLicenseSource`는 `managed`일 때만 `company` | `byo` | `null`입니다. 같은 이름 SW라도 PC마다 `company_pc_sw_license` 할당으로 덮어쓸 수 있습니다. 스냅샷은 설치 행만 갈아끼우므로 할당은 그대로 남습니다. 기존 DB에는 `Apply-DbSchema.ps1` 또는 `GET /api/schema/sql`의 정책 컬럼 `ALTER`와 `company_pc_sw_license` `CREATE`를 적용하세요.

```powershell
$token = $env:Security__AdminToken
$headers = @{ Authorization = "Bearer $token" }
Invoke-RestMethod -Headers $headers -Uri "http://127.0.0.1:5080/api/inventory/devices?search=PC-01&staleAfterHours=24"
Invoke-RestMethod -Headers $headers -Uri "http://127.0.0.1:5080/api/inventory/software?classification=unclassified"
Invoke-WebRequest -Headers $headers -Uri "http://127.0.0.1:5080/api/inventory/software?format=csv" -OutFile software.csv
Invoke-RestMethod -Headers $headers -Uri "http://127.0.0.1:5080/api/policies?search=Torrent&classification=black"
Invoke-RestMethod -Headers $headers -Uri "http://127.0.0.1:5080/api/violations?search=PC-01&since=2026-01-01T00:00:00Z"
Invoke-WebRequest -Headers $headers -Uri "http://127.0.0.1:5080/api/policies?format=csv" -OutFile policies.csv
```

Worker/Watchdog는 진단용으로 1회 실행 모드도 지원합니다. 콘텐츠 루트는 실행 파일 폴더이므로 cwd와 무관하게 `appsettings.json`을 읽습니다. 설치된 Worker 서비스를 제어하는 Watchdog 진단은 관리자 콘솔에서 실행하세요. 운영 설치본은 LocalSystem 서비스입니다.

```bash
dotnet run --project src/SwLicenseWatcher.Agent.Worker -- --Agent:RunOnceForDiagnostics=true
dotnet run --project src/SwLicenseWatcher.Agent.Watchdog -- --Watchdog:RunOnceForDiagnostics=true
```

## 라이선스

이 프로젝트는 [MIT License](LICENSE)로 배포됩니다. 사용된 서드파티 구성 요소의 라이선스 정보는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)를 참고하세요.
