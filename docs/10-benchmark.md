# Consolidation benchmark

실험 요구사항은 [원문 제안서](../benchmarks/proposal.md)다. LongMemEval-S 전체 500문항에서 Remember Only와 Full Long Journey 두 조건만 비교한다. 파일럿, 샘플링, 파라미터 탐색, 추가 ablation은 실행하지 않는다.

## 고정된 실행 계약

- 시작 저장소: `d98e17b` (실행 준비 시 최신 `origin/main`).
- 데이터: 공식 [LongMemEval cleaned](https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned)의 `longmemeval_s_cleaned.json`, SHA-256 `d6f21ea9d60a0d56f34a05b609c79c88a451d2ae03597821ea3d5a9678c3a442`.
- 각 질문은 별도 corpus를 갖는다. 다른 질문의 대화나 기억이 섞이지 않는다.
- 한 session 전체가 한 Source다. 원문의 role/content, 시각, 중립적인 session 순번만 ingestion에 제공한다. 정답, 질문, question type, evidence 표시, 원본 session ID의 `answer_` 같은 이름은 ingestion payload에 포함하지 않는다.
- Source 내 여러 observation은 같은 Source root를 공유한다. Remember 결과와 embedding은 한 번 생성하여 보존하고 두 조건에서 ID·내용·시각·embedding을 공유한다.
- 입력 문자 한도는 데이터 전체 session의 실제 최대 길이를 수용한다. session adapter의 observation 수용 한도는 128, Remember 출력 한도는 16,384 token이다. 이 한도는 session을 발화별로 나누는 규칙이 아니며, Remember가 추출할 관찰을 결정한다. 한도 초과나 불완전 응답을 조용히 잘라서 평가하지 않는다.
- 모델과 검색 설정은 저장소 기본값이다. Remember `gpt-5.6-terra/low`, Recall `gpt-5.6-terra/medium`, Dream `gpt-5.6-terra/high`, Meditation `gpt-5.6-sol/high`, embedding `text-embedding-3-large/3072`.
- Dataset의 시간대 없는 날짜를 UTC로 해석한다. session을 시각 순으로 추가하며 각 자정에서 실제 `MemoryScheduler`를 실행한다. 질문 시각과 마지막 session 시각 중 늦은 시각까지 완료된 날짜와 7일 구간을 처리한다. 아직 끝나지 않은 질문 날짜를 미래로 진행시켜 consolidation하지 않는다.
- **Weekly Meditation 실행별 최대 비용은 $5**다. 실제 사용량과 미정산 최대 비용 예약을 합쳐 제한하는 기존 ledger를 사용한다. Dream에는 별도 금액 제한을 추가하지 않는다.
- Recall은 실제 `MemoryEngine.RecallAsync`를 사용하며 상위 최대 5개 Memory를 반환한다. candidate 목록은 기존 검색의 반환값을 관찰해서 저장한다. 검색 가중치나 selection 규칙을 변경하지 않는다.
- 답변 모델은 양쪽 모두 `gpt-5.6-terra/medium`, 동일한 prompt이며 선택된 최대 5개 Memory만 제공한다. ancestry의 원문을 답변 입력에 추가하지 않는다.
- 답변 채점은 [공식 evaluate_qa.py](https://github.com/xiaowu0162/LongMemEval/blob/9e0b455f4ef0e2ab8f2e582289761153549043fc/src/evaluation/evaluate_qa.py)의 category별 prompt, `gpt-4o-2024-08-06`, Chat Completions `temperature=0`, `max_tokens=10`, `yes` 판정 규칙을 사용한다.

## 판정과 비용

공식 파일의 76문항에서 1,475개 session 시각이 질문 시각보다 늦다(gold session 75개 포함). 모두 같은 달력 날짜 안의 순서 불일치다. 전체 history를 보존하기 위해 재생 종료 시각만 마지막 session까지 진행하며, recall/answer의 질문 날짜는 원래 annotation을 유지한다. 이 처리로 자정이나 consolidation 기간이 추가되지는 않는다. 반복 session ID도 별개의 제공된 session boundary로 보존하며 동일 gold session ID에 대한 Source 매핑은 여러 개일 수 있다.

제안서의 Gold Source Recall@5는 선택된 기억의 `derived_from`을 재귀적으로 추적한 Source 중 gold session이 하나라도 있는 질문의 비율이다. relation은 provenance root로 취급하지 않는다. 제안서의 전체 질문 기준에 맞춰 **500문항을 분모**로 고정한다. 이는 여러 gold 중 전부를 회수해야 하는 지표가 아니다. 공식 LongMemEval의 다른 retrieval 지표와 혼동하지 않는다.

전체 Recall@5 차이가 +3 percentage points 이상인지 확인한다. 각 question type에서 −5 percentage points 이상 하락하면 실패 사례를 검토한다. 미완료 실행은 부분 결과로 표시하며 통과 판정을 내리지 않는다.

조건별 비용에는 공통 ingestion 비용을 각각 귀속한다. 실제 지출 합계는 공통 ingestion을 한 번만 합산한다. `settled`는 API token 사용량에 [공식 단가](https://developers.openai.com/api/docs/pricing)를 적용한 비용이고, `reserved`는 사용량을 확인하지 못한 호출의 최대 비용이다. 계정 청구서 조회값은 아니다. Recall context token은 recall selection API의 실제 input token이다.

## 실행

저장소 루트에서 .NET 10.0.400을 사용한다. `key.txt` 또는 `OPENAI_API_KEY`에서 자격 증명을 읽으며 결과물에 복사하지 않는다.

```powershell
./.dotnet/dotnet.exe build LongJourney.slnx --configuration Release
./.dotnet/dotnet.exe test LongJourney.slnx --configuration Release --no-build
./.dotnet/dotnet.exe run --project src/LongJourney.Benchmarks --configuration Release --no-build -- validate benchmarks/longmemeval-s.json
./.dotnet/dotnet.exe run --project src/LongJourney.Benchmarks --configuration Release --no-build -- run benchmarks/longmemeval-s.json
```

`validate`는 유료 API를 호출하지 않는다. `run`은 전체 실험을 수행한다. 설정의 worker 수는 독립적인 질문 처리의 동시성만 조절한다. 각 질문 내부의 session과 consolidation은 순서대로 실행한다.

같은 명령으로 중단 지점에서 재개한다. 완료된 기억 추출, embedding, consolidation work/proposal, recall, answer, judge 결과를 재사용한다. 원인을 알 수 없는 실패에서 무제한 유료 재시도를 하지 않는다. 오류가 발생하면 새 작업 시작을 중단하고, 이미 지출했으나 사용량이 불확실한 호출은 예약으로 남긴다.

기본 결과 위치는 `data/benchmark/runs/proposal-2026-09-05/`다. `manifest.json`, `progress.json`, `report.md`, `metrics.json`, 질문별 `result.json`, Source 원문, 두 조건의 `memory.db`, API 사용량 JSONL과 단계별 결과를 보존한다. 결과에는 데이터셋 내용이 포함되므로 Git에 자동 추가하지 않는다. `report` 명령으로 저장된 완료 결과에서 보고서만 다시 생성할 수 있다.
