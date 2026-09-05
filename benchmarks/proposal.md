# 벤치마크 제안서

## 1. 실험 목적

Long Journey는 원 경험에서 추출한 직접 기억을 그대로 검색하는 데 그치지 않고, Daily Dream과 Weekly Meditation을 통해 기억 사이의 관계와 상위 abstraction을 지속적으로 형성한다.

이 구조가 실제 장기 기억 성능에 기여하는지는 아직 검증되지 않았다. 따라서 첫 번째 benchmark에서는 시스템 전체를 한꺼번에 평가하기보다 다음 질문에 집중한다.

> 동일한 원 경험과 동일한 depth-0 기억에서 출발했을 때, Dream과 Meditation을 거쳐 형성된 계층적 기억 구조가 장기 retrieval 성능을 개선하는가?

이번 실험의 목적은 Long Journey의 핵심 consolidation 가설을 검증하는 것이다.

---

## 2. 연구 가설

귀무가설은 Dream과 Meditation을 적용하더라도 직접 관찰 기억만 사용하는 경우와 retrieval 성능에 의미 있는 차이가 없다는 것이다.

대립가설은 Dream과 Meditation을 통해 형성된 abstraction과 relation이 과거 경험을 다시 찾는 데 도움이 되어, 직접 관찰 기억만 사용하는 경우보다 관련 Source를 더 자주 회수한다는 것이다.

실험에서는 최종 답변 정확도와 retrieval 성능을 구분하여 관찰한다. 이를 통해 결과 변화가 기억 검색 단계에서 발생한 것인지, 검색된 기억을 답변 모델이 활용하는 단계에서 발생한 것인지 분리할 수 있도록 한다.

---

## 3. Benchmark 선정

첫 실험에는 **LongMemEval-S**를 사용한다.

LongMemEval-S를 선택하는 가장 큰 이유는 대화 history가 session 단위로 구성되어 있고, 질문에 대한 근거가 어느 session에 존재하는지 확인할 수 있기 때문이다.

이는 Long Journey에서 중요한 Source provenance와 직접 대응한다.

Benchmark의 각 session은 하나의 독립된 경험 단위로 취급한다.

```text
LongMemEval session
        ↓
      Source
        ↓
depth-0 memories
```

대화의 개별 발화나 문장을 별도의 Source로 취급하지 않는다.

하나의 session에서 여러 개의 depth-0 observation이 추출되더라도 동일한 Source를 provenance로 공유한다. 따라서 한 대화 안에서 같은 내용이 여러 표현으로 반복되었다는 이유만으로 독립적인 root support가 증가하지 않는다.

---

## 4. 기억 생성 방법

Benchmark history는 시간 순서대로 재생한다.

각 session 전체를 하나의 Source로 저장한 뒤, Long Journey의 Remember cognition을 이용해 해당 session에서 기억할 가치가 있는 직접 observation을 추출한다.

실제 MCP에서는 caller agent가 기억할 가치가 있는 **하나의 coherent experience**를 판단하고, 이를 이해하는 데 필요한 맥락과 함께 `remember(raw)`를 호출한다. 이 단위는 개별 문장이나 발화와 동일하지 않으며, 하나의 사건·결정·관찰을 이해하는 데 필요한 여러 발화를 포함할 수 있다.

Benchmark에서는 이러한 caller의 segmentation 판단 능력을 실험 변수에서 제외한다. 따라서 LongMemEval이 제공하는 session boundary를 경험 단위로 사용하고, 별도의 benchmark ingestion adapter가 session 전체를 하나의 Source로 보존하면서 그 안에서 여러 depth-0 Memory를 추출한다.

예를 들면 다음과 같다.

```text
Session 17
 ├─ observation A
 ├─ observation B
 └─ observation C

A.source_ref = Session 17
B.source_ref = Session 17
C.source_ref = Session 17
```

Benchmark의 정답, evidence label, 질문 또는 평가용 annotation은 기억 생성 과정에 제공하지 않는다.

