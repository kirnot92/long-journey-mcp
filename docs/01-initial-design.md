# Long Journey MCP — 설계 및 구현 명세

## 0. 프로젝트 목표

`long-journey-mcp`는 여러 에이전트와 여러 세션이 장기간 공유할 수 있는 개인용 장기 메모리 시스템이다.

이 시스템의 핵심 전제는 다음과 같다.

> Memory는 truth가 아니다.

시스템은 “현재 무엇이 참인지”를 관리하지 않는다.

대신 다음을 장기간 보존한다.

1. 실제 입력 원문
2. 그 원문에서 추출한 직접 observation
3. 여러 observation/memory를 바탕으로 형성된 abstraction
4. 이후 발견된 supporting / contradicting evidence
5. abstraction이 어디서 유래했는지를 나타내는 provenance

Recall된 memory 역시 현재 상황에서 다시 판단해야 하는 참고 자료일 뿐이다.

이 설계의 장기 목표는 특정 LLM 세대에 종속되지 않는 메모리 corpus를 만드는 것이다. 모델이 발전해도 기존 source와 memory를 버리지 않고, 더 좋은 모델이 Dream/Meditation을 통해 과거 경험을 계속 재해석할 수 있어야 한다.

---

# 1. 핵심 철학

## 1.1 Truth store가 아니다

다음과 같은 canonical state를 만들지 않는다.

```text
user_preference = explicit_api
```

대신 다음과 같은 memory graph를 유지한다.

```text
M1:
"이 API 논의에서는 explicit primitive를 선호한다고 말했다."

M2:
"다른 API에서도 implicit behavior를 싫어했다."

M3:
"반복 작업에서는 automation을 선호한다고 말했다."

M4:
"제어 비용이 큰 경우 explicit control을 선호하는 경향이 있다."
```

M4도 truth가 아니다.

단지 여러 memory를 바탕으로 한 abstraction이다.

---

## 1.2 원문을 잃지 않는다

컴퓨터는 인간처럼 원 경험을 반드시 잊을 필요가 없다.

따라서:

```text
raw source
→ observation
→ abstraction
→ higher abstraction
```

전 계층을 보존한다.

상위 abstraction은 원문을 대체하지 않는다.

---

## 1.3 모든 계층은 동일한 Memory 타입이다

`Episode`, `Fact`, `Schema`, `Belief`, `Profile` 등의 별도 ontology를 만들지 않는다.

모든 것은 `Memory`다.

차이는 `depth`와 생성 provenance뿐이다.

---

# 2. Source

`remember(raw)`에 전달된 원문은 Memory가 아니다.

원문은 immutable source artifact로 먼저 저장한다.

권장 형식:

```text
sources/
  2026/
    09/
      04/
        <source-id>.md
```

예:

```md
---
id: src_xxx
created_at: 2026-09-04T12:00:00Z
---

이번 API는 magic이 너무 많아서 별로고,
차라리 lower-level primitive 몇 개를 조합하는 게 낫다.
반복적인 배포는 자동화되는 게 편하다.
```

Source는 생성 이후 수정하지 않는다.

필요하면 새 Source를 추가한다.

DB의 `source_ref`는 실제 파일 경로 대신 stable source ID를 사용한다.

```text
source_ref = src_xxx
```

Storage layout이 바뀌어도 graph reference가 깨지지 않아야 한다.

---

# 3. Remember

Public API:

```text
remember(raw)
```

Remember는 “무조건 기억 하나를 만드는 함수”가 아니다.

역할:

```text
raw source
→ immutable source 저장
→ LLM observation extraction
→ 0..N depth-0 memories
```

LLM은 source에서 미래에 독립적으로 다시 떠올릴 가치가 있는 직접 observation을 0개 이상 추출한다.

예:

```text
입력:

"이번 API는 magic이 너무 많아서 별로다.
primitive 몇 개만 주는 쪽이 더 좋다.
배포는 자동화가 편하다."
```

출력:

```json
[
  {
    "content": "사용자는 이 API 논의에서 magic이 많은 설계보다 작은 primitive를 직접 조합하는 방식을 선호한다고 말했다."
  },
  {
    "content": "사용자는 반복적인 배포 작업에서는 automation을 편리하게 여긴다고 말했다."
  }
]
```

