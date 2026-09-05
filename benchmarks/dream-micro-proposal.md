# 저비용 Dream Retrieval 마이크로 벤치마크 제안서

## 1. 실험 목적

이번 실험은 Long Journey 전체의 최종 성능을 입증하는 것을 목적으로 하지 않는다.

질문은 하나로 제한한다.

> 동일한 depth-0 기억에서 출발했을 때, Daily Dream이 만든 relation과 abstraction이 depth-0만 사용하는 경우보다 실제 정답 근거를 더 잘 회수하게 만드는가?

Weekly Meditation은 이번 실험에서 제외한다.

현재 비용의 대부분이 Daily Dream의 Assimilation과 Consolidation에서 발생하고 있으며, 최근 수정 역시 Dream의 호출과 생성량을 더 보수적으로 만드는 데 집중되어 있다.

따라서 먼저 Dream 자체가 retrieval에 이득을 주는지 작은 표본에서 확인한다.

이 실험은 LongMemEval 공식 점수를 산출하기 위한 것이 아니라, 더 큰 실험에 비용을 투입할 가치가 있는지를 판단하기 위한 diagnostic benchmark다.

---

## 2. 실험 규모

LongMemEval-S에서 **8개 질문만 사용한다.**

기존에 조사하거나 부분 실행했던 다음 질문은 제외한다.

```text
58bf7951
51a45a95
e47becba
118b2229
```

질문 선택은 `question_type`이 한쪽에 몰리지 않도록 한다.

선택 과정에서 모델의 의미 판단은 사용하지 않는다.

각 `question_type` 안에서 question ID의 SHA-256 값을 기준으로 정렬한 뒤, category를 round-robin하면서 총 8개를 선택한다.

따라서 같은 dataset에서는 항상 같은 질문이 선택된다.

---

## 3. 질문당 history 축소

원래 LongMemEval-S의 전체 history를 재생하지 않는다.

각 질문당 최대 **10개 session**만 사용한다.

정답에 필요한 모든 `answer_session_ids`는 반드시 포함한다.

나머지는 non-gold session 중에서 시간 순서 전체에 고르게 퍼지도록 deterministic하게 선택한다.

예를 들어 gold session이 하나라면:

```text
Gold session 1개
+
Non-gold distractor 9개
=
총 10 Source
```

가 된다.

Distractor를 고를 때 질문 내용, 답변 내용, embedding similarity는 사용하지 않는다.

시간순으로 정렬된 non-gold history에서 균등한 위치를 선택한다.

선택된 session의 원래 timestamp와 순서는 그대로 보존한다.

따라서 최대 규모는 대략:

```text
8 questions × 10 Sources
= 최대 80 Sources
```

이다.

이 축소 dataset은 원래 LongMemEval-S와 난이도가 동일하다고 주장하지 않는다.

목적은 두 memory condition을 동일한 작은 history에서 비교하는 것이다.

---

## 4. Source와 depth-0 생성

LongMemEval session 하나를 Source 하나로 취급하는 기존 원칙은 유지한다.

```text
LongMemEval session
        ↓
      Source
        ↓
0..N depth-0 Memories
```

개별 turn이나 문장을 별도 Source로 쪼개지 않는다.

각 선택된 Source에 대해 Remember cognition은 **한 번만 실행한다.**

생성된 다음 값은 저장하고 두 조건에서 그대로 공유한다.

```text
Source
depth-0 Memory ID
depth-0 content
timestamp
embedding
source provenance
```

따라서 두 조건은 완전히 동일한 depth-0 corpus에서 출발한다.

Remember를 조건별로 다시 호출하지 않는다.

---

## 5. 비교 조건

두 조건만 사용한다.

### Condition A — Remember Only

공유된 depth-0 Memory만 사용한다.

Dream과 Meditation은 실행하지 않는다.

그 상태에서 질문에 대해 Recall을 수행한다.

### Condition B — Daily Dream

Condition A와 정확히 같은 depth-0 corpus에서 시작한다.

Dataset timestamp를 기준으로 Daily Dream을 수행한다.

선택된 history에서 실제 Dream work가 존재하는 날짜에 대해서만 Dream을 실행한다.

Daily Dream에는 현재 제품 코드의 실제 동작을 그대로 사용한다.

이번 실험을 위해 Core invariant를 끄거나 별도의 benchmark용 Dream 규칙을 만들지 않는다.

