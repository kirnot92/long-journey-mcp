# Daily Dream retrieval micro benchmark 결과 — 2026-09-05

8문항·80 Source를 모두 실행했다. **결과는 Inconclusive**다. 두 조건 모두 Gold Evidence Recall@5가 6/8이며 Dream win과 loss는 각각 0개다. 이번 축소 표본에서는 Dream의 retrieval 이득을 확인하지 못했다.


## 다른 세션에서 읽을 때

이 문서는 2026-09-05 실제 API 실행 결과를 보존한 세션 인계용 기록이다. 대화 이력이나 로컬 원본 산출물이 없어도 결과와 한계, 다음 수정 후보를 이해할 수 있도록 작성했다.

- 실행 완료: 2026-09-05 12:01 KST. 상태 complete, 8/8문항 완료.
- 조건 A: Remember Only. 조건 B: 동일 D0에서 출발한 Daily Dream.
- 질문당 최대 10개 session, session 하나당 Source 하나. 각 질문의 gold session은 모두 보존했다.
- 유형별 ID SHA-256 정렬과 유형 round-robin으로 질문을 선택하고, 시간순으로 균등한 non-gold distractor를 택했다. 기존 조사 문항 네 개는 제외했다.
- 주요 지표: 최종 선택된 최대 5개 기억 자체 또는 derived_from 조상에 answer-bearing gold D0가 하나라도 있으면 hit. Source만 같거나 relation으로만 연결된 경우는 hit가 아니다.
- 같은 Source·D0 ID·내용·시각·provenance·embedding을 두 조건에서 공유했다. 평가 label은 Dream이나 Recall에 제공하지 않았다.
- 모델: gpt-5.6-terra. Remember low, Recall medium, Dream high, evidence 판정 medium. Embedding은 text-embedding-3-large, 3,072차원.
- [실험 프로토콜](11-dream-micro.md), [사용자 원래 제안서](../benchmarks/dream-micro-proposal.md).
- 원본 산출물은 `data/benchmark/runs/dream-micro-2026-09-05/`에 있다. `data/`는 Git 제외 경로이므로 다른 checkout에는 없을 수 있다. 핵심 수치와 해석은 이 문서 안에 포함했다.
- 기반 제품 commit: `fe28d0d`. benchmark 실행기까지 포함한 실제 코드 식별자는 아래 implementation hash다.
- 실행 구현 SHA-256: `36a23c2f81e0af4fc2f8e442646b9f6f3732ae1bcac25729430fa9069b202061`.
- Dataset SHA-256: `d6f21ea9d60a0d56f34a05b609c79c88a451d2ae03597821ea3d5a9678c3a442`.

## 주요 결과

| 지표 | Remember Only | Daily Dream |
| --- | ---: | ---: |
| Gold Evidence Recall@5 | 6/8 (75%) | 6/8 (75%) |
| Gold Evidence Candidate Hit | 6/8 (75%) | 6/8 (75%) |
| Recall input tokens | 34,155 | 38,663 |
| D0 | 415 | 415 |
| D1 / D2+ | 0 / 0 | 19 / 0 |
| Positive / negative relations | 0 / 0 | 366 / 3 |
| 최종 선택된 higher memory | 0 | 0 |

Dream win 0, Dream loss 0, tie-success 6, tie-failure 2다. Win·loss 사례가 없으므로 해당 사례의 provenance도 없다. 모든 문항의 실제 후보·선택 기억·부모 ID·gold D0 경로는 [상세 보고서](../data/benchmark/runs/dream-micro-2026-09-05/report.md)에 보존했다.

Dream은 6문항에서 총 50개의 닫힌 활성 날짜에 실행됐다. `gpt4_d31cdae3`와 `80ec1f4f_abs`는 전체 세션이 질문 당일에 있어 닫힌 날짜가 없었다. 두 문항에서는 Dream 자체가 실행되지 않았다. 사전에 고정한 시간 규칙을 유지하고 다음 자정으로 강제 진행하지 않았다.


## 문항별 결과

Hit은 위에서 정의한 any-evidence 기준이다. 두 조건의 coverage도 모든 문항에서 동일했다.