서버가 각 결과에 다음을 붙인다.

```text
depth = 0
source_ref = src_xxx
created_at = ...
```

다음과 같이 정보량이 거의 없는 입력은:

```text
야
왜
안녕
```

정상적으로:

```json
[]
```

을 반환할 수 있어야 한다.

---

# 4. Depth 0의 정의

Depth 0은 raw data 자체가 아니라:

> 원문에서 선택되고 최소한으로 정규화된 직접 observation

이다.

Remember 단계에서 허용:

```text
"사용자가 이 API에 대해 'magic이 너무 많다'고 말했다."
```

가급적 생성하지 않을 것:

```text
"사용자는 automation을 싫어하는 사람이다."
```

후자는 여러 경험에 적용되는 generalization이므로 Dream/Meditation의 책임이다.

정리:

```text
remember
= selection + minimal normalization

dream / meditation
= interpretation + abstraction
```

Depth 0도 fallible하다.

잘못 해석될 수 있으므로 항상 `source_ref`로 raw source까지 내려갈 수 있어야 한다.

---

# 5. Memory 데이터 모델

초기 모델은 최대한 단순하게 유지한다.

```text
Memory
{
    id
    depth
    content

    source_ref?             // depth == 0
    derived_from[]          // depth > 0

    positive_related[]
    negative_related[]

    created_at
    dream_revision
    last_recalled_at?
}
```

의도적으로 다음 필드는 만들지 않는다.

```text
salience
confidence
truth_score
importance
surprise_score
memory_strength
```

초기 버전에서는 관측 가능하고 의미가 명확한 데이터만 저장한다.

---

# 6. Depth의 정확한 의미

Depth는 다음이 아니다.

```text
depth ≠ truth
depth ≠ confidence
depth ≠ importance
```

Depth의 정의:

> consolidation generation.

```text
depth 0
= remember가 source에서 직접 생성

depth 1
= depth 0 memories를 consolidation해서 생성

depth 2
= depth 1 memories를 consolidation해서 생성
```

높은 depth일수록 더 추상적일 가능성은 높지만, 그것을 invariant로 정의하지 않는다.

---

# 7. Derived From

`derived_from`은 Memory가 태어날 때 사용된 provenance다.

예:

```text
M100
depth = 1

content:
"이 프로젝트에서 generated code 문제는 build pipeline부터 조사할 가치가 있다."

derived_from:
[M12, M31, M48]
```

규칙:

1. 생성 시 한 번 결정
2. 이후 immutable
3. 새로운 evidence가 생겨도 추가하지 않음

다음은 금지한다.

```text
M100.derived_from += M200
```

새로운 후속 evidence는 `positive_related` 또는 `negative_related`에 들어간다.

---

# 8. Positive / Negative Relations

`positive_related`:

> Memory 생성 이후 발견된, 해당 Memory와 일관되거나 이를 지지하는 memory.

`negative_related`:

> Memory 생성 이후 발견된 반례, 예외, 모순, tension.

예:

```text
M100:
"제어권이 중요한 API에서는 explicit 설정을 선호하는 경향"

derived_from:
[M1, M2, M3]

positive_related:
[M10, M17]

negative_related:
[M31]
```

중요한 invariant:

```text
positive_count / total
```

을 confidence/truth로 변환하지 않는다.

Relations는 evidence neighborhood다.

단순 semantic similarity는 relation으로 저장하지 않는다.

그 역할은 embedding search가 맡는다.

---

# 9. Depth 폭증 방지

Depth가 LLM의 반복적인 자기 재해석으로 빠르게 증가하지 못하도록 deterministic invariant를 둔다.

Base:

```text
B = configurable
초기값 제안: 3
```

Depth `D` memory는 최소 다음 수의 서로 다른 depth-0 root observation을 가져야 한다.

```text
unique_depth0_ancestors >= B ^ D
```

B=3 예:

```text
depth 0 >= 1
depth 1 >= 3
depth 2 >= 9
depth 3 >= 27
depth 4 >= 81
```

추가 규칙:

```text
depth N memory는 depth N-1 memory에서만 derive 가능

derived_from.Count >= B

DAG cycle 금지
```

Root support는 parent 수가 아니라 unique depth-0 ancestor union으로 계산한다.

예:

