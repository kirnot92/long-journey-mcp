# 실사용 Daily Report

Daily Report는 에이전트의 Remember 입력이 observation, Recall·Think, Assimilation 작업과 비용으로 이어지는 과정을 기록한다. SQLite 기록을 읽어 Markdown 요약과 상세 JSON을 생성하며, 보고서 생성 자체는 모델을 호출하지 않는다.

## 사용

서버는 기본적으로 종료된 날짜의 보고서를 자동 생성한다. 위치는 `Engine:DataDirectory` 아래다.

```text
reports/daily/2026-09-05.md
reports/daily/2026-09-05.json
```

자동 보고는 Dream/Meditation과 독립적으로 동작한다. `--no-scheduler`로 인지 작업을 끈 상태에서도 보고 작업은 실행된다. 자동 파일 생성을 끄려면 `--Engine:DailyReportsEnabled=false`를 지정한다. 이 설정은 분석용 호출 기록을 끄지 않는다.

| 설정 | 기본값 | 의미 |
| --- | --- | --- |
| `Engine:DailyReportsEnabled` | `true` | 종료된 날짜의 보고서를 자동으로 저장한다. |
| `Engine:DailyReportPollSeconds` | `60` | 변경 및 날짜 전환을 확인하는 간격이다. |
| `Engine:TimeZoneId` | `Asia/Seoul` | 일별 집계에 사용하는 시간대다. |

저장된 데이터로 특정 날짜 또는 기간의 보고서를 다시 만들 수 있다. 저장소 루트에서 다음 명령을 실행한다. `D:/LongJourneyData`는 실제 사용 중인 데이터 폴더로 바꾼다.

```powershell
dotnet run --project src/LongJourney.Server -- --daily-report 2026-09-05 --Engine:DataDirectory=D:/LongJourneyData
dotnet run --project src/LongJourney.Server -- --daily-report 2026-09-01..2026-09-05 --Engine:DataDirectory=D:/LongJourneyData
```

이 명령은 HTTP 서버를 열지 않고, Source 복구·Dream·Meditation·reindex를 실행하지 않는다. API 키도 필요하지 않다. 원래 서버와 같은 `Engine:TimeZoneId`를 지정한다. 현재 날짜의 보고서는 잠정 집계다.

## 기록의 의미

### Remember

호출 시각, raw의 UTF-16 문자 수·UTF-8 byte 수, Source 연결, 추출 시도, 새로 생성한 D0와 반환한 D0를 기록한다. 호출 횟수는 `MemoryEngine.RememberAsync` 진입부터 센다. 입력 크기 거절도 포함하지만 MCP 전송·인자 해석 단계에서 실패하여 엔진에 도달하지 못한 요청은 포함하지 않는다.

제출 raw 총량에는 같은 원문의 반복 호출도 포함된다. 신규 Source 총량과 생성 D0 수는 별도다. 재사용한 Source가 아직 추출 중이거나 재시도 대상일 수도 있으므로, 기존 Source라는 사실을 곧바로 성공한 중복 반환으로 해석하지 않는다. 백그라운드 복구의 추출 시도는 에이전트의 Remember 호출 수를 늘리지 않는다.

원문은 기존 Source archive 파일을 참조한다. 수용되지 않은 입력은 Source가 없을 수 있다. 문자·byte 수는 token 수와 다르다.

### Recall / Think

query·context, 당시 후보와 최종 반환 기억의 ID·순서를 저장한다. 반환된 D0의 횟수와 고유 개수를 나눠 보여주고, 높은 depth의 반환도 보존한다. 후보가 없는 경우, 후보는 있지만 반환이 없는 경우, 오류를 구분할 수 있다.

Recall과 Think는 같은 검색·선택 경로를 사용한다. 두 호출 모두 `kind=recall`로 기록하고 기존 `summary.recall`에 합산한다. Markdown의 `Recall / Think` 수치는 이 합계다. 상세 JSON의 `operations[].details.tool`은 `recall` 또는 `think`이며, Think의 `topic`은 `details.query`에, `context`는 null로 기록한다. 호출별 후보·반환 ID를 `memories`의 content·depth와 연결하면 두 도구의 실제 검색어와 반환 깊이를 비교할 수 있다. 이 필드가 없던 과거 기록은 도구 구분 미계측으로 다루며 쿼리 표현으로 추측하지 않는다.

