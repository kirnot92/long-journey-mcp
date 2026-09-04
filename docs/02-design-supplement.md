# Long Journey MCP — 보완 설계

작성일: 2026-09-04

## 1. 문서의 역할과 적용 순서

이 문서는 [최초 설계](01-initial-design.md)를 읽고 사용자와 논의하여 확정한 변경사항을 기록한다. 최초 설계는 전달받은 원문 그대로 보관한다.

두 문서가 충돌하는 경우 이 보완 설계를 우선한다. 여기서 변경하지 않은 최초 설계의 철학과 규칙은 유지한다. 미확정 항목은 확정된 요구사항으로 취급하지 않는다.

현재 작업 범위는 설계 문서 정리다. 이 문서 작성 자체가 구현 착수나 미확정 세부사항에 대한 승인을 뜻하지는 않는다.

## 2. 구현 순서와 현재 범위

개발은 다음 순서로 나누어 진행한다.

1. 최초 설계의 Phase 1~3: Core, Daily Dream, Weekly Meditation 구현.
2. 구현된 코드를 검토하고 정리하는 단계.
3. Phase 4: 관찰 UI / inspector 구현.
4. Phase 5: Benchmark 구현.

관찰 도구를 Core와 함께 먼저 구현하자는 논의 중 제안은 채택하지 않는다. Phase 1~3 이후 코드 정리를 거쳐 Phase 4를 진행한다.

Benchmark는 현재 구현 논의에서 제외한다. 당장 dataset 선정, adapter, harness, 평가 모델 또는 benchmark 실행을 진행하지 않는다. Phase 5에서 다시 다룬다.

## 3. 구현 언어

구현 언어는 **C#**으로 확정한다.

최초 설계의 SQLite, raw source Markdown 보관, OpenAI 기반 semantic reasoning 및 embedding 방향은 유지한다.

OpenAI 인증 방식, API 종류, 기본 모델과 reasoning effort는 이후 전달된 [OpenAI API 사용 확정](04-openai-api.md)을 따른다.

.NET 버전, 구체적인 라이브러리, MCP transport, 배포 방식은 아직 확정하지 않았다. C# 선택만으로 상시 실행 서버나 원격 접속 지원까지 확정된 것은 아니다.

## 4. 하나의 공유 기억 공간과 Remember의 책임

모든 기억은 하나의 공간에서 공유한다. 프로젝트별 또는 에이전트별로 기억 공간을 분리하지 않는다.

Public API의 기본 형태는 그대로 유지한다.

```text
remember(raw)
recall(query, context?)
trace(memory_id)
```

`remember(raw)`는 전달된 내용을 기억하는 기능이다. 누가 어느 프로젝트에서 발언했는지를 검증하거나, 그 정보를 별도 필수 입력으로 요구하는 기능이 아니다.

- `created_at`은 시스템 내부에서 생성한다.
- 발언자, 프로젝트, 세션 등의 metadata를 필수 인자로 추가하지 않는다.
- 발언자나 프로젝트 정보의 유무 또는 진위를 기억 수용의 validation 조건으로 삼지 않는다.
- 원문에 포함된 맥락은 원문의 일부로 보존한다.

이 결정은 Source 불변성, provenance, depth 및 DB 일관성 같은 Core validation을 제거한다는 의미가 아니다.

## 5. Remember의 입력 단위와 depth 0

### 5.1 관찰 단위의 입력

`remember(raw)`의 입력은 **하나의 관찰이 거의 하나의 기억에 대응하는 크기**로 제한하는 방향으로 변경한다.

최초 설계처럼 큰 session/trajectory 하나를 받아 다수의 독립 observation으로 분해하는 방식을 기본 입력 모델로 삼지 않는다.

```text
관찰 하나에 가까운 raw
    ↓
immutable Source
    ↓
그 관찰에 대응하는 depth-0 Memory
```

Depth 0은 여전히 raw 파일 자체가 아니라 원문에서 선택하고 최소한으로 정규화한 직접 observation이다. Remember 단계에서 일반화하지 않는 원칙은 유지한다.

