# Phase 5 — Benchmark 설계와 실행

2026-09-04 사용자 위임에 따라 세부 조건을 정했다. 주간 Meditation은 실행당 **최대 5달러**다. 처리할 변경점이나 이월 작업이 없으면 API 호출 없이 종료한다. 금액을 채우기 위해 작업을 만들지 않는다.

## 실험 조건

| 항목 | 결정 |
| --- | --- |
| 데이터셋 | LongMemEval. 정식 비교 입력은 cleaned S, 첫 기능 확인은 oracle 한 문항 |
| 파일럿 | ceb54acb. 질문까지 일주일 이상인 짧은 문항으로 실행 흐름과 비용 확인 |
| 비교 | FullHistory, Remember, Dream, Relations, Meditation |
| 기하급수 제약 | 모든 기억 조건에서 RootBase=3, 부모 깊이 일치, 3^depth Source root 제약 강제. 해제 옵션 없음 |
| 입력 | user·assistant 발화를 문장/개행으로 나눔. 긴 구간은 분할. 날짜·role 헤더 포함 최대 1,000 UTF-16 문자 |
| 시계 | 시간 순서대로 처리. 시간대 없는 날짜는 UTC. 같은 세션 발화에는 임의의 초를 더하지 않음 |
| 질문 | question_date 이후 세션이 있으면 해당 문항 전체를 invalid_timeline으로 기록 |
| 격리 | 문항 × 조건마다 독립 저장소. 운영·화면 예제 저장소와 별도 |
| 답변/채점 | 각각 gpt-5.6-terra / medium / 최대 출력 4,096 토큰 |
| 전체 예산 | 첫 실험 10달러. 기억·임베딩·Dream·Meditation·답변·채점 포함 |
| 반복 | 처음은 1회. 다른 반복은 새 출력 폴더에서 별도 비용·그래프 보존 |

FullHistory는 날짜와 role이 붙은 원래 발화 전체를 제공한다. Remember는 depth 0만 생성한다. Dream은 consolidation만 수행한다. Relations는 assimilation을 더하고, Meditation은 주간 처리를 더한다. DreamAssimilationEnabled는 기본 true이고 Dream 비교 조건에서만 false로 지정한다.

질문당 Recall 한 번, 최대 10개 기억을 사용한다. 반환 순서대로 전체 항목을 담을 수 있을 때만 최대 32,000 문자까지 답변에 제공한다. FullHistory는 최대 1,000,000 문자를 허용하며 초과하면 오류로 중단한다. 과거 이력을 몰래 자르지 않는다. 이 한도는 직렬화된 문맥의 문자 수이며 실제 토큰 사용량을 별도로 기록한다. 모든 조건의 답변 프롬프트는 같고 외부 도구는 없다.

