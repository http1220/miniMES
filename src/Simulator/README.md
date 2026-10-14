# Simulator

미니 MES의 로컬 통합 테스트용 Modbus TCP 설비 5대입니다. .NET Framework 4.8과 FluentModbus 5.3.2를 사용하며 다른 프로젝트를 참조하지 않습니다.

## 빌드·실행

Windows와 .NET Framework 4.8 개발 도구가 필요합니다. 저장소 루트에서 실행합니다.

```powershell
dotnet build src/Simulator/Simulator.csproj --configuration Debug
& ./src/Simulator/bin/Debug/net48/Simulator.exe
```

5개 포트가 모두 준비되면 표준출력에 `READY`가 출력됩니다. 콘솔에 명령을 입력하거나, 통합 테스트에서 프로세스의 표준입출력을 리디렉션해 사용합니다. 별도 명령행 인자는 없습니다.

종료는 `quit`, 표준입력 EOF 또는 Ctrl+C입니다. 입력을 리디렉션한 테스트는 실행 중 표준입력을 열어 두어야 합니다. 정상 종료 코드는 `0`, 설정·시작 오류는 `1`이며 오류 설명은 표준오류로 출력됩니다. 시작 중 포트 확보에 실패하면 이미 시작한 설비도 정리합니다.

## 설정과 레지스터

`App.config`의 `appSettings`를 `ConfigurationManager`로 읽습니다. 빌드 결과의 `Simulator.exe.config`를 수정하면 다음 프로세스 시작부터 반영됩니다. 키 누락, 잘못된 숫자, 중복 포트, 잘못된 워드 순서는 시작 전에 거부합니다. 포트는 `1~65535`, 측정값은 유한한 float32이며 소수점은 `.`입니다.

| 설비 ID | 기본 포트 | 시작 주소 S | 워드 순서 |
|---|---:|---:|---|
| 1 | 15021 | 0 | ABCD |
| 2 | 15022 | 100 | CDAB |
| 3 | 15023 | 200 | ABCD |
| 4 | 15024 | 300 | CDAB |
| 5 | 15025 | 400 | ABCD |

`ListenAddress` 기본값은 `127.0.0.1`, 모든 설비의 Unit ID는 `1`입니다. 다른 Unit ID는 응답하지 않습니다. 설비 설정 키는 `Equipment.<id>.Port`, `StartAddress`, `WordOrder`, `NormalTemperature`, `NormalVibration`, `HighTemperature`, `HighVibration`입니다.

주소는 **0 기반 프로토콜 주소**입니다. `40001` 형식의 표시 주소가 아닙니다.

| 주소 | 형식 | 값 |
|---|---|---|
| S, S+1 | float32 | 온도 |
| S+2, S+3 | float32 | 진동 |
| S+4 | uint16 | 상태: 0 정지, 1 가동, 2 이상, 나머지 무효 |

float의 big-endian 바이트를 A/B/C/D라고 할 때 ABCD는 `[AB][CD]`, CDAB는 `[CD][AB]`입니다. 각 레지스터 내부의 바이트 순서는 항상 big-endian입니다.

시작 주소는 **0~65530**만 허용합니다. FluentModbus 5.3.2의 범위 검사에서 주소 65535를 포함하는 읽기를 거부하기 때문에 `65531`은 설정 오류로 처리합니다. 패키지를 변경하거나 라이브러리 내부 상태를 수정하지 않습니다.

정상값 기본값은 온도 `25`, 진동 `1`, 초과값은 `100`, `20`입니다. 초기 상태는 `1`이고 모든 장애는 꺼져 있습니다. 시간에 따른 난수 변화 없이 고정값을 제공하므로 재현 가능한 검증에 사용할 수 있습니다. 초과값은 수집기의 **임계값보다 크고 유효 범위 안에 있도록** 설정해야 합니다. Simulator는 수집기 임계값을 알거나 알람을 판정하지 않습니다.

## 제어 명령

명령은 대소문자를 구분하며 설비 ID는 `1~5`입니다. 한 줄당 명령 하나를 보내고 응답 한 줄을 받은 다음 다음 명령을 보냅니다.

| 명령 | 동작 |
|---|---|
| `delay <id> <ms>` | 0~2147483647ms 지연. 0은 해제 |
| `disconnect <id> on/off` | 기존 연결 종료 및 수신 중단 / 같은 포트로 복구 |
| `exception <id> on/off` | 정상 주소 읽기에 예외 04 주입 / 해제 |
| `high <id> on/off` | 초과값 쌍 / 정상값 쌍 선택 |
| `values <id> normal/high <온도> <진동>` | 값 쌍 변경. 현재 선택된 쌍은 유지 |
| `status <id> <0~65535>` | 상태 설정. 3 이상도 변환 없이 전달 |
| `show <id>` | 포트·주소·워드 순서·현재 장애·상태·두 값 쌍 조회 |
| `quit` | 모든 설비 정리 후 종료 |