---

## 5. 실험 조건

두 조건을 비교한다.

### Remember Only

Session에서 추출된 depth-0 Memory만 저장하고 이를 대상으로 recall을 수행한다.

이 조건은 Long Journey의 observation extraction과 기본 retrieval이 어느 정도 성능을 내는지를 보여주는 기준선 역할을 한다.

### Full Long Journey

동일한 depth-0 Memory에서 시작하여 실제 운영과 같은 방식으로 Daily Dream과 Weekly Meditation을 수행한다.

Dataset timestamp를 simulated clock으로 사용하고, 각 날짜가 끝날 때 Dream을 수행한다. 주간 구간이 완료되면 Meditation을 수행한다.

이 조건에서는 depth-0 Memory 외에도 Dream과 Meditation이 생성한 higher-depth Memory와 positive/negative relation이 corpus에 존재하게 된다.

두 조건의 차이는 consolidation 과정의 유무이며, 가능한 한 나머지 조건은 동일하게 유지한다.

특히 observation extraction 결과는 한 번 생성한 뒤 두 실험 조건에서 공유한다. 이렇게 하면 Remember 모델의 출력 차이가 실험 결과에 섞이는 것을 줄일 수 있다.

---

## 6. 평가 방법

### 6.1 주요 지표: Gold Source Recall@5

각 질문에 대해 Long Journey의 `recall()`을 실행하고 상위 5개의 Memory를 확인한다.

Depth-0 Memory는 직접 `source_ref`를 확인한다.

Higher-depth Memory는 `derived_from` provenance를 따라가 해당 Memory가 어떤 Source들에 기반하고 있는지 계산한다.

검색된 Memory의 Source ancestry 중 질문의 정답 근거 session이 포함되어 있으면 해당 질문에서 gold evidence가 recall된 것으로 기록한다.

예를 들어 다음과 같은 경우도 성공으로 본다.

```text
recall
  ↓
depth-2 memory
  ↓
depth-1 parents
  ↓
depth-0 memories
  ↓
gold evidence session
```

상위 abstraction 자체에 정답 표현이 직접 존재하지 않더라도 올바른 과거 경험으로 연결된다면 Long Journey의 retrieval 구조가 기능한 것으로 볼 수 있다.

전체 질문에 대해 이 비율을 계산하여 Remember Only와 Full Long Journey를 비교한다.

### 6.2 보조 지표: 최종 답변 정확도

각 조건에서 recall된 Memory를 동일한 answer model과 동일한 prompt에 제공한 뒤 LongMemEval의 평가 방식으로 답변 정확도를 측정한다.

이 값은 주요 판정 지표라기보다 retrieval 결과가 실제 답변 품질로 이어지는지를 확인하기 위한 보조 지표로 사용한다.

### 6.3 비용과 corpus 변화

두 조건에 대해 다음 값도 함께 기록한다.

- 총 API 비용
- recall당 입력 token 수
- depth별 Memory 수
- 생성된 relation 수
- Source당 depth-0 Memory 수
- Dream과 Meditation에서 생성된 Memory 수

이는 성능 개선이 발생하더라도 그 비용과 기억 구조의 변화가 어느 정도였는지 함께 판단하기 위한 자료다.

---

## 7. 통과 기준

첫 실험에서는 **Gold Source Recall@5**를 주요 성공 기준으로 사용한다.

Full Long Journey가 Remember Only보다 최소 **3 percentage points 이상 높은 Gold Source Recall@5**를 기록하면 consolidation이 retrieval에 실질적인 이점을 제공한다는 초기 근거로 판단한다.

동시에 특정 주요 question category에서 성능이 크게 악화되는 현상이 없는지도 확인한다. 한 category에서 Remember Only 대비 5 percentage points 이상 하락한다면 전체 평균이 좋아졌더라도 해당 실패 유형을 별도로 조사한다.