정보량이 없는 입력에서 Memory를 만들지 않을 수 있다는 최초 설계의 원칙도 유지한다.

### 5.2 확정되지 않은 입력 제한

다음은 아직 정하지 않았다.

- raw의 구체적인 최대 문자 수 또는 토큰 수.
- 크기 제한을 넘거나 여러 관찰이 섞인 입력을 어떻게 처리할지.
- 출력 개수를 반드시 `0..1`로 강제할지, 관찰 단위 입력 정책으로 관리할지.

따라서 최초 설계의 자유로운 `0..N` 추출을 그대로 구현하는 것도, 논의 없이 엄격한 `0..1` 계약으로 확정하는 것도 피한다. 확정된 방향은 큰 입력의 다중 관찰 추출보다 관찰 단위의 입력을 사용한다는 것이다.

### 5.3 동일 raw의 반복 입력 차단

`remember(raw)`에 이미 수용한 것과 **동일한 raw**가 다시 들어오면 코드 수준에서 중복을 감지하고 추가 기억 생성을 막는다. 중복 판정에는 LLM을 사용하지 않는다.

- 비교 대상은 전달된 raw 본문이다. 시스템이 붙이는 `created_at`이나 Source ID는 비교 대상이 아니다.
- 동일성은 raw 문자열의 완전 일치를 기준으로 한다. 의미가 비슷한 문장이나 공백·대소문자 등이 다른 입력을 자동으로 같은 입력으로 간주하지 않는다.
- 중복 검사는 새 Source 생성과 LLM observation 추출 전에 수행한다.
- 중복 입력으로 새 Source나 Memory를 추가하거나 root support를 늘리지 않는다.
- 저장된 입력을 기준으로 검사하여 서버 재시작 후에도 적용하고, 여러 에이전트가 동시에 같은 입력을 보내도 중복 저장되지 않도록 한다.

raw의 hash를 조회 키로 사용하는 등 deterministic한 방식으로 구현할 수 있다. 구체적인 저장 방식과 중복 요청의 반환 형식은 구현 설계에서 정한다.

이 규칙은 `remember(raw)`의 반복 입력에 관한 것이다. 기존 Source를 더 좋은 모델로 재관찰하는 별도 내부 작업이나 depth > 0 abstraction의 의미상 중복 제거 정책과는 구분한다.

## 6. 기하급수적 root 제약의 단위 변경

**Source 하나를 root 하나로 센다.**

최초 설계의 서로 다른 depth-0 observation 개수 대신, provenance를 끝까지 따라갔을 때 도달하는 서로 다른 Source ID 개수로 root support를 계산한다.

```text
source_roots(depth-0 memory)
    = { memory.source_ref }

source_roots(depth-D memory)
    = union(source_roots(parent) for parent in derived_from)

unique_source_roots(memory)
    = count(source_roots(memory))

unique_source_roots(memory) >= B ^ memory.depth
```

같은 Source에 연결된 depth-0 Memory가 여러 개 존재하더라도 root support는 1이다. 향후 동일 Source를 재관찰하여 새 depth-0 Memory를 추가하는 경우에도 그 Source의 root 수는 늘지 않는다.

B=3인 경우:

| Depth | 최소 서로 다른 Source 수 |
| --- | --- |
| 0 | 1 |
| 1 | 3 |
| 2 | 9 |
| 3 | 27 |
| 4 | 81 |

다음 규칙은 함께 유지한다.

- Depth N은 Depth N-1 Memory에서만 derive한다.
- Depth > 0의 `derived_from`에는 서로 다른 부모가 최소 B개 있어야 한다.
- `derived_from`은 생성 이후 변경하지 않는다.
- Derivation graph에는 cycle이 없어야 한다.
- 같은 Source를 공유하는 부모들을 여러 개 사용해도 root는 중복 계산하지 않는다.

B는 최초 설계대로 설정값으로 두며, 최초 설계의 초기값 제안은 3이다.

Source ID 기준 집계와 별개로, §5.3의 동일 raw 검사로 반복 입력이 별도 Source를 만들어 root 수를 늘리는 것을 막는다. 의미가 비슷하지만 raw가 다른 입력을 같은 Source로 합치는 정책은 도입하지 않는다.

