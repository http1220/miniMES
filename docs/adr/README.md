# 아키텍처 결정 기록 (ADR)

이 프로젝트에서 내린 설계 결정과 그 이유를 기록한다. 형식은 맥락 / 결정 / 검토한 대안 / 결과를 따른다.

| 번호 | 제목 | 상태 |
|---|---|---|
| [0001](0001-out-of-scope.md) | 범위 밖 항목과 이유 | 채택 |
| [0002](0002-platform.md) | .NET Framework 4.8 + WinForms + C# 7.3 | 채택 |
| [0003](0003-three-processes-thin-server.md) | 프로세스 3분리와 얇은 Server | 채택 |
| [0004](0004-project-structure.md) | 프로젝트 구성과 참조 규칙 | 채택 |
| [0005](0005-single-time-source.md) | 시각의 단일 원천과 기준점 방식 | 채택 |
| [0006](0006-store-failure-handling.md) | 저장 실패 처리: 수집기 버퍼, 끝까지 확인, 알람 큐 분리 | 채택 |
| [0007](0007-http-transport.md) | HTTP 통신과 MQTT 전환 조건 | 채택 |
| [0008](0008-uptime-gap-calculation.md) | 가동·공백 구간을 저장하지 않고 계산 | 채택 |
| [0009](0009-alarm-state-in-db.md) | 알람 상태의 진실은 DB | 채택 |
| [0010](0010-authentication-scope.md) | 인증 범위 | 채택 |
| [0011](0011-test-strategy.md) | 테스트 전략 | 채택 |
| [0012](0012-data-retention.md) | 데이터 보관 정책은 향후 과제 | 채택 |
| [0013](0013-network-verification-limit.md) | 검증 한계: 실제 다른 네트워크 미검증 | 채택 |
| [0014](0014-system-availability.md) | 설비 연계: 시스템 구조와 시스템 가용도 | 채택 |
| [0015](0015-collector-concurrency.md) | 수집기 동시성: 역할별 비동기 루프 | 채택 |
