# Phase 1~3 운영 안내

## 서버와 데이터

서버는 .NET 10, 공식 MCP C# SDK의 Streamable HTTP transport를 사용한다. 기본 주소는 `http://127.0.0.1:5088/mcp`이며 IPv4 loopback만 수신한다. 포트는 `Server:Port`로 변경한다. 사용자별·프로젝트별 partition 없이 연결된 모든 MCP 클라이언트가 같은 corpus를 사용한다.

서버 하나를 상시 실행한다. 프로세스가 종료되면 예약 작업도 멈추며, 다음 시작 때 저장된 진행 상태에서 재개한다. Windows 서비스나 작업 스케줄러에 자동 등록하는 설치 기능은 이번 범위에 없다.

`Engine:DataDirectory`에는 `memory.db`, immutable `sources/*.md`, `.server.lock`이 저장된다. 상대 경로는 서버 content root를 기준으로 해석하므로 실제 운영에서는 절대 경로를 권장한다. SQLite를 열거나 복구하기 전에 OS 파일 잠금을 얻으며 같은 corpus의 두 번째 서버 실행은 실패한다. 잠금 파일 자체는 종료 후 남을 수 있고 프로세스 종료 시 OS 잠금은 해제된다.

Host는 `localhost`, `127.0.0.1`, `[::1]`만 허용하고, Origin 헤더가 있으면 요청 서버와 scheme·host·port가 일치해야 한다. 원격 공개·인증 서버 운영은 구현하지 않았다. CLI `--urls`나 `ASPNETCORE_URLS`로 외부 인터페이스를 여는 방식은 지원하지 않으며, 사용자 지정 `Kestrel:Endpoints`가 있으면 시작을 거부한다.

## 설정

기본값은 Core의 `EngineOptions`, `OpenAiOptions`에 있고 서버 `appsettings.json`, 환경변수, 명령행으로 바꿀 수 있다. 환경변수의 중첩 구분자는 `__`, 명령행은 `:`이다. API key는 **`OPENAI_API_KEY` 환경변수만** 사용한다. 아래 예시는 저장소 루트에서 실행한다.

```powershell
dotnet run --project src/LongJourney.Server -- --Server:Port=5088 --Engine:DataDirectory=D:/LongJourneyData
```

기본 OpenAI 설정에 대응하는 JSON은 다음과 같다. 역할별 옵션은 객체이며 API 확정 문서의 평면 예시와 같은 값으로 구성된다.

```json
{
  "OpenAI": {
    "Remember": { "Model": "gpt-5.6-terra", "ReasoningEffort": "low", "MaxOutputTokens": 4096 },
    "Recall": { "Model": "gpt-5.6-terra", "ReasoningEffort": "medium", "MaxOutputTokens": 4096 },
    "Dream": { "Model": "gpt-5.6-terra", "ReasoningEffort": "high", "MaxOutputTokens": 8192 },
    "Meditation": { "Model": "gpt-5.6-sol", "ReasoningEffort": "high", "MaxOutputTokens": 16384 },
    "EmbeddingModel": "text-embedding-3-large",
    "EmbeddingDimensions": 3072,
    "TimeoutSeconds": 300
  },
  "Engine": {
    "DataDirectory": "data",
    "TimeZoneId": "Asia/Seoul",
    "RootBase": 3,
    "MaxRawCharacters": 4000,
    "MaxObservations": 1,
    "MaxMemoryCharacters": 4000,
    "SearchCandidates": 30,
    "RecallLimit": 10,
    "NeighborhoodSize": 20,
    "MeditationGraphLimit": 80,
    "MeditationSourceLimit": 12,
    "MeditationBudgetUsd": null,
    "SchedulerEnabled": true,
    "SchedulerPollSeconds": 60
  }
}
```

문자·개수 제한과 탐색 크기는 변경 가능한 구현 기본값이다. 상한을 초과한 raw를 임의로 자르지 않고 입력 오류를 반환한다. 1개를 기본으로 하는 observation 상한도 설정값이며, 고정된 Core invariant로 새로 도입한 것은 아니다. Source 하나는 observation 개수와 관계없이 root 하나로 센다.

