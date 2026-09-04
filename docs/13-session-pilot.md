# 세션 전체 입력과 실제 observation 검증

실행일: 2026-09-05. 프로토콜: `longmemeval-v3-session`.

문장 분할과 개별 발화 입력은 실험 설계 오류였다. 대화 맥락은 입력에 보존하고, 어떤 사건이나 교환을 하나의 기억으로 만들지는 전체 대화를 읽은 모델이 판단해야 한다. 입력 단위를 바꾼 뒤에도 실제 출력의 순서·귀속·조건을 읽어 확인했다.

## 최종 입력과 추출 방식

- 날짜, 역할, 원래 발화 순서와 본문을 유지한 **세션 전체를 remember(raw) 한 번에 전달**한다. 입력을 문장이나 발화로 나누거나 절삭하지 않는다.
- raw 하나는 immutable Source 하나다. 모델이 복수 observation을 추출해도 Source root는 하나다. B=3 기하급수 제약은 항상 적용한다.
- observation은 독립적으로 이해할 수 있는 사건이나 대화 교환이다. 질문·답변·수정·현재의 결정 상태를 필요한 맥락과 함께 묶는다. 다른 기억의 “그 물건”, “앞서 말한 가게”에 의존하는 발화별 재서술을 목표로 삼지 않는다.
- 화자 귀속, 제안과 수용, 잠정 계획과 구매, 보고일과 사건일을 구분한다. 보이지 않는 앞 대화에 대한 반응을 나중 제안에 연결하지 않는다.
- 입력에 question, answer, has_answer, 평가용 session ID를 섞지 않는다. 원래 세션 ID는 Source 매핑에만 남긴다.
- benchmark 기본 한도는 raw 64,000 UTF-16 문자와 observation 32개다. Remember 출력은 이번 설정에서 8,192토큰이다. 상한은 출력 목표가 아니며 운영 엔진 기본값과 구분한다.
- 완전한 세션이 raw 한도를 넘으면 유료 호출 전에 실패한다. 이전 v1/v2 manifest와 `next_observation` 체크포인트를 재사용하지 않는다.

Remember 모델은 기존 `gpt-5.6-terra / low`를 유지했다. Recall·답변·채점은 Terra/medium, Dream은 Terra/high, Meditation은 Sol/high, 임베딩은 text-embedding-3-large다. 모델 설정을 바꾼 개선으로 해석하지 않는다.

원본: LongMemEval oracle, SHA-256 `821a2034d219ab45846873dd14c14f12cfe7776e73527a483f9dac095d38620c`. 최종 네 raw 아카이브가 두 사례의 원래 34개 발화를 포함하고 원문/SHA-256이 일치함을 별도 검토에서도 확인했다. 기존 v1 파일럿·운영 corpus·화면 fixture는 수정하지 않았다. 새 출력 폴더에 실제 API 결과를 보존했다.

## 실패도 포함한 수정 과정

| 실행 | 입력 | 결과와 발견 | 실제 비용 USD |
| --- | --- | --- | ---: |
| session-recall | ceb54acb 전체 세션 | observation 1개. 정답은 맞혔지만 앞의 “더 길다”를 나중 sexual compulsions 제안에 연결한 순서 오류 | 0.00668056 |
| session-recall-r2 | 같은 전체 세션 | 순서 오류는 고쳤으나 네 대안 전체 누락. 답변 유보, 채점 오답 | 0.00665783 |
| session-recall-r3 | 같은 전체 세션 | observation 3개에 제안 과정·네 대안 보존. 답변 정답 | 0.00906847 |
| session-integration | gpt4_f49edff3 세션 3개, r3 지침 | 10+10+12개의 D0. 문맥 의존·잠정 표현 강화 발견. Dream·Meditation도 정성 검토 | 3.92799663 |
| session-context-r4 | 두 사례, 최종 지침 | 단일 대화 1개, 세션 3개에서 6+6+4개. 두 답변 정답. 남은 정보 손실은 별도 기록 | 0.05857484 |

입력만 세션으로 바꾸거나 정답 채점을 통과한 것으로 종료하지 않았다. r3 통합 observation 32개를 모두 원문과 대조해 발화별 재서술에 가까운 결과를 발견했고, 사건·대화 교환을 먼저 복원한 후 추출하도록 지침을 다시 고쳤다. 이 수정에 쓴 두 사례는 미사용 평가 표본이 아니다.

## 최종 r4 실제 출력

`ceb54acb`: 네 발화 전체를 Source 하나로 입력하고 observation **1개**를 생성했다. 같은 기억에 앞선 보이지 않는 용어에 대한 길이 불만, sexual compulsions 제안, 더 나은 표현 요청, 네 대안과 각각의 의미가 함께 들어 있다. 사용자가 특정 용어를 최종 수용했다고 만들지 않았다. Recall은 이 기억 하나를 선택했고 답변에 네 용어를 모두 반환했다. 6개 API 호출, 0.00838878달러.

