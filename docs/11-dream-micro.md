# Daily Dream retrieval micro benchmark

2026-09-05 실행의 수치, 문항별 결과, 해석 한계, 후속 수정 제안은 [마이크로 벤치마크 결과](12-dream-micro-results-2026-09-05.md)에 보존했다. 다른 세션에서 결과를 파악할 때는 이 문서를 먼저 읽는다.

[사용자 제안서](../benchmarks/dream-micro-proposal.md)를 구현한 진단 실험이다. LongMemEval 공식 점수나 전체 시스템 성능을 추정하지 않는다.

## 고정 프로토콜

- LongMemEval-S 전체 500문항의 checksum을 확인한 뒤 question_type을 ordinal 정렬한다. 유형별 question ID의 UTF-8 SHA-256 순서로 round-robin하여 8문항을 선택한다. 기존 조사 문항 네 개는 제안서대로 제외한다.
- 질문당 gold session을 모두 보존하고 최대 10개 session을 사용한다. 시간순 non-gold 목록 길이 n에서 k개를 고를 때 `floor(i*(n-1)/(k-1))` 위치를 택한다. k=1은 중앙 `floor((n-1)/2)`이다. 원래 timestamp, 동률 순서, raw를 유지한다. 모델로 표본을 고르지 않는다.
- session 하나는 Source 하나다. Remember를 조건마다 반복하지 않고 공유 D0의 ID, 내용, 시각, 모델, Source provenance, embedding을 보존한다. 최대 observation 수 128과 Remember 출력 한도 16,384는 기존 session ingestion adapter 설정을 따른다.
- A는 Remember Only, B는 Daily Dream이다. B는 공유 D0를 시간순으로 가져와 해당 날짜의 실제 작업이 있는 날만 제품 `DreamAsync`로 처리한다. 평가 cutoff는 `max(question_date,last_session_timestamp)`이며 닫히지 않은 마지막 날짜는 강제로 Dream하지 않는다. Meditation·별도 답변 생성·official judge는 호출하지 않는다.
- gold Source의 D0만 evaluator에 전달해 각 D0의 answer-bearing 여부와 짧은 이유를 한 번 판정한다. 입력은 question, gold answer, D0 ID·내용·시각으로 한정하며 결과는 Dream/Recall에 제공하지 않는다. 결과를 보고 label을 다시 생성하거나 표본을 교체하지 않는다.
- 주요 지표는 선택된 최대 5개 기억의 `derived_from` 조상에 answer-bearing D0가 하나라도 있는지다. Candidate hit, evidence coverage 비율, 전체 evidence 회수 여부도 기록한다. 같은 Source나 relation만 공유하는 것은 hit가 아니다. Ancestry hit 자체가 추상 기억 본문에 정답이 보존됐다는 뜻은 아니다.
- positive evidence가 0개인 문항도 분모에서 유지하고 두 조건의 retrieval 실패로 기록한다. `_abs` 문항은 별도 표시한다. 정보가 없다는 답은 명시적 D0 근거가 없을 수 있으므로 일반 추출 누락과 구분해 해석한다.

고정된 8문항은 `07741c45`, `720133ac`, `c4f10528`, `54026fce`, `c8c3f81d`, `gpt4_d31cdae3`, `b6019101`, `80ec1f4f_abs`다. 총 80개 session이다.

## 비용과 실행

모든 Remember, embedding, Dream assimilation/consolidation, Recall, evidence labeling은 실험 전역의 별도 SQLite 원장을 통과한다. 실제 정산액과 미정산 최대 예약액의 합에 다음 요청 최대액을 더한 값이 $20을 넘으면 HTTP 요청을 보내지 않는다. 정산은 알려진 실제 사용량을 반영하며 불명확한 요청의 예약은 해제하지 않는다. 미정산 요청이 남아 있으면 자동 재개를 거부한다. HTTP 실패 시 자동 재시도하지 않는다. 두 question worker가 같은 한도를 공유한다.

2026-09-05에 [Terra 공식 가격](https://developers.openai.com/api/docs/models/gpt-5.6-terra)과 [embedding 공식 가격](https://developers.openai.com/api/docs/models/text-embedding-3-large)을 확인했다. 설정된 Terra 가격은 입력/출력 1M tokens당 $2/$12, cached input $0.20, cache write $2.50이고, 272K 초과 입력에는 입력 2배·출력 1.5배를 적용한다. Embedding은 $0.13/1M input tokens다. 모델·추론 설정과 가격은 manifest에 고정한다.

```powershell
dotnet run --project src/LongJourney.Benchmarks -c Release -- micro-validate benchmarks/dream-micro.json
dotnet run --project src/LongJourney.Benchmarks -c Release -- micro-run benchmarks/dream-micro.json
```

유료 실행은 두 번째 명령만 수행한다. API 인증은 기존 key provider를 사용한다. 기본 출력은 `data/benchmark/runs/dream-micro-2026-09-05`이며 이전 대규모 benchmark의 corpus나 유료 결과를 재사용하지 않는다.

## 산출물과 판정

- `manifest.json`, `selection.json`, `implementation/`: 설정, 코드 hash·snapshot, 선택된 실제 입력.
- `budget/budget.db`, `global-api-calls.jsonl`: 실험 전체 비용. 공유 ingestion은 한 번만 합산한다.
- `questions/<id>/evidence.json`: 사람이 검토할 D0별 판정. 두 조건의 `recall.json`에는 후보·선택·전체 provenance·retrieval trace·input tokens가 있다.
- `questions/<id>/partial-corpus.json`: 예정 Source 수, 실제 등록 수, 미완료 수, morphology, pruning. 중단 시에도 저장한다.
- `report.md`, `metrics.json`, `status.json`: 완료된 paired 비교, 비용, pruning, morphology, 실제 기억과 ancestry. 미완료 문항을 실패로 채우지 않고 전체 실험을 incomplete로 표시한다.

완료된 8쌍에서 win > loss이며 선택된 Dream abstraction이 실제 gold D0 회수에 기여했는지 provenance를 확인한 경우에만 promising으로 해석한다. loss > win이면 regression, 그 외는 inconclusive다. 작은 표본의 진단 결과로 해석하며 전체 효용을 주장하지 않는다.
