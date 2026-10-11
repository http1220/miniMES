# Modbus 재연결 대상 분류 학습 기록

- 날짜: 2026-10-11 (KST)
- 요구사항: COL-05, D-048, 사용자 제시 설계 `../../work/03-detailed-design.md` §1.2
- 유형: `../../src/Core/ModbusFailureType.cs`
- 분류 함수: `../../src/Core/ModbusRetryPolicy.cs`
- 테스트: `ModbusRetryPolicyTests.cs`

## 1. 목표와 범위

Modbus 실패 유형 하나를 받아 재연결 재시도 대상인지 bool로 반환한다.
실패 이력이나 시간은 기억하지 않는 순수 분류 함수다.
Server 응답별 재전송 정책과 실제 재연결 실행은 이번 범위에 포함하지 않았다.

| 실패 유형 | 재연결 대상 |
|---|---|
| ConnectionFailed | true |
| ConnectionLost | true |
| Timeout | false |
| DeviceException | false |
| InvalidValue | false |

처음 제시된 네 테스트 유형에 연결을 처음 맺지 못하는 경우를 명시적으로 추가해 총 다섯 유형으로 정리했다.

## 2. 처음 작성한 형태와 피드백

Core 네임스페이스에 public enum ModbusFailureType을 작성했다.
그다음 ModbusRetryPolicy에 public static bool ShouldReconnect를 선언했다.
처음 메서드 본문은 비어 있었지만 bool 반환형은 결과 반환이 필요하다는 피드백을 받았다.
임시로 false를 반환하도록 하여 실행 가능한 테스트 준비 상태를 만들었다.

이름은 일반적인 재시도 여부보다 재연결을 의미하는 ShouldReconnect로 정했다.
타임아웃 요청 재시도나 Server 전송 재시도와 구분하기 위해서다.

## 3. 테스트 먼저 작성하고 실패 확인

Theory 하나에 InlineData 다섯 사례를 넣었다.
테스트 메서드는 ModbusFailureType failureType과 bool expected를 받는다.
Assert.Equal로 기대 결과와 ShouldReconnect의 실제 반환값을 비교했다.

임시 구현이 항상 false이므로 ConnectionFailed와 ConnectionLost 두 사례가 실패했다.
나머지 세 사례는 false가 기대값이므로 통과했다.
이 실패로 실제 분류 조건이 필요한 두 경우를 확인했다.

## 4. 최소 구현과 결과

failureType이 ConnectionFailed와 같은지, 또는 ConnectionLost와 같은지를 비교했다.
동등 비교 두 개를 ||로 연결한 bool 결과를 그대로 반환했다.
불필요한 상태나 객체 생성 없이 전달받은 유형만으로 판단한다.
사용자가 다섯 사례 모두 통과했다고 확인했다.
문서 작성 시 테스트를 별도로 실행하지 않았다.

## 5. 역할 분리와 남은 범위

| 구성 요소 | 역할 |
|---|---|
| ModbusRetryPolicy | 재연결 대상 여부 판단 |
| ConnectionBackoff | 실패 이력에 따라 기본 대기시간 계산 |
| DelayJitter | 전달받은 비율로 대기시간에 지터 적용 |
| 수집기 | 실제 실패 분류, 재연결 실행, 성공·실패 결과 전달 |

분류 함수가 true를 반환해도 실제 연결이 수행되는 것은 아니다.
실제 예외를 ModbusFailureType으로 매핑하는 작업과 재연결 루프 연결은 별도 구현 대상이다.
Server 응답별 재시도 정책도 별도로 설계하고 테스트해야 한다.