Core invariant는 모든 생성 경로에서 강제한다. 같은 바로 아래 depth의 부모가 최소 B개 있어야 하고, 서로 다른 Source root의 합집합이 `B^depth` 이상이어야 한다. `RootBase`는 신규 corpus에서 선택하며 기존 corpus의 값을 설정만 바꿔 변경할 수 없다. 원문·content·부모 provenance는 생성 뒤 수정하지 않는다.

## MCP 도구와 결과

공개 도구는 정확히 다음 세 개다. 반환 객체는 JSON `snake_case` 이름의 structured content로 제공된다.

| 도구 | 입력 | 반환 |
| --- | --- | --- |
| `remember` | `raw` | `source_id`, `duplicate`, `memories`, `status` |
| `recall` | `query`, 선택적 `context` | `memories` |
| `trace` | `memory_id` | `memory_id`, 부모를 포함한 `memories`, 원문이 포함된 `sources` |

Source 생성 시각은 내부에서 기록한다. `remember`에 발언자·프로젝트·세션 필수 인자는 없다. 동일 raw는 문자열 완전 일치 기준이고 공백·대소문자 차이는 별개 입력이다. 중복 완료 입력은 기존 ID와 기억을 반환한다. 처리 중이면 기존 Source의 상태를 반환하며, 실패한 입력은 같은 Source로 다시 처리할 수 있다.

각 기억의 `relations`에는 `related_memory_id`, `kind`, `related_at`, `sequence`가 있다. `positive_related`/`negative_related`는 outgoing ID 목록이다. A→B 관계를 추가해도 B→A를 만들거나 조회하지 않는다. 동일 방향에서 positive와 negative는 별도로 존재할 수 있다. 재발견은 기존 `related_at`을 갱신하지 않는다. `trace`는 immutable `derived_from`으로 부모와 원문만 추적한다.

Recall은 lexical/embedding 후보를 결합하고 Responses API로 후보 중 ID를 선택한다. Recall 기록은 다음 Dream의 seed에 활용하지만 별도의 증거·truth·confidence·retrieval boost로 변환하지 않는다.

## 일일·주간 작업

스케줄러는 기본 60초 간격으로 확인하며, 현재 진행 중인 날짜가 아니라 **종료된 로컬 날짜**를 `[start, end)` 구간으로 처리한다. 예를 들어 서울 시각 9월 5일 첫 poll은 9월 4일 구간을 처리한다. 실행 시각의 기준 timezone은 corpus scheduler 상태에 저장된다. 기존 corpus의 timezone은 상태 이관 없이 바꿀 수 없다.

Daily Dream은 해당 날짜에 생성된 모든 depth 0 기억과 해당 날짜에 recall된 모든 depth의 기억의 합집합에서 시작한다. seed의 outgoing relation과 semantic 이웃까지 탐색할 수 있다. 생성만 된 depth > 0은 그 이유만으로 seed가 되지 않는다. 새 observation의 assimilation과 seed neighborhood의 consolidation을 처리하며 일일 금액 제한은 없다.

주간 구간은 최초 corpus 활동 날짜를 기준으로 7일씩 이어진다. 특정 요일에 고정한 달력 주가 아니며, 각 일일 구간 처리가 끝난 뒤 그에 대응하는 7일 구간이 완성되면 Meditation을 실행한다. 서버가 며칠 꺼졌다면 놓친 날짜와 주간 구간을 차례로 따라잡는다. 미설정 budget 때문에 주간 구간을 버리거나 완료로 표시하지 않는다. 이때 budget을 나중에 설정하면 밀린 여러 주가 각각 N달러의 별도 budget으로 실행될 수 있다.

Meditation은 구간 안에서 생성된 depth >= 1 기억과, 그 기간에 outgoing relation이 추가된 depth >= 1 기억을 수집한다. Relation target이 depth 0이어도 owner가 depth >= 1이면 포함한다. 새 negative relation 수, 전체 negative relation 수, 최근 변경 시각 순으로 우선 처리한다. 이 우선순위는 작업 순서일 뿐 기억의 영구적인 importance 필드가 아니다. 필요하면 더 넓은 그래프, depth 0, raw Source를 확인한다.

금액 N은 `Engine:MeditationBudgetUsd`로 설정한다. 미설정 시 서버가 안내 로그를 남기고 주간 작업을 보류한다. budget에 걸려 남은 작업은 이후 주간 실행으로 이월한다. 이월 작업의 원래 snapshot/proposal은 유지하며 새 주의 budget을 사용한다.