```text
M1 roots = {A,B,C}
M2 roots = {A,B,C}
M3 roots = {A,B,C}
```

M1/M2/M3 세 개를 사용해도 unique root는 3이므로 depth 2의 최소 9개 조건을 만족하지 못한다.

이를 통해 동일 evidence를 여러 abstraction에 재사용하여 가짜 depth를 만드는 것을 차단한다.

---

# 10. Public API

Public MCP/API surface는 세 개만 둔다.

```text
remember
recall
trace
```

Dream과 Meditation은 내부 scheduler다.

---

# 11. Recall

Public API:

```text
recall(query, context?)
```

목적:

> 현재 판단에 참고할 가치가 있어 보이는 과거 memory를 찾는다.

Recall은 truth lookup이 아니다.

---

## 11.1 Candidate retrieval

BM25/FTS와 embedding을 동시에 사용한다.

```text
query
 ├─ BM25 / lexical
 └─ embedding similarity
          ↓
      candidate fusion
          ↓
      LLM selection
```

BM25가 필요한 이유:

```text
고유명사
에러 코드
파일 경로
정확한 표현
ID
```

Embedding이 필요한 이유:

```text
표현은 다르지만 의미가 비슷한 기억
높은 depth abstraction
자연어 개념 질의
```

초기 fusion은 RRF 같은 단순 deterministic rank fusion을 사용한다.

복잡한 weighting은 나중 문제다.

---

## 11.2 LLM contextual selection

Candidate를 GPT에 제공하고 현재 query/context에서 실제로 떠올릴 가치가 있는 memory만 선택한다.

GPT는 depth가 confidence가 아니라는 사실을 명시적으로 알고 있어야 한다.

높은 depth를 항상 선호하지 않는다.

구체적 질의에서는 낮은 depth가 더 적절할 수 있다.

---

## 11.3 Recall reinforcement

Recall됐다는 사실은 evidence가 아니다.

Recall로 다음을 하지 않는다.

```text
positive_related 증가
root support 증가
depth 상승
retrieval score boost
```

필요하면:

```text
last_recalled_at = now
```

만 기록한다.

이는 Daily Dream seed selection 등에 사용할 수 있지만 retrieval ranking을 직접 강화하지 않는다.

---

# 12. Trace

Public API:

```text
trace(memory_id)
```

역할:

> 왜 이런 memory가 존재하는지 provenance를 따라 내려간다.

동작:

```text
depth 3
 ↓ derived_from
depth 2
 ↓
depth 1
 ↓
depth 0
 ↓ source_ref
raw source.md
```

Trace는 가능한 한 deterministic하게 구현한다.

LLM reasoning은 필요하지 않다.

---

# 13. Daily Dream

Dream은 public API가 아니다.

Memory server 내부 scheduler가 기본 하루 1회 실행한다.

실제 scheduling frequency는 operational policy이며 memory semantics와 분리한다.

Dream의 입력 중심:

```text
오늘 remember된 depth-0 memories
오늘 recall된 memories
```

Dream은 두 단계만 수행한다.

```text
1. Assimilation
2. Consolidation
```

---

# 14. Daily Dream — Assimilation

새 observation이 기존 memory graph와 어떤 관계인지 판단한다.

흐름:

```text
new depth-0 observation
        ↓
BM25 / embedding으로 관련 기존 memories 검색
        ↓
GPT
        ├─ supports
        ├─ contradicts
        └─ unrelated
```

결과:

```text
positive_related 추가

또는

negative_related 추가
```

기존 memory의:

```text
content
derived_from
```

은 절대 수정하지 않는다.

Recall된 memory 역시 Dream에서 재검토 seed가 될 수 있다.

하지만 recall됐다는 사실 자체는 evidence가 아니다.

---

# 15. Daily Dream — Consolidation

같은 depth의 memory들 가운데 새로운 abstraction이 형성될 수 있는 local neighborhood를 탐색한다.

초기 버전에서는 전통적인 hard clustering을 하지 않는다.

이유:

> 하나의 memory가 여러 abstraction의 재료가 될 수 있기 때문이다.

따라서 overlapping membership을 허용한다.

초기 알고리즘:

```text
eligible seed memory
        ↓
same-depth embedding top-K
        ↓
direct positive / negative relations
        ↓
local candidate neighborhood
        ↓
GPT
        ↓
0..N abstraction proposals
```