기존 인지 모델은 [API 사용 확정](04-openai-api.md)을 따른다. [OpenAI 공식 가격표](https://developers.openai.com/api/docs/pricing)를 2026-09-04 확인했다. Terra의 입력/캐시 읽기/캐시 쓰기/출력은 100만 토큰당 2/0.2/2.5/12달러, Sol은 4/0.4/5/20달러다. 설정 단가와 실제 응답 모델을 보존한다.

## 시간과 근거

- 다음 관찰을 넣기 전에 지나간 자정마다 시계를 맞추고 Scheduler를 실행한다. 완료된 7일 뒤에 Meditation을 실행한다.
- 질문 날짜의 아직 끝나지 않은 하루를 강제로 Dream 처리하지 않는다.
- 관찰이 실패하면 같은 시각에서 remember를 재시도한다. 완료된 Source는 기존 중복 처리로 재사용한다.
- 날짜 경계에서 실패하면 그 경계의 미완료 실행부터 재개한다. 기존 sequence 상한과 저장된 제안을 유지한다.
- 답변은 채점 전에 저장한다. 채점 재시도는 저장된 답변을 사용한다. 응답 수신과 체크포인트 사이의 프로세스 종료로 호출이 반복될 가능성까지 없애지는 못한다.
- 질문 이전에 인위적인 recall을 만들지 않는다. 따라서 과거 질문의 recall이 이후 Dream에 미치는 누적 효과는 이 프로토콜에서 측정하지 않는다.
- depth 0 날짜는 원문 수신 시각이다. 추상 기억의 Dream/Meditation 생성 시각을 사건 날짜로 전달하지 않는다. 본문에 날짜가 없으면 알 수 없는 날짜로 둔다.

공식 oracle 오프라인 검사에서 500개 중 43개 문항에 질문 이후의 세션 시각이 있었다. 날짜를 고치거나 이력을 자르지 않고 invalid_timeline으로 기록한다. 선택한 문항의 이런 오류는 plan에서도 확인한다.

question, answer, question_type, has_answer, answer_session_ids를 기억 입력에 섞지 않는다. 질문·날짜만 Recall/답변에 전달하고 정답·유형·abstention 여부는 채점에만 전달한다. session ID에 정답 위치를 드러내는 이름이 있을 수 있으므로 답변의 발화 ID는 중립적인 순번을 쓴다. 원래 ID는 평가용 매핑에 보존한다.

조각 하나가 Source root 하나다. 동일 세션의 세 조각도 세 root이므로 root 수를 독립 경험 수와 동일시하지 않는다. 추상 기억마다 Source root 수와 원래 세션 수를 함께 기록한다.

## 비용과 재개

5달러는 Meditation 한 번의 상한이다. 다음 요청의 최대 비용을 예약할 수 없으면 남은 작업을 이월한다. Dream 자체에는 금액 제한을 추가하지 않는다. 별도의 전체 실험 상한에 도달하면 실험을 중단하고 상태를 보존한다.

각 corpus의 api_calls가 유일한 비용 원장이다. 사용량이 확인되지 않은 요청은 예약액을 유지한다. 재시작은 비용을 초기화하지 않는다. 전체 실험과 모든 corpus를 잠근 동안 합산하며, 예약·정산 때는 현재 corpus만 다시 읽는다. 비용은 설정 단가로 계산한 값이며 OpenAI 청구서 그 자체는 아니다.

manifest에는 원본 SHA-256, 선택 문항, 모델·단가·예산·문맥 한도, 프로토콜/프롬프트 버전, 구현 바이너리 식별자가 고정된다. 같은 조건과 구현에서만 재개한다. 변경된 실험은 새 폴더를 사용한다. 기존 manifest만 삭제해서 새 예산을 부여하는 방식은 거부한다.

입력 순서가 같아도 LLM 출력과 기억 GUID가 달라질 수 있다. 조건별 기억 생성은 독립 실행이며 같은 D0 결과를 공유하지 않는다. 동일 그래프의 재현이나 완전히 통제된 인과 비교를 보장하지 않는다.

## 실행

저장소 루트에서 실행한다. 실제 키는 key.txt 또는 OPENAI_API_KEY를 사용하며 설정·결과에 저장하지 않는다.

~~~powershell
New-Item -ItemType Directory -Force TestResults/benchmark-input
Invoke-WebRequest https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/resolve/main/longmemeval_oracle.json -OutFile TestResults/benchmark-input/longmemeval_oracle.json

# 입력·문항·시각·예산 확인. API 호출 없음.
dotnet run --project src/LongJourney.Benchmarks -- plan benchmarks/pilot.json

# 실제 실행. 같은 명령으로 재개.
dotnet run --project src/LongJourney.Benchmarks -- run benchmarks/pilot.json

# 저장 결과 요약. API 호출 없음.
dotnet run --project src/LongJourney.Benchmarks -- report benchmarks/pilot.json
~~~

설정의 경로는 해당 JSON 파일 기준이다. question_ids가 있으면 그 문항을 사용한다. 없으면 ID를 ordinal 정렬한 앞의 limit개를 선택한다. 무작위 또는 유형별 균형 표본이라는 의미는 아니다. split은 선언된 데이터 구분이며 공식 출처 인증이 아니다. 실제 입력은 SHA-256으로 식별한다.

S는 [longmemeval-s.json](../benchmarks/longmemeval-s.json)을 사용한다. cleaned S 파일을 지정 위치에 내려받고 plan을 확인한다. 500문항 × 다섯 조건은 대규모 유료 작업이다. 예제 전체 상한은 10달러이므로 일부만 완료될 수 있다.

## 결과

- manifest.json: 고정 조건과 저장소 목록.
- report.json: 계획/완료 개수, 실제/미정산 비용, 유형별 점수, 공통 완료 문항 점수.
- 각 문항/조건의 result.json: 상태·오류 종류·답변·판정·모델·비용·실행 시간.
- evidence.json: 답변에 제공한 기억/발화.
- graph.json, runs.json, source-sessions.json, corpus/: 그래프·실행·Source 매핑·SQLite·원문.
- hypotheses-조건.jsonl: 공식 evaluator용 question_id/hypothesis 형식.

조건 비교에는 모든 선택 조건이 완료된 공통 문항만 쓴다. 독립 점수와 완료율도 함께 제공한다. 실패·예산 중단·invalid_timeline을 오답으로 바꾸지 않는다. 정답 근거가 없는 abstention의 retrieval coverage는 null이다.

retrieval coverage는 답변에 들어간 기억의 provenance에서 찾은 정답 세션 비율이다. 추상 기억의 전체 root를 따라가므로 정답 문장을 실제 보존했는지까지 보장하지 않는다. depth 분포, 방향별 관계 수, root/세션 support, 거절 제안, 문자열이 동일한 추상 기억, 실행 시간과 비용도 기록한다.

의미상 중복·과도한 일반화·인과 환각은 root 검사나 문자열 일치로 판정할 수 없다. 보존된 그래프와 Source를 대조하여 정성 리뷰한다. 자동 채점이나 invariant 통과를 의미 품질의 증명으로 해석하지 않는다.

한 문항 oracle은 실행 경로 확인이다. 정답 근거 세션만 제공하므로 S 검색 성능을 대표하지 않는다. 내부 채점은 [공식 evaluator](https://github.com/xiaowu0162/LongMemEval/blob/main/src/evaluation/evaluate_qa.py)의 유형별 규칙을 참고하지만 모델·요청 형식이 다르므로 공식 leaderboard 점수로 보고하지 않는다. 입력 형식과 공식 실험 방식은 [LongMemEval 저장소](https://github.com/xiaowu0162/LongMemEval)를 참고한다.


첫 실제 실행의 비용과 정성 검토는 [파일럿 결과](11-benchmark-pilot.md)에 기록했다.
