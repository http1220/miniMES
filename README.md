# miniMES Edge

Modbus 시뮬레이터 5대의 센서 데이터를 수집·감지하고, 설비 감시와 고장 기록을 전산화하는 .NET Framework 4.8 WinForms 애플리케이션입니다.

**개발 진행 중입니다.**

## 구성

| 프로젝트 | 역할 |
|---|---|
| Simulator | Modbus TCP 시뮬레이터 5대 역할 |
| Collector | 설비 데이터 수집 |
| Server | DB 접근, 수집기·화면 중계 |
| Dashboard | 설비 감시·고장 기록 화면 |

## 빌드

```
dotnet build MiniMesEdge.slnx
```

## 테스트

```
dotnet test test/UnitTests/UnitTests.csproj
```

통합 테스트(`test/IntegrationTests`)는 외부 SQL Server가 필요해 로컬에서만 실행합니다.

## 실행

`Collector`·`Server`·`Dashboard`는 실행 전 `.env` 파일이 필요합니다. 각 프로젝트 폴더의 `.env.example`을 복사해 `.env`로 만들고 값을 채우세요(DB 접속 정보 등은 직접 준비한 SQL Server 기준).