Weekly Meditation은 실행하지 않는다.

---

## 6. 주요 지표: Gold Evidence Recall@5

기존의 `Gold Source Recall@5` 대신 **Gold Evidence Recall@5**를 사용한다.

Source 전체가 아니라 실제 답을 담고 있는 depth-0 Memory를 gold evidence로 삼는다.

먼저 공유 depth-0 extraction이 끝난 뒤, 각 질문의 gold Source에서 생성된 D0들을 대상으로 한 번만 evidence 판정을 수행한다.

판정 입력은 다음으로 제한한다.

```text
question
gold answer
gold Source에서 나온 depth-0 Memories
```

판정 결과는:

> 어떤 D0가 이 질문에 답하는 데 필요한 사실을 실제로 담고 있는가?

이다.

이 단계는 benchmark evaluator일 뿐이며, 결과를 Dream이나 Recall cognition에 제공하지 않는다.

8개 질문뿐이므로 결과는 사람이 함께 확인할 수 있는 형태로 저장한다.

어떤 gold Source에서도 answer-bearing D0가 만들어지지 않았다면 해당 질문을 제외하지 않는다.

이를 `Remember extraction failure`로 기록하고 두 조건 모두 retrieval 실패로 처리한다.

---

## 7. Higher Memory의 성공 판정

Recall된 Memory가 depth-0라면 해당 Memory 자체가 gold evidence인지 확인한다.

Higher-depth Memory라면 `derived_from`을 재귀적으로 따라간다.

그 ancestry 안에 answer-bearing gold D0가 있을 때만 hit로 인정한다.

예:

```text
Question
   ↓
Recall된 D1
   ↓
parents
   ├─ D0 A
   ├─ D0 B  ← answer-bearing gold D0
   └─ D0 C

→ hit
```

반면 같은 gold session에서 왔더라도 정답과 관계없는 D0만 ancestry에 포함되어 있다면 실패다.

```text
Gold Source
 ├─ D0: 이탈리아 식당
 ├─ D0: 연기 수업
 └─ D0: The Glass Menagerie ← 실제 정답 근거

Recall된 abstraction ancestry
→ 이탈리아 식당 D0만 포함

→ miss
```

이렇게 하여 session 단위 provenance가 너무 넓어서 생기던 false hit를 피한다.

---

## 8. 함께 기록할 retrieval 지표

각 질문에서 다음을 기록한다.

```text
Gold Evidence Recall@5
Gold Evidence candidate hit 여부
Recall된 Memory의 depth
Recall된 Memory의 gold D0 ancestry
```

Candidate에는 gold evidence가 있었지만 최종 Recall에서 빠졌다면 Recall selection 문제로 구분한다.

Candidate에도 없었다면 candidate retrieval 문제로 구분한다.

추가 모델 호출 없이 기존 retrieval trace만으로 기록한다.

---

## 9. 답변 생성 평가는 기본적으로 하지 않는다

이번 실험의 목적은 Dream의 retrieval 효과를 확인하는 것이다.

따라서 모든 질문에 대해 별도의 answer generation과 official judge를 실행하지 않는다.

두 조건의 retrieval 결과가 다른 질문에 대해서만 필요하면 정성 분석용 답변을 생성할 수 있으나, 이것은 benchmark 주요 점수에는 포함하지 않는다.

이렇게 하면 answer model과 judge 비용을 크게 줄일 수 있다.

---

## 10. 비용과 corpus 기록

두 조건에 대해 다음 값을 기록한다.

```text
Remember API cost
Embedding API cost
Assimilation calls / cost
Consolidation calls / cost

D0 count
D1 count
D2+ count

Positive relations
Negative relations

Dream-created Memory count
D1 / D0 ratio

Recall candidate count
Recall input tokens
```

특히 최근 Dream 수정의 효과를 보기 위해 다음 값은 반드시 남긴다.

```text
Consolidation work 수
성립 불가능하여 LLM 전에 제거된 work 수
exact neighborhood dedup으로 제거된 work 수
실제 Consolidation LLM 호출 수
0 abstraction으로 끝난 호출 수
생성된 abstraction 수
```

이를 통해 단순히 API 비용이 줄었는지가 아니라 어느 규칙 때문에 줄었는지 확인한다.

---

## 11. 비용 제한

