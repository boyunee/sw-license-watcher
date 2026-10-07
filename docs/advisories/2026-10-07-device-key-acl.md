# GHSA 초안: 기기 개인키 ACL 적용 실패를 무시함

상태: 초안. 공개 권고로 제출하지 않음.  
심사 항목: 낮음 1.  
제품: sw-license-watcher Agent Worker

## 요약

기기 개인키 파일의 ACL 적용이 실패해도 Worker는 그 사실을 무시하고 계속 동작한다. 실패하면 같은 PC에서 암호문을 읽을 수 있는 사용자가 DPAPI LocalMachine으로 개인키를 복호화할 수 있다.

## 분류

| 항목 | 값 |
| --- | --- |
| 심각도 | Low |
| CVSS | `CVSS:3.1/AV:L/AC:H/PR:L/UI:N/S:U/C:H/I:L/A:N` (5.3) |
| CWE | CWE-280 Improper Handling of Insufficient Permissions or Privileges |
| 영향 버전 | 이 ACL 처리가 들어 있는 릴리스 전체 |
| 수정 버전 | 없음 |

CVSS 수치 구간은 Medium에 걸친다. 권고 심각도는 심사 등급인 Low를 유지한다. ACL API가 실제로 실패해야 성립하고, 성공하면 SYSTEM과 Administrators만 읽는다.

## 설명

`LocalIdentityFileAcl.Apply`는 보안 설명자를 만든 뒤 `SetFileSecurity`를 호출한다. 변환에 실패하면 반환하고, `SetFileSecurity`의 성공 여부는 확인하지 않는다. 실패하면 파일은 디렉터리에서 물려받은 ACL로 남는다.

개인키 파일은 DPAPI `LocalMachine`으로 보호한다. 이 범위는 암호문을 읽을 수 있는 같은 PC의 사용자가 복호화할 수 있다. ACL 적용이 성공하면 읽기는 SYSTEM과 Administrators로 제한된다.

## 영향

ACL 적용이 실패한 PC에서는 로그온 사용자가 기기 개인키를 복호화할 수 있다. 그 키는 해당 기기의 증명과 사용자 Toast 서명에 쓰인다. 적용이 성공한 PC에서는 이 경로가 로컬 관리자로 한정된다.

## 완화

개인키 파일 ACL 적용이 실패하면 Worker는 방금 쓴 키 파일과 임시 파일을 지우고 예외를 던진다. 그 키는 메모리에 남기지 않으며, 증명이나 Toast 서명에 쓰지 않는다. 공개키 파일의 Users 읽기 권한은 이 실패 조건에 포함하지 않는다.

## 관련 코드

- `src/SwLicenseWatcher.Agent.Worker/LocalIdentityFileAcl.cs`
- `src/SwLicenseWatcher.Agent.Worker/AgentDeviceIdentityStore.cs`
- `src/SwLicenseWatcher.Core/LocalStateProtection.cs`