| 질문 ID | 유형 | A hit | B hit | A/B evidence coverage | Dream 생성 D1 | 비고 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| 07741c45 | knowledge-update | 실패 | 실패 | 0 | 5 | 현재 상태 D0 근거 누락·원문 모호함 |
| 720133ac | multi-session | 성공 | 성공 | 1 | 0 |  |
| c4f10528 | single-session-assistant | 성공 | 성공 | 1 | 0 |  |
| 54026fce | single-session-preference | 성공 | 성공 | 0.625 | 3 | 선호 정보 8개 중 5개, 필수 사실 8개라는 뜻은 아님 |
| c8c3f81d | single-session-user | 성공 | 성공 | 1 | 4 |  |
| gpt4_d31cdae3 | temporal-reasoning | 성공 | 성공 | 1 | 0 | 닫힌 날짜 없음: Dream 미실행 |
| b6019101 | knowledge-update | 성공 | 성공 | 1 | 7 |  |
| 80ec1f4f_abs | multi-session | 실패 | 실패 | 0 | 0 | 정보 부재 문항, positive D0 없음; Dream 미실행 |

## 호출 생략과 생성량

| 항목 | 수 |
| --- | ---: |
| Consolidation work | 277 |
| 불가능 조건으로 LLM 전에 생략 | 18 |
| 정확히 동일한 neighborhood로 생략 | 105 |
| 실제 consolidation LLM 호출 | 154 |
| 빈 abstraction 응답 | 134 |
| 비어 있지 않은 응답 | 20 |
| Core 검증에서 거절 | 1 |
| 생성된 abstraction | 19 |

123/277개 작업(44.4%)이 두 생략 규칙으로 LLM 호출 전에 처리됐다. 거절된 1건은 모델에 주어진 후보 밖의 부모 ID를 사용했다. 저장 전 검증으로 차단됐으며, 임베딩·기억 생성으로 이어지지 않았다. 실제 호출 154개 중 134개(87.0%)는 빈 결과였다. D1/D0 비율은 19/415, 약 4.58%다. 이전 대규모 실험과 입력 규모가 다르므로 이 비율이나 비용을 이전 실행의 직접 비교치로 해석하지 않는다.

생성된 higher memory 중 18개가 각 문항의 Recall 후보에 포함됐지만 최종 선택된 higher memory는 0개였다. 두 조건의 evidence coverage도 문항별로 동일했다. Recall input tokens는 13.2% 증가했다.

## 실제 비용

| 작업 | API 호출 | 정산 USD |
| --- | ---: | ---: |
| Remember | 80 | 0.709596 |
| Embedding | 727 | 0.00291629 |
| Assimilation | 277 | 2.849010 |
| Consolidation | 154 | 1.438138 |
| Recall | 16 | 0.157288 |
| Evidence labeling | 8 | 0.067674 |
| 합계 | 1,262 | **5.22462229** |

미정산 예약액은 $0이다. 공통 D0 ingestion은 한 번만 합산했다. 전역 원장 합계와 모든 문항·조건별 원장 합계가 정확히 일치한다. $20 hard cap 안에서 전체 실행을 완료했다. Meditation, answer generation, official judge 호출은 없다.

## 근거 판정과 해석

근거가 존재하는 6문항은 Remember Only가 이미 전부 성공했다. 따라서 이번 10-session 표본에는 주요 지표에서 Dream이 개선할 여지가 없었다. 이 결과만으로 더 큰 실험에 비용을 투입할 근거를 얻지는 못했으며, 표본의 높은 baseline 성능 때문에 Dream의 효과를 구분하는 데도 한계가 있다.

- `07741c45`: 현재 신발 보관 위치를 묻지만 D0에는 이전 위치와 향후 신발장 보관 계획이 남았다. 최신 gold 원문에는 현재 상태와 미래 계획이 섞여 있어, 근거 누락과 원문 모호함을 함께 기록했다. Positive D0가 0개이므로 두 조건 모두 실패다.
- `80ec1f4f_abs`: “12월 박물관 방문을 언급하지 않았다”는 답이다. 정보의 부재를 positive D0로 만들지 않는 고정 규칙 때문에 positive가 0개다. 스키마의 `RememberExtractionFailure` 플래그는 여기서 ‘긍정 근거 없음’을 뜻하며, 실제 존재하는 사실을 Remember가 놓쳤다는 의미는 아니다.
- `54026fce`: positive 8개는 서로 겹치는 선호 정보다. 두 조건 모두 5/8을 선택했다. 이 coverage를 8개의 필수 사실 중 회수 비율이나 답변 완성도로 해석하지 않는다.
- 여러 근거가 필요한 문항의 primary hit는 하나의 positive D0만 회수해도 성립하는 정의다. 이번에는 선호 문항을 제외한 나머지 positive 문항에서 두 조건 모두 전체 labeled D0를 회수했다.