GPT가 실제 `derived_from` subset을 선택한다.

예:

```text
candidate neighborhood:
[M1,M2,M3,M4,M5,M6]

GPT proposal:
derived_from = [M1,M3,M4]
content = "..."
```

Core가 invariant를 검사한 뒤 새 Memory를 생성한다.

향후 필요하면:

```text
HDBSCAN
kNN graph
community detection
```

등을 seed/neighborhood discovery에 사용할 수 있으나 v1에서는 도입하지 않는다.

---

# 16. Dream Generation Barrier

한 번의 Dream에서 생성된 memory는 같은 Dream에서 다시 source가 될 수 없다.

Dream 시작 시 revision snapshot을 고정한다.

```text
Dream revision R

input:
dream_revision < R

output:
dream_revision = R
```

따라서 한 번의 Dream에서:

```text
depth 0
→ depth 1
→ depth 2
→ depth 3
```

이 연쇄 생성되는 것을 막는다.

새 Memory는 다음 Dream부터 source가 될 수 있다.

---

# 17. Weekly Meditation

Meditation은 Memory Server 내부 scheduler가 주 단위로 수행하는 deep reasoning phase다.

Daily Dream이:

> 이 경험은 기존 기억과 어떤 관계인가?

를 묻는다면 Meditation은:

> 왜 이런 패턴이 반복되는가?

를 묻는다.

주요 입력 후보:

```text
최근 생성된 abstractions
negative relation이 많은 영역
최근 반복적으로 recall된 영역
설명되지 않은 contradiction
서로 다른 local neighborhood에서 반복되는 패턴
```

Meditation은 Dream보다 넓은 graph traversal을 허용한다.

필요하면 depth 0 및 raw source까지 trace한다.

---

# 18. Meditation의 역할

LLM은 다음과 같은 reasoning을 수행할 수 있다.

```text
공통 조건 탐색
반례 분석
기존 abstraction이 과도하게 일반화됐는지 판단
alternative explanation 생성
잠재적 인과관계 탐색
higher-order abstraction 생성
```

그러나 Meditation도 truth generator가 아니다.

다음처럼 확정적으로 저장하는 것을 피한다.

```text
"X causes Y."
```

대신 evidence에 맞는 provisional formulation을 사용한다.

```text
"현재 observation들에서는 X가 Y의 조건 또는 원인 후보로 보인다."
```

Meditation 결과도 동일한 `Memory`다.

별도의 CausalModel/Belief 타입을 만들지 않는다.

새 Memory의 `derived_from`은 strict layering invariant를 그대로 지킨다.

LLM은 여러 depth를 읽을 수 있지만 새 Memory는 반드시 바로 아래 depth memory로부터 derive되어야 한다.

---

# 19. LLM / Deterministic Core 역할 분리

## LLM = cognition

LLM이 판단할 것:

```text
source에서 무엇을 observation으로 기억할지
무엇이 현재 query에 관련 있는지
support / contradiction인지
어떤 abstraction이 존재하는지
어떤 subset을 parents로 사용할지
어떤 explanatory hypothesis가 가능한지
```

## Core = physics

Core가 강제할 것:

```text
source immutable
depth 계산
strict layering
B^depth root support
minimum parent count
cycle 금지
derived_from immutable
stable IDs
dream revision barrier
DB consistency
```

LLM은 직접 임의 SQL mutation을 하지 않는다.

LLM은 structured proposal을 생성한다.

Core가 validation 후 적용한다.

---

# 20. OpenAI API 사용

초기 구현은 모든 semantic reasoning에 OpenAI API를 사용한다.

구체 model name과 reasoning effort는 configuration으로 둔다.

최소한 역할별 config를 분리한다.

```text
remember_model
recall_model
dream_model
meditation_model
embedding_model
```

Meditation은 가장 높은 reasoning budget을 사용할 수 있다.

Remember는 가장 낮은 비용으로 수행할 수 있다.

모델 세대가 바뀌더라도 memory corpus/schema를 다시 만들 필요가 없도록 한다.

모델 이름은 DB invariant가 아니다.

단, provenance/실험 분석을 위해 `created_by_model` 같은 metadata를 기록하는 것은 허용한다.

이 값은 confidence 계산에 사용하지 않는다.