## 7. Positive / Negative Relation의 방향과 생성 시각

### 7.1 단방향 관계

Relation은 **단방향으로 저장하고 단방향으로 조회한다. 역방향 조회는 제공하지 않는다.**

예를 들어:

```text
A.negative_related = [B]
```

이것은 A에서 B를 negative relation으로 참조한다는 정보다. 이 기록만으로 B에서 A로 향하는 relation이 존재한다고 해석하지 않는다.

- A의 relation 목록을 통해 B로 이동할 수 있다.
- B에서 자신을 참조하는 A를 찾는 역방향 relation 조회는 제공하지 않는다.
- 반대 방향 edge를 자동으로 생성하지 않는다.
- UI/inspector에서도 역방향 relation 탐색을 제공하지 않는다.

이 제한은 positive/negative relation에 관한 것이다. `trace(memory_id)`가 `derived_from`을 따라 부모 Memory와 Source로 내려가는 동작은 유지한다.

동일한 방향의 동일 Memory 쌍에 positive와 negative를 동시에 허용할지는 아직 결정하지 않았다.

### 7.2 Relation별 `related_at`

Positive/negative relation을 생성할 때 **각 relation에 `related_at`을 기록한다.** `related_at`은 Core가 내부에서 부여하는 관계 생성 시각이다.

예를 들어 `A.negative_related = [B]`에 대응하는 저장 정보는 다음을 포함한다.

```text
memory_id = A
related_memory_id = B
kind = negative
related_at = 관계가 생성된 시각
```

- `related_at`은 Memory 단위의 최근 수정 시각이 아니라 개별 relation에 붙는 시각이다.
- Relation 추가 시 기존 Memory의 `created_at`, content 또는 `derived_from`을 변경하지 않는다.
- 기존 relation을 다시 조회하거나 발견한 것만으로 `related_at`을 갱신하지 않는다.
- Meditation은 이 시각으로 최근 1주일 동안 추가된 relation을 찾는다.

`A → B` relation 추가는 A의 outgoing relation 목록에 생긴 변경이다. 변경점 수집을 이유로 B에서 A를 찾는 역방향 relation 조회를 도입하지 않는다.

## 8. Daily Dream

### 8.1 시작점과 탐색 범위

Daily Dream의 seed는 다음으로 제한한다.

```text
오늘 생성된 Memory 중 depth == 0
오늘 recall된 Memory — 모든 depth
```

두 집합의 합집합을 seed로 사용한다. 오늘 생성된 depth > 0 Memory는 생성됐다는 사실만으로 seed가 되지 않는다. 해당 Memory가 오늘 recall됐다면 recall된 집합에 포함될 수 있지만, Dream generation barrier는 그대로 적용한다.

그 seed와 연관된 기억까지 탐색할 수 있다. 날짜와 관계없이 전체 corpus의 임의 Memory를 일일 seed로 삼지는 않는다.

일일 작업에서는 **그날 새로 생긴 모든 observation을 처리한다.** Daily Dream에는 비용 budget 제한을 두지 않는다.

최초 설계의 Assimilation과 Consolidation 구조, same-depth 부모 선택, Core validation 및 Dream generation barrier는 유지한다. 실행 중 생성한 Memory를 같은 Dream에서 다시 생성 재료로 사용할 수 없다.

### 8.2 중복 abstraction

오늘 생성된 depth-0 Memory와 오늘 recall된 모든 depth의 Memory에서 시작하는 제한된 작업 범위로 불필요한 반복 생성을 줄인다.

그럼에도 depth > 0에서 비슷하거나 중복된 abstraction이 생길 수 있다. 초기에는 이를 허용하고 관찰한다. 중복 abstraction을 막기 위한 별도의 semantic 병합·제거 정책을 지금 도입하지 않는다.

Recall 사실 자체가 evidence가 되거나 retrieval ranking을 강화하지 않는다는 규칙은 그대로 유지한다.

## 9. Weekly Meditation

