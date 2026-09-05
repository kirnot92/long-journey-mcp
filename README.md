# Long Journey MCP

Long Journey MCP는 여러 에이전트와 세션이 경험을 저장하고 다시 찾을 수 있는 로컬 기억 서버입니다.
C#/.NET 10으로 구현했으며, MCP 클라이언트들이 하나의 기억 저장소를 공유합니다.
원문에서 얻은 관찰과 여러 경험을 함께 보며 도출한 기억을 쌓고, 각 기억의 근거를 원문까지 추적합니다.

저장된 기억은 이후 판단에 참고하는 자료입니다.
어떤 경험에서 나온 생각인지, 이후 어떤 경험이 이를 지지하거나 반박했는지 확인할 수 있도록 원문과 생성 경로를 보존합니다.

## 기억이 쌓이는 방식

입력한 원문은 **Source**라는 단위로 Markdown 파일에 보존합니다.
원문에서 직접 확인할 수 있는 내용은 **관찰**로 추출해 depth 0 기억으로 저장합니다.
관찰할 내용이 없으면 원문만 남길 수 있으며, 하나의 Source에서 생성하는 관찰의 기본 상한은 1개입니다.

Daily Dream과 Weekly Meditation은 기존 기억을 바탕으로 새로운 기억을 만듭니다.
모델이 생성 내용을 제안하면 Core가 부모와 근거 조건을 검증한 뒤 저장합니다.
원문, 기억 본문, 부모 연결인 `derived_from`은 생성 후 변경하지 않습니다.
기억과 단방향 관계, 생성 경로인 provenance는 SQLite에 저장합니다.

더 높은 depth의 기억은 바로 아래 depth의 부모를 최소 `B`개 가져야 하며,
원문까지 따라갔을 때 서로 다른 Source가 최소 `B^depth`개 있어야 합니다. 기본 `B`는 3입니다.
같은 Source에서 나온 관찰이 여러 개여도 근거는 하나로 셉니다.
Depth는 기억의 생성 계층을 나타내며, 정확성이나 신뢰도를 보증하는 점수로 사용하지 않습니다.

## 세 가지 도구

| 도구 | 역할 |
| --- | --- |
| `remember(raw)` | 기억할 경험의 원문을 보존하고 관찰을 추출합니다. |
| `recall(query, context?)` | 질문과 선택적 맥락에 맞는 기억을 찾아 반환합니다. |
| `trace(memory_id)` | 기억의 부모 연결을 따라 원문 Source까지 추적합니다. |

`remember`에는 호출 에이전트가 기억할 가치가 있다고 선택한 하나의 일관된 경험을,
그 경험을 이해하는 데 필요한 맥락과 함께 전달합니다. 발언자나 프로젝트 정보를 필수 인자로 요구하지 않습니다.
입력 상한은 기본 4,000 UTF-16 문자이며, 상한을 넘으면 입력 오류를 반환합니다.
동일한 원문을 다시 보내면 새 Source를 만들지 않습니다. 완료된 입력은 API 호출 없이 기존 결과를 반환하고,
실패한 입력은 보존된 Source로 다시 처리할 수 있습니다.

`recall`은 단어 검색과 embedding 검색의 후보를 결합한 뒤 모델이 선택한 기억을 기본 최대 10개 반환합니다.
호출 에이전트는 반환된 기억을 현재 맥락과 함께 판단해 답변에 사용합니다.
회수 시각은 다음 Dream의 입력으로 활용하지만, 반복해서 회수한 기억의 근거 수나 검색 순위를 높이지 않습니다.
`trace`는 부모 provenance를 추적합니다. Positive/negative 관계는 저장된 소유자에서 대상 방향으로만 사용합니다.
입력·관찰·반환 개수의 상한은 [설정](docs/05-operations.md)으로 변경할 수 있습니다.

## 빠른 시작

[global.json](global.json)은 .NET SDK `10.0.400`을 지정하며 같은 기능 대역의 최신 패치를 허용합니다(`latestPatch`).
기억 추출·검색·자동 정리에는 OpenAI API 키와 설정된 모델의 API 접근 권한이 필요합니다.
저장소 루트의 `key.txt`에 키 한 개를 넣거나 `OPENAI_API_KEY` 환경변수를 설정합니다. 환경변수가 우선합니다.

저장소 루트의 PowerShell에서 실행합니다.

```powershell
dotnet restore LongJourney.slnx
dotnet build LongJourney.slnx
dotnet run --project src/LongJourney.Server -- --Engine:DataDirectory=D:/LongJourneyData
```

MCP 클라이언트의 Streamable HTTP 서버 주소를 `http://127.0.0.1:5088/mcp`로 설정합니다.
서버는 IPv4 loopback에서 수신하며, 연결된 모든 클라이언트가 사용자·프로젝트 구분 없이 같은 기억 저장소를 사용합니다.
같은 데이터 폴더에는 서버 하나만 실행할 수 있습니다. 포트는 `Server:Port`로 변경합니다.