표의 `on/off`, `normal/high`는 둘 중 하나를 입력한다는 뜻입니다.

```text
high 2 on
values 2 high 120 25
status 2 3
delay 1 3000
show 1
disconnect 1 on
delay 1 0
disconnect 1 off
exception 3 on
exception 3 off
quit
```

성공 응답은 `OK`, 조회는 `OK id=... address=... ...`, 실패는 `ERR <이유>`입니다. `show`의 bool은 `True/False`이고 숫자는 invariant culture 형식입니다. 명령 문법 오류는 상태를 바꾸지 않습니다. 재연결 시 포트 충돌은 `ERR`로 응답하며 해당 설비는 끊긴 상태를 유지합니다.

값·상태·장애 플래그는 요청 검증 시 하나의 스냅샷으로 읽습니다. `OK` 이후 새로 처리되는 요청에 변경값을 적용하며, 이미 지연 중인 요청의 스냅샷이나 대기 시간은 바꾸지 않습니다. `disconnect on`과 종료는 진행 중인 지연도 취소합니다. 연결을 복구해도 상태·값·다른 장애 플래그는 보존됩니다.

FC03은 맵 내부의 부분 읽기를 허용합니다. 수량 `0` 또는 `126` 이상은 예외 `03`, 유효한 수량의 맵 밖 요청은 `02`이며 주입한 `04`보다 우선합니다. 정상 범위의 요청에는 설정된 지연 후 정상값 또는 예외 `04`를 반환합니다. 다른 함수 코드는 `01`로 거부합니다.

## 연결 처리

각 설비는 외부 TCP 포트와 독립적인 요청 처리 경로를 갖습니다. 같은 설비의 FC03 요청은 순서대로 처리되므로 여러 클라이언트가 동시에 요청하면 해당 설비의 지연 뒤에 대기할 수 있습니다. 다른 설비의 응답과 표준입력 제어는 계속 진행됩니다.

`ReadOnlyEndpoint`는 외부 연결의 프레임 경계를 복원하고 미지원 함수의 예외 응답을 만듭니다. FC03의 주소 검증과 응답 생성은 `RequestValidator`를 사용하는 FluentModbus에 위임합니다. 내부에는 설비당 loopback 소켓 쌍 하나가 있으며 이를 통해 외부 연결 목록과 라이브러리의 연결 관리를 분리합니다. 소켓 쌍 생성용 임시 포트는 생성 직후 수신을 중단합니다.

이 구성은 FluentModbus 5.3.2에서 여러 연결 종료 시 발생하는 연결 목록 변경 경쟁을 피하고, 분할·연속 수신된 외부 요청을 처리합니다. 내부 라이브러리 코드나 버전은 변경하지 않습니다. 잘못된 MBAP 길이·프로토콜 ID는 연결을 닫고, 길이가 잘못된 FC03 PDU는 `03`으로 응답합니다.

## 자동 검증

```powershell
dotnet build src/Simulator/Simulator.csproj --configuration Debug
powershell -NoProfile -ExecutionPolicy Bypass -File src/Simulator/tests/Verify-Simulator.ps1
```

`ExecutionPolicy Bypass`는 해당 검증 프로세스에만 적용합니다. 스크립트는 빌드 결과를 `obj/verification/<고유 ID>`에 복사하고 사용 가능한 포트와 테스트 설정으로 실행합니다. 원본 설정은 변경하지 않으며 추가 패키지는 필요 없습니다. 테스트 복사본은 실패 분석을 위해 남깁니다.

검증 항목은 5대 동시 읽기, 정확한 워드·바이트 순서, 부분 읽기·주소 경계·수량·미지원 함수, 분할·연속 프레임, 상태·값 변경, 잘못된 명령의 원자성, 장애 조합과 설비 격리, 재연결, 포트 충돌, 설정 오류, 지연 중 종료, EOF·Ctrl+C 및 포트 해제입니다. Ctrl+C 검증만 실제 콘솔이 필요하므로 숨겨진 콘솔과 별도 신호 전송 프로세스를 사용합니다.

테스트는 TCP 프레임을 직접 전송해 결과를 검사하며 Core·Modbus·기존 테스트 프로젝트를 참조하거나 수정하지 않습니다. 이 검증은 향후 수집기와의 end-to-end 통합 테스트를 대체하지 않습니다.