8문항의 label 입력과 판단을 모두 점검했고 명백한 labeling 오류는 발견하지 못했다. 사후 재판정·label 수정·문항 교체·추가 유료 평가를 하지 않았다. 감사 기록: [첫 두 문항](../data/benchmark/runs/dream-micro-2026-09-05/audit/first-two.md), [중간 세 문항](../data/benchmark/runs/dream-micro-2026-09-05/audit/middle-three.md), [마지막 세 문항](../data/benchmark/runs/dream-micro-2026-09-05/audit/last-three.md).

## 재현성과 검증

Source ID·원문 hash·시각·상태, D0 ID·내용·시각·모델·Source provenance, embedding 값이 두 조건에서 동일함을 8개 corpus 모두 확인했다. 실행 중 사용한 코드 snapshot과 현재 구현의 SHA-256도 일치한다. 전체 자동 테스트 205개와 서식 검사가 통과했다.

- [프로토콜·코드 manifest](../data/benchmark/runs/dream-micro-2026-09-05/manifest.json)
- [전체 지표와 문항별 결과](../data/benchmark/runs/dream-micro-2026-09-05/metrics.json)
- [전역 API 원장 export](../data/benchmark/runs/dream-micro-2026-09-05/global-api-calls.jsonl)
- [검증 기록](../data/benchmark/runs/dream-micro-2026-09-05/verification.json)

이 결과는 축소 history의 진단 결과이며 LongMemEval 공식 성적이나 전체 시스템 성능의 증명이 아니다.

## 결과에 따른 수정 제안 — 아직 미구현

다음은 결과를 본 뒤 제안한 후속 작업이며, 이번 실험에 적용된 변경이나 효과가 입증된 수정이 아니다. 원래 결과를 고쳐 쓰거나 같은 실험의 label을 재판정하지 않는다.

1. **D0 정보 보존부터 수정:** 현재 상태·완료된 행동·미래 계획을 구분하고, 같은 Source의 모순된 발언을 하나로 합치면서 지우지 않도록 Remember 프롬프트와 회귀 테스트를 보완한다. 첫 대상으로 신발 보관 위치 사례를 사용한다. Source 분할이나 root 수 증가는 하지 않는다.
2. **평가 구분력 보완:** abstention과 답변 가능한 retrieval 문항을 별도 집계하고, 실제 Dream 실행 여부를 기록한다. 새 실험에서는 사전에 정한 규칙으로 distractor를 늘려 baseline 천장 효과를 줄이고, 여러 사실이 필요한 질문은 필수 사실 단위 회수율을 측정한다. 기존 8문항의 고정 결과는 유지한다.
3. **그래프를 이용한 후보 확장 실험:** 현재 MemorySearch의 후보 생성은 lexical·embedding 순위 결합이며 부모·relation을 따라 후보를 확장하지 않는다. 검색된 D1의 derived_from 조상 D0와 검색된 기억의 outgoing relation 대상을 제한적으로 후보에 추가하는 두 방식을 각각 비교한다. 확장 수·최종 후보 예산·D0 후보가 밀려나는 현상을 확인한다. 이 효과는 아직 가설이다.
4. **Assimilation 비용 최적화 실험:** 전체 비용에서 가장 큰 $2.85를 차지했다. Assimilation과 Consolidation의 추론 설정을 분리하고, 동일 입력에서 Assimilation high와 medium의 관계 누락·잘못된 관계·최종 retrieval을 비교한 뒤 기본값을 판단한다. 아직 설정을 낮추지 않았다.

한 가지를 먼저 한다면 1번이 우선이다. D0에서 사라진 사실은 이후 Dream이 복구하기 어렵다. 빈 consolidation 응답 비율만으로 생성 기준을 완화하거나, 선택률을 올리려고 D1을 강제로 선택하도록 바꾸지는 않는다.
