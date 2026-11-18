# DB 장애 주입 시험

Server와 DB 사이의 연결 단절과 저장은 됐지만 응답을 받지 못한 상황의 재전송을 통합 테스트로 확인합니다.

## 준비물

- [Toxiproxy 2.13.0](https://github.com/Shopify/toxiproxy/releases)의 `toxiproxy-server-windows-amd64.exe`
- TCP/IP가 켜져 있고 포트 1433에서 수신하는 SQL Server
- Windows 인증을 사용하는 테스트 연결 문자열 환경 변수 `MINIMES_TEST_SQL_CONNECTION_STRING`
- 응답 유실 시험을 위한 `https://+:5443/` 포트의 신뢰된 개발용 인증서 바인딩과 URL 예약

## 서버 실행

PowerShell에서 실행합니다.

```powershell
& "<설치 폴더>\toxiproxy-server-windows-amd64.exe" -host 127.0.0.1 -port 8474
```

API에는 인증이 없으므로 127.0.0.1에서만 엽니다. 콘솔 창을 클릭하면 선택 모드로 멈추므로 클릭하지 마세요. 선택 모드는 `Esc`로 해제합니다.

## 프록시 생성

API가 User-Agent가 없는 요청을 거부하므로 `-UserAgent`를 지정합니다.

```powershell
$body = @{ name = "minimes-sql"; listen = "[::1]:21433"; upstream = "127.0.0.1:1433"; enabled = $true } | ConvertTo-Json
Invoke-RestMethod -UserAgent "minimes-tests" -Method Post -Uri http://127.0.0.1:8474/proxies -ContentType "application/json" -Body $body
```

프록시 경유 Windows 인증은 IPv6 루프백 리터럴 `[::1]`로 접속할 때만 성공했습니다. 원인은 확인되지 않았습니다.

## 응답 유실 시험

같은 `minimes-sql` 프록시를 사용합니다. 테스트는 Server를 HTTPS로 실제 호스팅하고 DB 응답 방향에 지연 독성(`response-latency`)을 걸어 클라이언트가 제한 시간에 응답을 포기하게 합니다. 직접 연결로 저장을 확인한 뒤 독성을 지우고 같은 묶음을 재전송하여 중복 없이 한 번만 저장되는지 확인합니다.

DB를 늦춰 응답이 늦어 클라이언트가 포기한 상황을 재현하므로 네트워크에서 응답 자체가 사라지는 경우와는 다릅니다.

## 테스트 실행

```powershell
dotnet test test/IntegrationTests/IntegrationTests.csproj --filter Category=Toxiproxy
```

Toxiproxy 없이 나머지 통합 테스트를 실행하려면 다음 명령을 사용합니다.

```powershell
dotnet test test/IntegrationTests/IntegrationTests.csproj --filter Category!=Toxiproxy
```

## 프록시 복구와 정리

프록시가 꺼진 채 남았다면 다음 명령으로 다시 켭니다.

```powershell
Invoke-RestMethod -UserAgent "minimes-tests" -Method Post -Uri http://127.0.0.1:8474/proxies/minimes-sql -ContentType "application/json" -Body '{"enabled": true}'
```

독성이 남았다면 다음 명령으로 지웁니다.

```powershell
Invoke-RestMethod -UserAgent "minimes-tests" -Method Delete -Uri http://127.0.0.1:8474/proxies/minimes-sql/toxics/response-latency
```

시험 후 프록시를 삭제하려면 다음 명령을 사용합니다.

```powershell
Invoke-RestMethod -UserAgent "minimes-tests" -Method Delete -Uri http://127.0.0.1:8474/proxies/minimes-sql
```