---

# 21. 장기 모델 교체

Source archive와 Memory history는 특정 LLM과 분리한다.

새로운 모델이 출시되어도 기존 corpus를 폐기하지 않는다.

새 모델은 기존:

```text
sources
depth-0 observations
higher memories
relations
```

을 읽고 Dream/Meditation을 계속 수행한다.

필요하면 raw source를 다시 관찰하여 새로운 depth-0 observation을 추가할 수 있다.

기존 observation은 수정하지 않는다.

예:

```text
Old observation:
"사용자는 automation을 싫어한다."

New model re-observation:
"원 발언은 automation 일반이 아니라 해당 API의 implicit magic에 대한 불만이었다."

negative relation:
new observation -> old observation
```

따라서 시스템은 모델 세대가 바뀌어도 장기적으로 진화할 수 있어야 한다.

---

# 22. Persistence

초기 구현에서는 SQLite를 사용한다.

필요한 저장 요소:

```text
sources metadata
memories
derived_from edges
positive_related edges
negative_related edges
embeddings
recall events / last_recalled_at
dream revisions
scheduler state
```

Raw source 본문은 Markdown files로 저장한다.

SQLite에는 source ID와 metadata를 저장한다.

---

# 23. Search

초기 search stack:

```text
SQLite FTS5 / BM25
+
OpenAI embeddings
```

Corpus가 작을 때는 exact cosine similarity도 허용한다.

처음부터 ANN/HNSW를 도입하지 않는다.

실제 corpus/benchmark에서 필요성이 확인된 이후 최적화한다.

---

# 24. 관찰 UI

이 프로젝트에서는 memory graph 자체를 관찰하는 것이 매우 중요하다.

최소한 read-only Web UI 또는 CLI inspector를 구현한다.

다음 정보를 확인 가능하게 한다.

```text
Memory ID
depth
content
created_at
source_ref
derived_from
positive_related
negative_related
unique depth-0 root count
last_recalled_at
dream revision
created_by_model
```

추가 화면/명령:

```text
depth별 memory count
최근 생성 memory
최근 relations
trace tree
source raw text
dream/meditation run history
```

Memory formation 자체가 연구 대상이므로 inspection tooling을 후순위로 미루지 않는다.

---

# 25. Benchmark Harness

첫 실험에서는 agent가 자발적으로 `remember()`를 호출하게 하지 않는다.

Benchmark harness가 chronological experience stream을 replay하면서 deterministic하게 `remember()`를 호출한다.

목적:

> Memory engine의 품질과 agent의 tool-usage 능력을 분리한다.

개념적 흐름:

```text
benchmark history
      ↓ chronological replay
Harness calls remember(session/trajectory)
      ↓
source + depth0 memories
      ↓
simulated Dream/Meditation scheduler
      ↓
question
      ↓
recall(question)
      ↓
fixed answer model
      ↓
benchmark evaluator
```

Bench source unit은 turn 하나가 아니라 가능한 한 session/trajectory 단위로 사용한다.

Remember가 한 source에서 0..N observations를 추출한다.

---

# 26. Simulated Scheduler

Benchmark에서는 실제 하루/일주일을 기다리지 않는다.

Dataset timestamp가 존재하면 그것을 simulated clock으로 사용한다.

예:

```text
day 1 sessions
→ Daily Dream

day 2 sessions
→ Daily Dream

...

7 days
→ Meditation
```

Timestamp가 적절하지 않은 경우 session count 기반 scheduler를 별도 실험 config로 둘 수 있다.

---

# 27. Benchmark 대상

초기에는 다음 계열을 고려한다.

```text
LongMemEval
LongMemEval-V2
LoCoMo
```

특히 LongMemEval-V2는 과거 agent trajectory에서 workflow knowledge, environment gotcha, dynamic state 등을 배우는 능력을 볼 수 있으므로 중요한 target이다.

Benchmark adapter는 core memory engine과 분리한다.

---

# 28. Ablation

Memory architecture 각 부분의 실제 효과를 분리해서 볼 수 있어야 한다.

초기 최소 ablation:

```text
A. Plain full-history / baseline RAG

B. Remember only
   source → depth0 → recall

C. + Daily Dream consolidation

D. + Assimilation
   positive/negative relations

E. + Geometric depth constraints

F. + Weekly Meditation
```

