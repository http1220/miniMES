# 0002. .NET Framework 4.8 + WinForms + C# 7.3

- 상태: 채택
- 날짜: 2026-09-30

## 맥락

설비 현장의 데스크톱 프로그램은 .NET Framework 기반이 여전히 많다. 이 프로젝트는 최신 런타임의 편의 기능 없이도 실패 처리를 설계로 풀 수 있음을 보이고자 한다.

## 결정

- 모든 프로젝트(시뮬레이터 포함)는 .NET Framework 4.8
- 화면은 WinForms, 차트는 내장 `System.Windows.Forms.DataVisualization`
- C# 언어 버전은 4.8 기본값인 **7.3**으로 고정
- 유료 UI 컴포넌트는 쓰지 않는다
- 주요 패키지(모두 무료 오픈소스, 2026-09-30 기준 최신 안정판)

| 영역 | 패키지 |
|---|---|
| HTTP Server | Microsoft.AspNet.WebApi.OwinSelfHost 5.3.0 |
| JSON | Newtonsoft.Json 13.0.4 |
| DB 드라이버 | Microsoft.Data.SqlClient 7.1.1 |
| DB 접근 | Dapper 2.1.89 |
| 마이그레이션 | dbup-sqlserver 7.2.0 |
| 로그 | Serilog 4.4.0, Serilog.Sinks.File 7.0.0, Serilog.Formatting.Compact 3.0.0 |
| 테스트 | xunit 2.9.3, xunit.runner.visualstudio 4.0.0 |
| 시뮬레이터 전용 | FluentModbus |

## 검토한 대안

- **.NET 8 이상**: 편의 기능이 많지만 목표 환경과 다르다
- **C# 상위 버전 지정**: 문법은 편해지지만 기능마다 런타임 지원 여부가 달라 함정이 있다
- **EF6**: SQL이 드러나지 않아 DB 설계 의도를 보여주기 어렵다

## 결과

- `PeriodicTimer`, `System.Threading.Channels` 같은 최신 API 대신 대체 수단을 쓴다
- MQTT 라이브러리 등 일부 패키지는 .NET Framework 지원이 끝난 구버전에 묶인다(ADR 0007)
- OWIN 셀프 호스팅은 Windows HttpListener 기반이라 원격 수신과 HTTPS에 운영체제 설정(URL 예약, 인증서 바인딩)이 필요하다. 절차는 개발용 인증서 생성 → 인증서 바인딩 → URL 예약 순이며 관리자 권한이 필요하다(구현 단계에서 확인)