이번 benchmark의 **전체 API 비용 hard cap은 $20**으로 한다.

이 금액에는 다음을 모두 포함한다.

```text
Remember
Embedding
Dream Assimilation
Dream Consolidation
Recall
Gold D0 evidence 판정
```

$20에 도달하면 추가 모델 호출을 시작하지 않고 실행을 중단한다.

중단된 경우 결과를 성공이나 실패로 판정하지 않고 incomplete로 기록한다.

개별 Meditation budget 설정은 이번 실험에서는 사용하지 않는다. Meditation 자체를 실행하지 않기 때문이다.

이전 실행의 Source당 비용을 단순 비례시키면 최대 80 Source 규모는 대략 $10대 수준이 예상되지만, 이는 보장된 예산 추정치가 아니다. 실제 보호 장치는 예상치가 아니라 $20 hard cap이다.

---

## 12. 결과 판정

8개 질문은 통계적으로 Long Journey 전체 성능을 입증하기에는 너무 작다.

따라서 기존의 `+3 percentage points` 같은 전체 benchmark 통과 기준은 사용하지 않는다.

각 질문을 paired comparison으로 본다.

```text
Dream win:
Remember Only miss
Dream hit

Dream loss:
Remember Only hit
Dream miss

Tie-success:
둘 다 hit

Tie-failure:
둘 다 miss
```

최종 결과는 다음처럼 보고한다.

```text
Dream wins: N
Dream losses: N
Tie-success: N
Tie-failure: N
```

해석은 다음과 같이 제한한다.

### Promising

Dream win이 Dream loss보다 많고, 실제 win의 provenance를 확인했을 때 Dream-created abstraction이 answer-bearing D0를 회수하는 데 기여했다.

### Regression

Dream loss가 Dream win보다 많다.

특히 higher memory가 candidate 자리를 차지하면서 기존 D0 retrieval을 밀어낸 사례가 있는지 조사한다.

### Inconclusive

win과 loss가 없거나 거의 동일하다.

작은 표본에서는 Dream의 retrieval 이점을 확인하지 못한 것으로 기록한다.

이 결과만으로 Long Journey 전체의 효용을 주장하지 않는다.

---

## 13. 결과 보고 형식

### Retrieval

| Metric                      | Remember Only | Daily Dream |
| --------------------------- | ------------: | ----------: |
| Gold Evidence Recall@5      |               |             |
| Gold Evidence Candidate Hit |               |             |
| Recall input tokens         |               |             |

### Paired Result

| Result      | Count |
| ----------- | ----: |
| Dream win   |       |
| Dream loss  |       |
| Tie-success |       |
| Tie-failure |       |

### Dream Cost

| Operation         | Calls | Cost |
| ----------------- | ----: | ---: |
| Remember          |       |      |
| Embedding         |       |      |
| Assimilation      |       |      |
| Consolidation     |       |      |
| Recall            |       |      |
| Evidence labeling |       |      |
| Total             |       |      |

### Dream Pruning

| Item                         | Count |
| ---------------------------- | ----: |
| Consolidation work           |       |
| Impossible before LLM        |       |
| Exact duplicate neighborhood |       |
| Actual LLM calls             |       |
| Zero-abstraction calls       |       |
| Created abstractions         |       |

### Memory Morphology

| Metric             | Value |
| ------------------ | ----: |
| D0                 |       |
| D1                 |       |
| D2+                |       |
| Positive relations |       |
| Negative relations |       |

마지막에는 Dream win과 Dream loss 각각의 실제 Memory, `derived_from`, answer-bearing D0 provenance를 직접 보여준다.

---

## 14. 이 실험이 답하려는 것

이 benchmark가 답하려는 것은 다음 하나다.

> 비용을 줄이고 더 보수적으로 수정한 Daily Dream이, 작은 실제 장기-memory history에서 depth-0 retrieval보다 나은 사례를 실제로 만들어내는가?

8개 질문에서도 Dream이 retrieval win을 만들지 못하거나 오히려 regression만 만든다면, 더 큰 Full Long Journey benchmark에 비용을 투입할 근거가 약하다.

반대로 작은 규모에서도 Dream-created Memory가 실제 answer-bearing evidence를 회수하는 명확한 사례가 나온다면, 그 사례를 provenance 수준에서 확인할 수 있다.

이번 benchmark는 그 판단에 필요한 최소한의 비용만 사용한다.