3 percentage points와 5 percentage points는 시스템의 영구적인 품질 기준이 아니라 첫 실험을 시작하기 전에 정해 두는 판정 기준이다. 결과를 확인한 뒤 유리한 방향으로 기준을 변경하지 않는다.

---

## 8. 결과 분석

실험 결과는 단순히 Full Long Journey의 점수가 높거나 낮다는 결론으로 끝내지 않는다.

성능이 기대보다 낮은 질문은 retrieval 과정에서 어디서 실패했는지를 구분한다.

### Candidate retrieval 실패

정답 Source에 연결되는 Memory가 BM25/embedding candidate에 처음부터 들어오지 않은 경우다.

이 경우 문제는 기억 consolidation보다 검색 단계에 있을 가능성이 높다. BM25, embedding retrieval, RRF candidate fusion과 candidate 수를 우선 조사한다.

### Recall selection 실패

정답 Source에 연결되는 Memory가 candidate에는 존재했지만 contextual selection 단계에서 선택되지 않은 경우다.

이 경우 Recall LLM이 candidate를 평가하는 방식과 candidate에 제공되는 정보가 주요 분석 대상이 된다.

### Consolidation으로 인한 악화

Remember Only에서는 올바른 Source를 회수했지만 Full Long Journey에서는 회수하지 못한 경우다.

이 경우 Dream 또는 Meditation에서 생성된 abstraction이 검색 결과를 과도하게 차지했는지, 지나치게 일반적인 Memory가 생성되었는지, relation이나 higher-depth Memory가 실제 검색에 어떤 영향을 주었는지를 memory graph와 provenance를 통해 확인한다.

반대로 Full Long Journey에서만 성공한 질문은 어떤 abstraction 또는 relation이 기존 depth-0 검색의 한계를 보완했는지 조사한다.

이 사례들이 Long Journey의 consolidation이 실제로 어떤 종류의 장기 기억 문제에서 도움이 되는지를 설명하는 핵심 자료가 된다.

---

## 9. 실험 결과 보고 형식

실험 완료 후 결과는 다음 형식으로 정리한다.

### Summary

| Metric | Remember Only | Full Long Journey | Difference |
| --- | ---: | ---: | ---: |
| Gold Source Recall@5 | | | |
| Answer Accuracy | | | |
| Total API Cost | | | |
| Avg. Recall Context Tokens | | | |

### Memory Morphology

| Metric | Remember Only | Full Long Journey |
| --- | ---: | ---: |
| Depth 0 Memories | | |
| Depth 1 Memories | | |
| Depth 2+ Memories | | |
| Positive Relations | | |
| Negative Relations | | |

### Failure Analysis

각 실패 질문을 다음 범주로 집계한다.

| Failure type | Count |
| --- | ---: |
| Candidate retrieval failure | |
| Recall selection failure | |
| Consolidation regression | |
| Answer-model failure | |

마지막으로 Full Long Journey에서 새롭게 성공한 사례와 반대로 악화된 사례를 각각 몇 개 선정하여 실제 Memory와 provenance를 함께 검토한다.

---

## 10. 예상되는 결론

이 실험으로 확인하려는 것은 Long Journey가 일반적인 memory benchmark에서 높은 점수를 얻는가 자체가 아니다.

핵심은 동일한 경험에서 출발했을 때 다음 과정이 실제로 유용한 정보를 만들어내는지를 확인하는 것이다.

```text
direct observations
        ↓
Daily Dream
        ↓
relations / abstractions
        ↓
Weekly Meditation
        ↓
higher-order memories
        ↓
better retrieval
```

Full Long Journey가 Remember Only보다 안정적으로 gold Source를 더 잘 회수한다면, 현재의 consolidation 구조를 유지하고 이후 실험에서 그 효과를 더 세분화할 근거가 생긴다.

차이가 없거나 성능이 악화된다면 실패 사례의 retrieval provenance를 분석하여 어느 단계가 이득을 만들지 못했는지를 찾고, 그 결과를 다음 설계 변경의 근거로 사용한다.