Weekly Meditation은 **1주일마다** 실행하며, Daily Dream보다 넓은 범위를 탐색한다.

처리의 출발점은 **최근 1주일 동안 생긴 depth 1 이상 Memory의 변경점**이다.

변경점에는 다음을 포함한다.

- 최근 1주일 동안 생성된 depth >= 1 Memory: Memory의 `created_at`으로 찾는다.
- Depth >= 1 Memory에 최근 1주일 동안 추가된 positive/negative relation: 각 relation의 `related_at`으로 찾는다. Memory 자체가 생성된 시점은 이전 주여도 된다.

두 번째 항목의 depth 조건은 relation을 보유한 Memory에 적용한다. 예를 들어 depth-1 Memory A의 `negative_related`에 depth-0 Memory B가 이번 주 추가되었다면 A의 변경점으로 수집한다. B의 생성 시각이나 depth 때문에 이 변경점을 빠뜨리지 않는다.

```text
created_at / related_at으로 최근 1주일의 depth >= 1 변경점 수집
    ↓
처리 우선순위 결정
    ↓
우선순위가 높은 항목부터 넓은 기억 탐색 및 reasoning
    ↓
해당 실행의 N달러 budget에 도달할 때까지 처리
```

- Dream과 달리 Meditation에는 금액 기준 budget을 둔다.
- N의 실제 금액은 아직 정하지 않았다.
- 처리 우선순위의 구체적인 기준과 구현은 아직 정하지 않았다.
- 수집 대상의 위 변경점을 모두 확인한 뒤 우선순위를 정하고, 실제 처리는 N달러 budget 내에서 진행한다.
- 넓은 탐색에는 최초 설계처럼 필요한 depth-0 Memory와 raw Source 확인이 포함될 수 있다.

변경점을 처리한다는 표현은 기존 Memory의 content나 `derived_from`을 수정한다는 뜻이 아니다. 결과는 최초 설계대로 새로운 Memory이며, strict layering과 Source 기준 root 제약을 지켜야 한다.

작업 순서를 정하기 위한 우선순위가 곧 Memory의 truth, confidence 또는 영구적인 importance 필드가 되는 것은 아니다. 최초 설계의 단순한 Memory 모델을 유지한다.

## 10. 기하급수적 제약과 Benchmark의 관계

기하급수적 root 제약은 **일반 실행과 기본 benchmark 모두에서 항상 강제하는 Core invariant**다.

최초 설계 §28 Ablation의 다음 단계는 제거한다.

```text
E. + Geometric depth constraints
```

이 제약을 선택적으로 추가하는 기능으로 취급하거나, 해당 ablation을 위해 Core 제약을 해제하는 설계는 채택하지 않는다.

그 외 benchmark 상세 설계와 최초 적용 대상 선정은 Phase 5까지 보류한다. 관찰 단위 입력 결정에 따라 최초 설계의 session/trajectory 단위 `remember()` replay 방식도 그때 재검토해야 한다.

## 11. 후속 설계에서 확정할 사항

아래 사항은 논의에 등장했거나 구현 전에 구체화해야 하지만, 이번에 확정되지는 않았다.

- C# 프로젝트의 .NET 버전, 라이브러리 및 서버 실행/접속 방식.
- 관찰 단위 raw의 크기 제한, 초과 입력 처리 및 엄격한 출력 개수 제한 여부.
- 중복 `remember` 요청의 반환 형식 및 처리 중/실패 입력의 재시도 방식.
- 동일 방향의 Memory 쌍에 positive/negative relation을 함께 허용할지 여부.
- Dream의 일자 경계, timezone, 실행 시각 및 실행을 놓쳤을 때의 처리.
- Meditation의 N달러 값, 비용 집계·중단 방식, 우선순위 기준 및 미처리 항목의 이월 방식.
- Dream/Meditation의 공통 revision 운영 여부, 실패 시 재시도 및 embedding 모델 교체의 세부 절차.

이 목록은 지금 모두 결정해야 한다는 뜻이 아니다. 해당 구현 단계를 설계할 때 확정하며, 미답변 제안을 사용자 합의로 간주하지 않는다.