읽기 전용 화면은 [Inspection](http://127.0.0.1:5088/inspect)입니다.
기억 검색·상세, Source 원문, provenance, 관계, Dream/Meditation의 실행·제안·비용을 확인합니다.
화면 조회는 OpenAI API를 호출하거나 기억·recall·작업 상태를 바꾸지 않습니다.
API 키가 없어도 서버를 시작하고 저장된 데이터를 `trace`와 Inspection으로 읽을 수 있습니다.

백그라운드 처리 없이 살펴보려면 실행 인자에 `--no-scheduler`를 추가합니다.
이 옵션은 미완료 Source의 백그라운드 재시도와 Dream/Meditation을 끕니다. 서버 시작 시 저장소 복구는 유지합니다.
화면별 사용법과 조회 범위는 [Inspection 안내](docs/09-inspection.md)에 정리되어 있습니다.

## 자동 작업과 비용

스케줄러는 기본 서울 시간(`Asia/Seoul`)을 기준으로 종료된 날짜를 처리합니다.
서버가 중단되면 작업도 멈추며, 다시 시작하면 저장된 진행 상태에서 놓친 날짜를 순서대로 처리합니다.
API 실패 시 원문과 작업 상태를 보존하고, 저장된 제안과 완료 결과를 재사용해 재개합니다.

| 작업 | 처리 대상 | 비용 한도 |
| --- | --- | --- |
| Daily Dream | 해당 날짜에 생성된 depth 0 기억과 해당 날짜에 회수된 모든 depth의 기억에서 시작합니다. | 일일 금액 한도가 없습니다. |
| Weekly Meditation | 7일 구간에 생성되거나 outgoing 관계가 추가된 depth 1 이상 기억을 검토합니다. | 실행별 USD 예산을 사용자가 설정합니다. |

Dream은 여러 부모를 함께 볼 때 드러나는 패턴·조건·차이·예외를 새로운 기억으로 남깁니다.
한 번의 Dream에서 하나의 이웃 기억 집합으로 만드는 추상 기억은 최대 1개이며, 새롭게 남길 내용이 없으면 만들지 않습니다.
Meditation은 더 넓은 기억과 원문을 검토하며, 처리 순서를 모델이 판단합니다.
주간 구간은 최초 저장소 활동 날짜부터 7일씩 이어집니다.

**`Engine:MeditationBudgetUsd`를 설정하기 전에는 Meditation이 보류됩니다.**
명령행의 `--Engine:MeditationBudgetUsd=<실행별 USD 예산>` 또는 환경변수 `Engine__MeditationBudgetUsd`에
사용자가 정한 양수를 지정합니다. 예산을 나중에 설정하면 밀린 주간 구간마다 별도 예산으로 실행할 수 있습니다.
예산이 부족해 남은 작업은 다음 주간 실행으로 이월합니다.

Remember·Recall·Dream에도 API 비용이 발생합니다.
호출 전 최대 예상 비용을 예약하고 응답의 token 사용량으로 정산하며, 사용량이 불명확한 호출은 예약액을 유지합니다.
표시 비용은 설정 단가에 기반한 로컬 장부입니다. 예산·재시도·백업의 상세 동작은 [운영 안내](docs/05-operations.md)를 참고합니다.

현재 기본 모델은 [Options.cs](src/LongJourney.Core/Options.cs)에 정의되어 있습니다.
운영 중 의미 처리는 OpenAI Responses API, embedding은 OpenAI Embeddings API를 사용합니다.

| 용도 | 기본 모델 | 추론 수준 또는 차원 |
| --- | --- | --- |
| Remember | `gpt-5.6-terra` | `low` |
| Recall | `gpt-5.6-terra` | `medium` |
| Dream | `gpt-5.6-terra` | `high` |
| Meditation | `gpt-5.6-sol` | `high` |
| Embedding | `text-embedding-3-large` | 3,072차원 |

## 코드 구조와 검증

| 경로 | 역할 |
| --- | --- |
| `src/LongJourney.Core` | 기억 모델, 원문·SQLite 저장, 검색, 근거 검증, 자동 작업을 구현합니다. |
| `src/LongJourney.OpenAI` | OpenAI 호출, 구조화 출력, 인증, 비용 계산을 담당합니다. |
| `src/LongJourney.Server` | MCP HTTP 서버, 백그라운드 실행, Inspection 화면을 제공합니다. |
| `src/LongJourney.Benchmarks` | 데이터 재생, 조건별 평가, 비용·결과 보고서를 구현합니다. |
| `tests/LongJourney.Tests` | 저장·복구·근거·비용·HTTP·실험 절차의 동작을 검증합니다. |

```powershell
dotnet test LongJourney.slnx
```

자동 테스트는 실제 SQLite와 로컬 HTTP 서버, 가짜 cognition 및 OpenAI HTTP 응답을 사용합니다.
실제 계정의 모델 접근 권한과 기억 품질은 별도의 API 실행과 평가로 확인해야 합니다.

[LongMemEval-S consolidation benchmark](docs/10-benchmark.md)는 Remember Only와 Full Long Journey를 비교하는 실험입니다.
공통 관찰을 두 조건에 공유하고 검색 결과, provenance, 답변, 비용을 기록합니다.
실험 구현과 실행 결과를 구분하며, 성능에 관한 판단은 완료된 결과와 해당 지표의 의미를 함께 확인해야 합니다.

## 문서 안내

현재 사용과 개발에는 [운영 안내](docs/05-operations.md), [Inspection 안내](docs/09-inspection.md),
[보완 설계](docs/02-design-supplement.md), [코드 작성 기준](docs/06-code-style.md)을 참고합니다.
실험 조건과 실행 방법은 [벤치마크 안내](docs/10-benchmark.md)에 정리되어 있습니다.

[최초 설계](docs/01-initial-design.md), [초기 구현 계획](docs/03-implementation-plan.md),
[OpenAI API 사용 확정 원문](docs/04-openai-api.md)은 설계와 결정의 배경을 보존한 문서입니다.
이후 변경된 구현 범위·설정·운영 방식은 현재 코드와 운영 안내를 기준으로 확인합니다.