기억 내용·depth는 불변 ID로 연결하지만 현재 관계 목록을 과거 Recall 맥락으로 취급하지 않는다. 기록된 반환은 서버 측 결과이며 에이전트가 답변에 실제 사용했는지를 뜻하지 않는다.

### Assimilation

논리 work와 실행 시도를 구분하고, 대상 D0·후보·모델 제안·저장 proposal 재사용 여부를 기록한다. 관계 제안마다 다음 결과를 남긴다.

| 결과 | 의미 |
| --- | --- |
| `appended` | 이 제안이 새 관계를 추가했다. |
| `already_exists` | 유효한 제안이지만 해당 방향·종류의 관계가 이미 있었다. |
| `rejected` | 제안이 검증을 통과하지 못했다. 사유를 함께 남긴다. |

제안 0개, 아직 결과가 없는 시도, 실패도 구분한다. 같은 proposal을 재개해도 최초 적용 결과가 바뀌지 않는다. 관계는 같은 Source의 D0끼리, 다른 Source의 D0끼리, 추상 기억에서 새 D0로 향하는 연결로 나눠 분석한다. 시도 기록이 없는 work에는 대기 중인 작업과 계측 이전에 실행한 작업이 섞일 수 있으므로, 이를 모두 미실행으로 단정하지 않는다.

`model_invoked`는 cognition 메서드를 시도했다는 뜻이다. 예산 예약 전에 실패할 수 있으므로 실제 API 기록 수와 같다고 가정하지 않는다. API 원장도 호출 전 예약을 포함하며, 정산되지 않은 예약이 반드시 서버에서 처리된 요청임을 보장하지 않는다.

### 비용과 날짜

API 비용은 원장 ID로 활동에 연결한다. 실제 정산액과 미정산 최대 예약액은 분리한다. 모델·추론·가격 설정은 해당 API 예약 시점의 설정을 기록한다. 논리 작업 수, 재시도 수, API 기록 수는 서로 다르다.

호출은 시작한 현지 날짜에 귀속하고 완료 시각을 별도로 남긴다. API 비용은 API 호출 시작일에 귀속하며, 나중에 정산되면 과거 보고서도 갱신한다. Dream 대상 기간과 실제 실행 시각을 함께 남겨 밀린 작업의 비용을 구분한다.

보고서에는 생성 시점과 snapshot 식별자가 있다. Markdown과 JSON의 식별자가 같은지 확인하면 같은 집계인지 알 수 있다. 보고서는 갱신 가능한 파생 파일이며 SQLite와 Source archive가 원본이다.

서버 시작 시 계측 시작점을 기록하므로 호출이 없는 날도 보고할 수 있다. 자동 생성과 수동 재생성은 같은 출력 잠금을 사용한다. 다른 출력 작업과 겹치면 자동 작업은 다음 poll에서 재시도하며, 수동 명령은 출력 오류를 반환할 수 있다. 중단으로 두 파일의 snapshot이 어긋나면 다음 자동 확인 또는 수동 재생성에서 복구한다.

## 사후 분석 시 주의할 해석

- 계측 이전의 Remember 호출 횟수·빈 Recall·최초 관계 적용 결과는 복원할 수 없다. 미계측을 0회로 해석하지 않는다.
- 당일 Recall에는 이전에 만든 기억도 포함된다. 당일 생성 D0 수로 나누어 회수율을 계산하지 않는다.
- 관계 append는 저장 결과다. 관계의 정확성이나 유용성을 자동으로 평가한 점수가 아니다.
- 이 기록은 에이전트가 Remember하지 않은 경험이나 선택 이유 전체를 담지 않는다.
- 분석용 JSON에는 query·context·기억 내용이 포함되며 로컬 데이터 폴더에 저장된다. 일반 운영 콘솔에는 이 내용을 출력하지 않는다.

초기 분석에서는 raw 크기의 분포, Source당 D0, 처리 D0당 Assimilation 시도·비용, 관계 종류별 append를 함께 비교한다. 일부 큰 입력이 작업량을 지배하는지, 같은 Source 내부 연결에 작업이 집중되는지를 관찰한다.
