# GHSA 초안: JWT를 켜고 scope와 role이 비어 있으면 audience 토큰이 관리자이다

상태: 초안. 공개 권고로 제출하지 않음.  
심사 항목: 중간 1.  
제품: sw-license-watcher API

## 요약

OAuth 2 리소스 서버를 켜고 `RequiredScope`와 `RequiredRole`을 둘 다 비우면, 그 audience의 액세스 토큰은 관리자 API 전체가 된다. Authority와 메타데이터 주소에 HTTP를 허용한다.

## 분류

| 항목 | 값 |
| --- | --- |
| 심각도 | Medium |
| CVSS | `CVSS:3.1/AV:N/AC:H/PR:L/UI:N/S:U/C:H/I:H/A:N` (6.8) |
| CWE | CWE-862 Missing Authorization |
| 영향 버전 | JWT 리소스 서버 인증이 있는 릴리스 (0.2.0부터) |
| 수정 버전 | 없음 |

공격 복잡도를 High로 둔 이유는 기본 구성에서 JWT가 꺼져 있기 때문이다. Authority를 설정하고 두 제약을 비운 운영에서만 성립한다.

## 설명

`JwtAccessTokenAuthenticator.HasRequiredScope`와 `HasRequiredRole`은 해당 설정이 비어 있으면 통과한다. `ApiSecurityOptionsValidator.HasValidJwt`는 Authority와 Audience, 시계 오차만 검사하고 scope와 role은 요구하지 않는다.

issuer, audience, 수명, 서명키 검증은 수행한다. scope 또는 role을 넣어 두면 그 값이 없는 토큰은 거절한다. 테스트 `Valid_token_is_accepted_as_admin`은 scope 클레임이 없는 토큰을 관리자로 받는다. `Missing_required_scope_is_rejected`와 `Missing_required_role_is_rejected`는 값을 설정한 경우에만 거절을 확인한다.

같은 검증기는 Authority와 MetadataAddress에 HTTP URI를 허용한다. 메타데이터 조회는 그 주소가 `https://`로 시작할 때만 HTTPS를 요구한다.

JWT가 꺼져 있으면 이 권고에 해당하지 않는다. 관리자 API는 그때 정적 `AdminToken`만 받는다. JWT는 에이전트 수집 경로를 열지 않는다.

## 영향

`Security:Jwt:Authority`만 채운 배포에서는 그 audience로 발급된 액세스 토큰이 정책, 스키마, 제거 승인, 업데이트 핀을 포함한 관리자 API를 호출할 수 있다. 테넌트 사용자가 그 audience의 토큰을 받을 수 있으면 관리자와 같은 권한이 된다.

HTTP 메타데이터 주소는 서명키 조회가 네트워크에서 변조될 수 있다.

## 완화

JWT를 켜면 `RequiredScope` 또는 `RequiredRole` 중 하나가 있어야 하고, Authority와 MetadataAddress는 HTTPS여야 한다. 하나라도 빠지면 API가 기동하지 않는다. 두 제약이 모두 비어 있으면 인증기도 토큰을 거절한다. Audience만 맞는 토큰은 관리자가 되지 않는다.

## 관련 코드

- `src/SwLicenseWatcher.Api/JwtAccessTokenAuthenticator.cs`
- `src/SwLicenseWatcher.Core/Configuration.cs` (`ApiSecurityOptionsValidator.HasValidJwt`)
- `tests/SwLicenseWatcher.Api.Tests/JwtAccessTokenAuthenticatorTests.cs`