`gpt4_f49edff3`: 30개 발화를 세 번의 Remember 호출로 처리해 observation **16개**를 생성했다. 각 세션에서 6·6·4개, Source는 **3개**다. 예를 들어 직장동료의 아기 선물 바구니에 carrier/sling을 넣을지 묻는 질문, 부모 취향을 확인하라는 답변, 더 보편적인 물건으로 기울며 washcloth를 검토하는 후속 교환이 대상과 조건을 포함한다. sister의 가방 예산과 Zara 모델 제안도 함께 저장한다. 23개 API 호출, 0.05018606달러.

최종 두 실행은 Remember 조건이다. 생성된 17개 기억은 모두 depth 0이며 각각 root 하나를 가진다. 세션 수가 1개인 사례에서 depth 1이 생기지 않는 것이 정상이다. 최종 추출 지침으로 Dream·Meditation까지 다시 실행한 결과는 아니다.

정성 한계:

- 단일 대화 기억은 “actually longer”를 “too long”으로 바꾸고 도입부의 요청 순서를 압축했다. 더 길다는 비교가 너무 길다는 절대 판단으로 약간 강화됐다.

- 세션 3개 사례의 observation 7은 사촌의 Target 쇼핑과 직장동료 선물 계획을 구분하지만, 원문의 최근성 표현 “just”를 생략했다.
- observation 13은 폰 케이스 주문을 2월 20일 보고한 것으로 담으면서 원문의 “today”를 명시적으로 보존하지 않았다. 저장 시각과 사건 시각을 항상 동일하게 해석할 수는 없다.
- observation 12는 카드 문구를 잠정 표현으로 고쳤지만 직장동료의 아기 선물이라는 대상을 자체 본문에 다시 넣지 않았다.
- 초기의 일반 선물 추천과 가방 스타일·브랜드 설명 일부는 최종 기억에 남지 않았다. observation 4는 유축기 액세서리의 “모유 수유 중이라면”이라는 조건도 생략했다. 원문 충실도가 완전하다고 주장하지 않는다.

시간 질문의 실제 최종 답변은 nursery 준비 → 사촌 baby-shower 쇼핑 → 폰 케이스 주문 순서로 참조 답과 일치했다. 다만 사촌 쇼핑의 정확한 날짜는 원문에 없다. 2월 10일 보고의 “just”를 최근 사건으로 해석한 순서이며, 앞의 두 사건 사이 순서는 명시 날짜만으로 확정되지 않는다. 자동 정답 판정이 이 불확실성까지 검증한 것은 아니다.

## r3 통합 실행의 확정 집계와 중단

| 항목 | 실제 값 |
| --- | ---: |
| 원문 세션 / Source | 3 / 3 |
| Remember observation | 32 (10 + 10 + 12) |
| Dream에서 생성한 depth 1 | 19 |
| 첫 유효 주간 Meditation에서 생성한 depth 1 | 37 |
| 최종 depth 0 / depth 1 / depth 2 이상 | 32 / 56 / 0 |
| 양의 / 음의 관계 | 68 / 5 |
| 전체 API 호출 / 미정산 호출 | 206 / 0 |
| 통합 진단 비용 | 3.92799663달러 |
| 주간 Meditation 비용 (추론 19회 + 새 기억 임베딩) | 3.09476055달러 |
| 주간 Meditation 작업 | 19개 전부 완료 |
| 주간 제안 / 적용 / root 부족 거절 | 57 / 37 / 20 |
| 저장된 기억의 root·부모·depth 위반 | 0 |

이전 두 주는 depth 1 변경이 없어 Meditation 작업과 유료 호출이 없었다. 첫 유효 주간 실행은 원래 시계 기준 2023-02-26에 수행한 run 24다. 빈 제안이 한 번 있었고 5달러를 채우기 위한 추가 작업은 만들지 않았다.

주간 작업을 처리하는 동안 같은 근거에 대한 반복 추상이 충분히 확인됐다. 다음 주에도 이 출력을 다시 처리하는 유료 반복을 이어갈 필요가 없어 **첫 유효 주간 실행 완료 후 진단을 중단**했다. 중단 시 진행 중이거나 미정산인 API 호출은 0개였으며 모든 기억·원문·원장을 그대로 보존했다. 종료 확인 기록은 `TestResults/benchmark-session-integration-audit/diagnostic-stop.json`이다.