각 Dream/Meditation은 시작 시 graph sequence 상한과 revision을 고정한다. 실행 중 생성된 기억·relation·recall을 같은 실행의 재료로 재사용하지 않는다. 반환된 proposal은 저장한 뒤 Core validation을 통해 적용한다. 중단 후 재시도는 저장된 proposal과 작업 키로 중복 적용을 방지한다. 서로 다른 run이 생성한 의미상 유사 abstraction은 자동 병합하지 않는다.

## API 비용과 오류

모든 운영 reasoning은 OpenAI Responses API, embedding은 OpenAI Embeddings API를 직접 사용한다. 기본 endpoint는 `https://api.openai.com/v1/`이며 타 provider endpoint를 받지 않는다. 구조화 출력은 JSON Schema를 사용하고, 모델은 DB 대신 proposal만 반환한다. Responses 요청은 `service_tier=default`를 지정해 standard 단가 설정과 맞추며 계정의 fast tier를 자동 상속하지 않는다.

호출 전에 보수적인 최대 비용을 예약하고 응답 token usage로 정산한다. 주간 예약은 남은 budget 안에서만 허용한다. 최대 출력·입력 추정 비용을 모두 예약할 여유가 없으면 실제 잔액이 남았더라도 다음 호출을 시작하지 않을 수 있다. 완료 여부나 usage가 불명확한 호출은 예약을 남겨 이중 소비를 방지한다. 일일/remember/recall 비용도 기록하지만 금액 한도를 적용하지 않는다.

각 역할 `ModelOptions`의 `InputUsdPerMillion`, `CachedInputUsdPerMillion`, `CacheWriteUsdPerMillion`, `OutputUsdPerMillion`, `LongContextThresholdTokens`, `LongContextInputMultiplier`, `LongContextOutputMultiplier`와 `OpenAI:EmbeddingInputUsdPerMillion`은 변경 가능하다. cached input·cache write·긴 context 추가 요금을 구분한다. 이는 설정 단가에 기반한 로컬 비용 장부이며 실제 청구 금액의 확정 자료가 아니다. 모델 또는 API 요금 변경 시 해당 값도 함께 검토한다.

Key가 없어도 서버와 로컬 trace는 사용할 수 있지만 cognitive API 호출은 명확한 설정 오류를 반환한다. Background worker는 미완료 Source를 재시도한 뒤 해당 날짜의 Dream/Meditation으로 진행한다. 실패한 Source는 재시도 대상으로 남기고 다른 입력과 이미 생성된 기억의 consolidation은 계속 처리한다. API 실패는 저장된 Source와 작업 상태를 보존하며 다음 poll에서 다시 시도한다. `--no-scheduler` 또는 `Engine:SchedulerEnabled=false`는 background source recovery와 두 스케줄을 모두 끈다.

운영 로그는 stderr에 기록하며 raw·기억 본문·API key를 남기지 않는다. 오류 로그는 오류 종류와 작업 상태를 제공한다. OpenAI 계정의 모델 접근 권한, `OPENAI_API_KEY`, N달러 설정을 준비한 실제 API 검증은 별도로 필요하다. 현재 자동 테스트는 실제 SQLite와 HTTP MCP 연결, 가짜 cognition 및 가짜 OpenAI HTTP 응답을 사용한다.

## Embedding 교체와 백업

Embedding은 모델 ID와 차원으로 space를 구분해 저장한다. 설정을 바꾸면 새 space로 검색하며 빠진 embedding은 필요 시 생성한다. 원문과 기존 Memory는 그대로 두고 미리 전체 재색인하려면 서버를 종료한 후 같은 데이터 폴더로 아래 내부 명령을 실행한다. 이 명령은 미완료 Source도 먼저 재시도한다. 실제 API 비용이 발생하며 네 번째 MCP 도구로 노출하지 않는다.

```powershell
dotnet run --project src/LongJourney.Server -- --reindex --Engine:DataDirectory=D:/LongJourneyData
```

백업은 서버를 종료한 뒤 데이터 폴더 전체를 복사한다. SQLite 파일과 `sources`를 함께 보존해야 provenance를 복구할 수 있다. 원본 파일이나 DB를 손으로 수정하는 방식은 지원하지 않는다.