동일한:

```text
answer model
remember model
recall model
embedding model
prompt
```

을 가능한 한 고정한다.

---

# 29. 평가

최종 benchmark score만 보지 않는다.

두 종류를 동시에 관찰한다.

## External performance

```text
benchmark accuracy
retrieval recall
answer accuracy
token usage
API cost
latency
```

## Internal memory morphology

```text
depth distribution
memory count
duplicate abstraction rate
positive/negative edge growth
평균 root support
generic abstraction 비율
trace plausibility
causal hallucination
contradiction handling
```

예를 들어:

```text
D0 100000
D1   8000
D2    300
D3      7
```

과 같은 구조가 어떻게 자연스럽게 형성되는지 기록한다.

---

# 30. 실험 개발 원칙

처음부터 다음 개념을 만들지 않는다.

```text
salience
surprise score
confidence
importance score
forgetting equation
complex clustering
ANN index
automatic truth resolution
```

구체적인 failure mode가 관찰된 이후 최소한의 기능만 추가한다.

개발 사이클:

```text
Design hypothesis
       ↓
Minimal implementation
       ↓
Benchmark
       ↓
Inspect score + actual memories
       ↓
Identify concrete failure mode
       ↓
One targeted change
       ↓
Ablation / rerun
```

---

# 31. 초기 구현 순서

## Phase 1 — Core

구현:

```text
Source archive
Memory schema
SQLite persistence
remember
trace
BM25
embeddings
recall
```

이 단계에서 depth 0 memory system이 정상 작동해야 한다.

## Phase 2 — Dream

구현:

```text
dream revisions
Assimilation
positive/negative relations
same-depth neighbor retrieval
Consolidation
B^depth validation
generation barrier
```

## Phase 3 — Meditation

구현:

```text
weekly scheduler
deep graph exploration
contradiction analysis
higher-order abstraction
```

## Phase 4 — Inspection

구현:

```text
read-only memory browser
trace visualization/text view
source view
depth statistics
dream/meditation run inspection
```

## Phase 5 — Benchmark

구현:

```text
benchmark adapters
simulated clock
ablation configs
results persistence
memory snapshots
score comparison
```

---

# 32. 핵심 Invariants

구현 전 테스트로 고정할 것.

1. 모든 cognitive item은 동일한 `Memory` 타입이다.
2. Source는 immutable이다.
3. `remember()`는 한 source에서 0..N memories를 만들 수 있다.
4. Remember가 만드는 memory는 항상 depth 0이다.
5. Depth 0에는 `source_ref`가 필요하다.
6. Depth > 0에는 `derived_from`이 필요하다.
7. Depth N은 Depth N-1에서만 derive 가능하다.
8. `derived_from`은 생성 이후 변경하지 않는다.
9. `unique_depth0_roots >= B^depth`.
10. `derived_from.Count >= B`.
11. Derivation graph에는 cycle이 없다.
12. Positive/negative relations는 truth/confidence score가 아니다.
13. Recall은 evidence를 생성하지 않는다.
14. Recall은 retrieval ranking을 자기강화하지 않는다.
15. 같은 Dream revision에서 생성된 memory는 같은 revision의 source가 될 수 없다.
16. Dream과 Meditation 결과도 truth가 아니라 Memory다.
17. 모든 abstraction은 trace를 통해 depth 0과 raw source까지 내려갈 수 있다.
18. 모델 교체가 memory migration을 요구해서는 안 된다.

---

# 33. 비목표

초기 버전에서는 다음을 하지 않는다.

```text
human moderation
canonical truth resolution
memory edit/delete workflow
confidence calculation
automatic user profile object
separate belief/fact/schema ontology
hard clustering
complex forgetting
memory scoring formula
```

Raw source와 history를 보존하는 것을 우선한다.

---

# 34. 프로젝트의 핵심 문장

> Long Journey는 무엇이 참인지 저장하는 시스템이 아니다.

> 여러 세션과 여러 에이전트가 무엇을 관찰했고, 그 경험들로부터 어떤 잠정적 생각을 형성했으며, 이후 어떤 경험이 그것을 지지하거나 반박했는지를 장기간 추적 가능한 형태로 유지하는 시스템이다.

> 모델은 바뀔 수 있지만 여행은 계속된다.