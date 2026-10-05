# 0004. 프로젝트 구성과 참조 규칙

- 상태: 채택
- 날짜: 2026-09-30

## 맥락

판정 규칙(통신 이상, 백오프, 시각 계산, MTBF·MTTR, 상태 전환)을 CI에서 DB·네트워크 없이 검증하려면 입출력과 분리돼야 한다. "DB는 Server만 접근한다"(ADR 0003) 같은 결정은 문서가 아니라 구조로 강제하고 싶다.

## 결정

| 프로젝트 | 종류 | 참조 |
|---|---|---|
| Core | 라이브러리: 순수 로직 + 전송용 일반 클래스 | 없음 |
| Modbus | 라이브러리: 직접 구현한 Modbus TCP 클라이언트 | Core |
| Data | 라이브러리: DB 접근 | Core |
| Collector | 콘솔 exe | Core, Modbus |
| Server | 콘솔 exe | Core, Data |
| Dashboard | WinForms exe | Core |
| Simulator | exe | FluentModbus |
| UnitTests | 테스트(CI) | Core, Modbus |
| IntegrationTests | 테스트(로컬) | 전부 |

- Data를 참조하는 실행 파일은 Server뿐이다. 다른 곳에서 DB에 접근하면 빌드가 실패한다
- Modbus 라이브러리(FluentModbus)는 Simulator만 참조한다. 수집기는 직접 구현한 클라이언트만 쓴다
- Core는 아무것도 참조하지 않는다(Functional Core, Imperative Shell)

## 검토한 대안

- **UI / BLL / DAL 3계층**: 로직 계층이 DAL을 참조해 로직 테스트에 DB가 필요해지기 쉽다
- **공용 라이브러리 하나**: 순수 로직과 입출력이 섞인다
- **네임스페이스로만 분리**: IDE에서 경계를 쉽게 넘는다
- **전송 형식을 별도 프로젝트로**: 일반 클래스라면 Core에 둬도 입출력 의존이 생기지 않는다

## 결과

- 프로젝트가 9개로 늘어 초기 설정이 필요하다
- 설계 경계 위반이 리뷰가 아니라 빌드에서 드러난다