따라서 이 통합 진단은 **질문 날짜까지 완료한 benchmark가 아니다**. Recall·답변·채점은 실행하지 않았으며 정답률을 부여하지 않는다. 프로세스 종료 때문에 원래 benchmark 체크포인트는 `running`으로 남아 있다. 그 값을 성공으로 수정하지 않았고, 별도 감사 자료에 `diagnostic_stopped_after_completed_week`와 미완료 사실을 기록했다. 이후 `run`은 남은 날짜의 유료 작업을 계속하므로 결과 열람에는 `report` 또는 감사 자료를 사용한다.

세 번의 단일 세션 진단 0.02240686달러, r3 통합 진단 3.92799663달러, 최종 r4 두 사례 0.05857484달러를 합한 이번 작업의 실제 원장 비용은 **4.00897833달러**, API 호출은 **255회**, 미정산은 **0건**이다. 이전 v1 파일럿 비용은 이 합계에 포함하지 않는다. 이는 설정 단가로 계산한 API 원장 값이며 청구서 자체는 아니다.

## r3 통합 실행에서 확인한 문제

통합 실행의 입력은 세션 단위이며 추출 지침은 r3이다. r4의 최신 결과와 섞어 평가하지 않는다. 모든 D0와 생성된 상위 기억을 원문·부모·관계와 대조했다.

- r3 D0 I03의 “stores already mentioned”, I04의 생략된 baby blanket 대상, I20의 불명확한 “chosen card message”는 독립적으로 이해하기 어렵다. I15·I19는 “I think I will”과 예시 문구를 확정 선택처럼 강화했다. 이 문제를 r4 수정의 근거로 삼았다.
- Dream은 같은 부모 I10/I30/I14로 I34·I37을 만들었고, I12/I02/I24로 I38·I47을 만들었다. I12/I06/I22로 I41·I48·I50도 생성했다. 동일 문자열은 아니지만 내용은 상당 부분 반복된다.
- 관계의 방향이 저장됐다고 의미적 근거가 확보되지는 않는다. I07→I09 negative는 gym/playmat을 고민하다 gym 쪽으로 기운 진행을 모순처럼 취급한다. I13→I15 negative도 carrier를 검토하다 보편적 대안으로 기우는 진행이다. I24→I21 positive는 sister용 추천을 친구의 폰 케이스 주문을 뒷받침하는 근거처럼 연결한다.
- Meditation은 계획·구매·반응의 차이, assistant의 미검증 상품 설명, Zara의 일반 가격대와 특정 모델 가격의 차이를 짚었다. 그러나 같은 구분을 여러 번 재서술하는 중복도 있다. 특히 “positively received” 같은 표현은 물리적 배송·수령까지 입증하지 않는다.
- Source가 세 개뿐이므로 depth 2에 필요한 서로 다른 root 9개를 확보할 수 없다. depth를 높이기 위해 입력을 잘게 쪼개거나 제약을 낮추지 않는다.

중복 생성 방지와 관계의 주장·근거 판정은 남은 문제다. 이번 변경은 입력 맥락과 observation 추출을 고쳤으며 이 두 문제까지 해결했다고 보고하지 않는다.

## 보존 위치와 검증

- 최종 모델 결과: `TestResults/benchmark-session-context-r4/`의 manifest, corpus, graph, evidence, result, report.
- 최종 모델 실행 바이너리·추출 지침: `TestResults/benchmark-session-runtime-r4/`.
- r3 통합 진단: `TestResults/benchmark-session-integration/`; 해당 구현 보관: `TestResults/benchmark-session-runtime-v3/`.
- 전체 통합 원문·기억·관계 목록: `TestResults/benchmark-session-integration-audit/memory-review.md`.
- 독립 원문 및 r3 D0 전수 대조: `TestResults/benchmark-session-integration-source-review.md`.
- 독립 r4 검토: `TestResults/benchmark-session-context-r4-review.md`.

저장된 실험의 결과를 확인하려면 보관한 바이너리와 원래 설정을 사용한다. r4는 두 문항 모두 완료했고 r3 통합 진단은 위 지점에서 중단했다. 다른 빌드로 재개하려 하면 manifest가 거부할 수 있다. `report`는 API를 호출하지 않는다.

```powershell
dotnet TestResults/benchmark-session-runtime-r4/LongJourney.Benchmarks.dll report benchmarks/session-context-pilot.json
dotnet TestResults/benchmark-session-runtime-v3/LongJourney.Benchmarks.dll report benchmarks/session-integration-pilot.json
```

입력·체크포인트·중복 root·실제 API 전달 계약을 포함한 전체 테스트 228개가 통과했다. 이후 r4는 추출 프롬프트 문구만 수정했으며 별도 빌드 경고·오류 0개, 관련 OpenAI 테스트 32개 통과와 실제 모델 출력으로 확인했다. MCP raw 설명도 개별 관찰 크기에서 대화 맥락을 보존하는 입력으로 맞췄다. 가짜 API를 쓰는 테스트의 성공과 실제 기억의 의미 품질은 구분한다.
